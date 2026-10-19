"""Update only JerryChart image tags in the existing DigitalOcean app spec."""

import copy
import json
import os
from pathlib import Path
import re
import sys
import time
import urllib.error
import urllib.request
import uuid


TARGETS = {
    "services": ("jerrychart-api", "jerrychart-web"),
    "workers": ("jerrychart-monitor",),
}
WAITING_PHASES = {"PENDING_BUILD", "BUILDING", "PENDING_DEPLOY", "DEPLOYING"}


class DeploymentError(Exception):
    """A deployment failure whose message is safe to write to workflow logs."""


class RejectRedirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise DeploymentError("DigitalOcean API unexpectedly redirected the request.")


def api_request(token, method, path, body=None):
    request = urllib.request.Request(
        f"https://api.digitalocean.com/v2{path}",
        data=json.dumps(body).encode() if body is not None else None,
        headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"},
        method=method,
    )
    try:
        with urllib.request.build_opener(RejectRedirects).open(request, timeout=30) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        # Response bodies and app specs can contain secrets. Never echo them.
        status = error.code
        error.close()
        raise DeploymentError(f"DigitalOcean API {method} failed (HTTP {status}).") from None
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError):
        raise DeploymentError(f"DigitalOcean API {method} failed; inspect App Platform before retrying.") from None


def updated_spec(spec, tag):
    result = copy.deepcopy(spec)
    if result.get("name") != "jerrychart":
        raise DeploymentError("The configured app is not jerrychart; refusing to update it.")
    for kind, names in TARGETS.items():
        for name in names:
            matches = [item for item in result.get(kind, []) if item.get("name") == name]
            if len(matches) != 1:
                raise DeploymentError(f"Expected exactly one {kind} component named {name}.")
            component = matches[0]
            image = component.get("image", {})
            if (image.get("registry_type") != "DOCR" or
                    image.get("registry", "") not in ("", "idunno-registry") or
                    image.get("repository") != name or
                    any(source in component for source in ("github", "gitlab", "git", "bitbucket"))):
                raise DeploymentError(f"Unexpected image source for {name}; refusing to replace it.")
            image["tag"] = tag
            image.pop("digest", None)
    return result


def require_idle(app):
    for key in ("pending_deployment", "in_progress_deployment"):
        deployment = app.get(key)
        if deployment and deployment.get("phase") not in {"ACTIVE", "ERROR", "CANCELED", "SUPERSEDED"}:
            raise DeploymentError("Another app deployment is pending or running; wait before releasing.")
    if app.get("pinned_deployment"):
        raise DeploymentError("The app is pinned to a deployment; unpin it before releasing.")


def deploy(app_id, tag, request, sleep=time.sleep, clock=time.monotonic):
    path = f"/apps/{app_id}"
    app = request("GET", path)["app"]
    require_idle(app)
    spec = updated_spec(app["spec"], tag)
    active = app.get("active_deployment") or {}
    if app["spec"] == spec and active.get("phase") == "ACTIVE" and active.get("spec") == spec:
        print("The requested release is already active.")
        return active["id"]

    # Detect configuration edits while preparing the update. The API has no
    # conditional app-spec update; operators must avoid edits during this job.
    latest = request("GET", path)["app"]
    require_idle(latest)
    if latest != app:
        raise DeploymentError("The app changed while preparing deployment; refusing a stale update.")
    response = request("PUT", path, {"spec": spec})["app"]
    deployment = response.get("pending_deployment")
    if not deployment:
        deployment = response.get("in_progress_deployment")
        if not deployment or deployment.get("spec") != spec:
            raise DeploymentError(
                "App update was accepted but no associated deployment could be identified. "
                "Inspect App Platform Activity before retrying; no second update was submitted."
            )
    try:
        deployment_id = str(uuid.UUID(deployment["id"]))
    except (KeyError, ValueError, TypeError, AttributeError):
        raise DeploymentError(
            "App update was accepted but its deployment ID was missing or invalid. "
            "Inspect App Platform Activity before retrying; no second update was submitted."
        ) from None
    print(f"App {app_id}, deployment {deployment_id}.")
    deadline = clock() + 30 * 60
    while clock() < deadline:
        deployment = request("GET", f"{path}/deployments/{deployment_id}")["deployment"]
        phase = deployment.get("phase")
        if phase not in WAITING_PHASES | {"ACTIVE"}:
            # Log only known phase names and numeric counts, not remote messages.
            safe_phase = phase if phase in {"ERROR", "CANCELED", "SUPERSEDED", "UNKNOWN"} else "UNKNOWN"
            progress = deployment.get("progress") or {}
            errors = progress.get("error_steps", 0)
            errors = errors if type(errors) is int else 0
            raise DeploymentError(
                f"Deployment {deployment_id} ended in {safe_phase} ({errors} error steps). "
                "Inspect its Activity details in App Platform; no automatic rollback was attempted."
            )
        print(f"Deployment {deployment_id}: {phase}.")
        if phase == "ACTIVE":
            if deployment.get("spec") != spec:
                raise DeploymentError("Active deployment does not match the submitted release spec.")
            return deployment_id
        sleep(10)
    raise DeploymentError(
        f"Timed out waiting for deployment {deployment_id}. It may still be running; "
        "inspect App Platform before retrying."
    )


def main():
    token = os.environ.get("DIGITALOCEAN_ACCESS_TOKEN")
    app_id = os.environ.get("DIGITALOCEAN_APP_ID", "")
    if not token:
        raise DeploymentError("Set the production environment secret DIGITALOCEAN_ACCESS_TOKEN.")
    try:
        app_id = str(uuid.UUID(app_id))
    except ValueError:
        raise DeploymentError("Set production environment variable DIGITALOCEAN_APP_ID to the app UUID.") from None
    tag = Path("release-tag.txt").read_text(encoding="utf-8").strip()
    if not re.fullmatch(re.escape(os.environ["GITHUB_SHA"]) + r"-[0-9]+-[0-9]+", tag):
        raise DeploymentError("Published release tag does not match this workflow's commit.")
    deployment_id = deploy(app_id, tag, lambda method, path, body=None: api_request(token, method, path, body))
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as summary:
        summary.write(f"\n### Production deployment\n\nApp: `{app_id}`\n\n"
                      f"Deployment: `{deployment_id}`\n\nImage tag: `{tag}`\n\nStatus: **ACTIVE**\n")


if __name__ == "__main__":
    try:
        main()
    except DeploymentError as error:
        print(f"::error::{error}", file=sys.stderr)
        sys.exit(1)
