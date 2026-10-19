"""Offline regressions for release spec preservation and deployment outcomes."""

import copy
import io
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import urllib.error

import deploy


APP_ID = "01d3cf77-d726-443d-813f-2b08e26db2ef"
DEPLOYMENT_ID = "00000000-0000-0000-0000-000000000001"


def fixture():
    spec = {
        "name": "jerrychart",
        "region": "nyc",
        "envs": [{"key": "SECRET", "type": "SECRET", "value": "EV[encrypted-value]"}],
        "databases": [{"name": "database", "production": True}],
        "ingress": {"rules": [{"component": {"name": "jerrychart-web"}}]},
        "services": [],
        "workers": [],
    }
    for kind, names in deploy.TARGETS.items():
        for name in names:
            spec[kind].append({
                "name": name,
                "image": {"registry_type": "DOCR", "registry": "idunno-registry",
                          "repository": name, "tag": "old", "deploy_on_push": {"enabled": False}},
                "instance_count": 1,
                "run_command": "preserve startup command",
                "envs": [{"key": "BINDING", "value": "${database.DATABASE_URL}"}],
                "internal_ports": [8080],
                "health_check": {"http_path": "/health"},
            })
    spec["services"].append({"name": "unrelated", "image": {"tag": "untouched"}})
    return {"spec": spec}


class DeploymentTests(unittest.TestCase):
    def test_only_image_tags_change_and_input_is_untouched(self):
        app = fixture()
        original = copy.deepcopy(app)
        updated = deploy.updated_spec(app["spec"], "new")
        for kind, names in deploy.TARGETS.items():
            for component in updated[kind]:
                if component["name"] in names:
                    self.assertEqual(component["image"]["tag"], "new")
                    component["image"]["tag"] = "old"
        self.assertEqual(updated, app["spec"])
        self.assertEqual(original, app)

    def test_digest_is_replaced_by_release_tag(self):
        spec = fixture()["spec"]
        spec["services"][0]["image"]["digest"] = "sha256:old"
        self.assertNotIn("digest", deploy.updated_spec(spec, "new")["services"][0]["image"])

    def test_documented_docr_registry_forms_preserve_all_other_settings(self):
        for registry in ("", None, "idunno-registry"):
            with self.subTest(registry=registry):
                spec = fixture()["spec"]
                for kind, names in deploy.TARGETS.items():
                    for component in spec[kind]:
                        if component["name"] in names:
                            if registry is None:
                                component["image"].pop("registry")
                            else:
                                component["image"]["registry"] = registry
                expected = copy.deepcopy(spec)
                for kind, names in deploy.TARGETS.items():
                    for component in expected[kind]:
                        if component["name"] in names:
                            component["image"]["tag"] = "new"
                original = copy.deepcopy(spec)
                self.assertEqual(expected, deploy.updated_spec(spec, "new"))
                self.assertEqual(original, spec)

    def test_unexpected_sources_and_components_fail_closed(self):
        for mutation in ("app", "missing", "duplicate", "registry", "repository", "type", "git"):
            with self.subTest(mutation=mutation):
                spec = fixture()["spec"]
                component = spec["services"][0]
                if mutation == "app":
                    spec["name"] = "other"
                elif mutation == "missing":
                    spec["workers"] = []
                elif mutation == "duplicate":
                    spec["services"].append(copy.deepcopy(component))
                elif mutation == "git":
                    component["github"] = {"repo": "unexpected"}
                else:
                    key = "registry_type" if mutation == "type" else mutation
                    component["image"][key] = "unexpected"
                with self.assertRaises(deploy.DeploymentError):
                    deploy.updated_spec(spec, "new")

    def run_deploy(self, phases, changed=False):
        app = fixture()
        calls = []
        spec = deploy.updated_spec(app["spec"], "new")

        def request(method, path, body=None):
            calls.append((method, path, body))
            if method == "PUT":
                self.assertEqual(body, {"spec": spec})
                return {"app": {"pending_deployment": {"id": DEPLOYMENT_ID}}}
            if "/deployments/" in path:
                return {"deployment": {"phase": next(phases), "spec": spec,
                                       "progress": {"error_steps": 2}}}
            if changed and len(calls) == 2:
                return {"app": {**app, "updated_at": "changed"}}
            return {"app": app}

        return deploy.deploy(APP_ID, "new", request, sleep=lambda _: None), calls

    def test_waits_for_exact_deployment_and_records_only_safe_metadata(self):
        output = io.StringIO()
        with patch("sys.stdout", output):
            result, calls = self.run_deploy(iter(["PENDING_BUILD", "DEPLOYING", "ACTIVE"]))
        self.assertEqual(result, DEPLOYMENT_ID)
        self.assertEqual([call[0] for call in calls], ["GET", "GET", "PUT", "GET", "GET", "GET"])
        self.assertTrue(all(call[1].endswith(DEPLOYMENT_ID) for call in calls[3:]))
        self.assertNotIn("encrypted-value", output.getvalue())

    def test_failed_canceled_superseded_or_unknown_deployments_fail(self):
        for phase in ("ERROR", "CANCELED", "SUPERSEDED", "UNKNOWN", "secret-remote-value"):
            with self.subTest(phase=phase), self.assertRaises(deploy.DeploymentError) as error:
                self.run_deploy(iter([phase]))
            self.assertNotIn("secret-remote-value", str(error.exception))

    def test_concurrent_spec_change_is_not_overwritten(self):
        with self.assertRaisesRegex(deploy.DeploymentError, "stale update"):
            self.run_deploy(iter([]), changed=True)

    def test_update_response_deployment_selection_and_safe_failure(self):
        spec = deploy.updated_spec(fixture()["spec"], "new")
        associated = {"id": DEPLOYMENT_ID, "spec": spec}
        cases = [
            ({"pending_deployment": associated}, True),
            ({"in_progress_deployment": associated}, True),
            ({"pending_deployment": None, "in_progress_deployment": associated}, True),
            ({"pending_deployment": associated, "in_progress_deployment": {"id": "unrelated"}}, True),
            ({}, False),
            ({"pending_deployment": None, "in_progress_deployment": None}, False),
            ({"in_progress_deployment": {"id": DEPLOYMENT_ID}}, False),
            ({"in_progress_deployment": {"id": DEPLOYMENT_ID, "spec": fixture()["spec"]}}, False),
            ({"pending_deployment": {"id": "invalid"}}, False),
            ({"pending_deployment": {"spec": spec}}, False),
        ]
        for response, success in cases:
            with self.subTest(response=response), patch("sys.stdout", io.StringIO()):
                calls = []

                def request(method, path, body=None):
                    calls.append((method, path))
                    if method == "PUT":
                        return {"app": response}
                    if "/deployments/" in path:
                        return {"deployment": {"phase": "ACTIVE", "spec": spec}}
                    return {"app": fixture()}

                if success:
                    self.assertEqual(deploy.deploy(APP_ID, "new", request), DEPLOYMENT_ID)
                    self.assertEqual(calls[-1][1], f"/apps/{APP_ID}/deployments/{DEPLOYMENT_ID}")
                else:
                    with self.assertRaisesRegex(deploy.DeploymentError, "Inspect App Platform Activity before retrying"):
                        deploy.deploy(APP_ID, "new", request)
                    self.assertFalse(any("/deployments/" in path for _, path in calls))
                self.assertEqual(sum(method == "PUT" for method, _ in calls), 1)

    def test_running_or_pinned_deployment_is_not_overwritten(self):
        for key in ("pending_deployment", "in_progress_deployment", "pinned_deployment"):
            app = fixture()
            app[key] = {"phase": "DEPLOYING"}
            with self.subTest(key=key), self.assertRaises(deploy.DeploymentError):
                deploy.deploy(APP_ID, "new", lambda *_: {"app": app})

    def test_already_active_release_does_not_redeploy(self):
        app = fixture()
        app["spec"] = deploy.updated_spec(app["spec"], "new")
        app["active_deployment"] = {"id": DEPLOYMENT_ID, "phase": "ACTIVE", "spec": app["spec"]}
        with patch("sys.stdout", io.StringIO()):
            self.assertEqual(deploy.deploy(APP_ID, "new", lambda *_: {"app": app}), DEPLOYMENT_ID)

    def test_timeout_is_failure_not_success(self):
        times = iter([0, 1801])
        app = fixture()

        def request(method, _path, _body=None):
            if method == "PUT":
                return {"app": {"pending_deployment": {"id": DEPLOYMENT_ID}}}
            return {"app": app}

        with patch("sys.stdout", io.StringIO()), self.assertRaisesRegex(deploy.DeploymentError, "Timed out"):
            deploy.deploy(APP_ID, "new", request, clock=lambda: next(times))

    def test_active_deployment_must_match_submitted_spec(self):
        app = fixture()

        def request(method, path, body=None):
            if method == "PUT":
                return {"app": {"pending_deployment": {"id": DEPLOYMENT_ID}}}
            if "/deployments/" in path:
                return {"deployment": {"phase": "ACTIVE", "spec": app["spec"]}}
            return {"app": app}

        with patch("sys.stdout", io.StringIO()), self.assertRaisesRegex(deploy.DeploymentError, "does not match"):
            deploy.deploy(APP_ID, "new", request)

    def test_api_error_does_not_log_response_body(self):
        error = urllib.error.HTTPError("https://api.digitalocean.com", 403, "sensitive message",
                                       {}, io.BytesIO(b"sensitive response body"))
        with patch("urllib.request.OpenerDirector.open", side_effect=error):
            with self.assertRaisesRegex(deploy.DeploymentError, r"HTTP 403") as raised:
                deploy.api_request("sensitive token", "GET", f"/apps/{APP_ID}")
        self.assertNotIn("sensitive", str(raised.exception))

    def test_redirect_does_not_forward_authorization(self):
        with self.assertRaisesRegex(deploy.DeploymentError, "redirected"):
            deploy.RejectRedirects().redirect_request(None, None, 302, "", {}, "https://unexpected.example")

    def test_main_uses_original_artifact_tag_on_deploy_retry(self):
        with tempfile.TemporaryDirectory() as directory:
            summary = Path(directory) / "summary.md"
            env = {"DIGITALOCEAN_ACCESS_TOKEN": "secret", "DIGITALOCEAN_APP_ID": APP_ID,
                   "GITHUB_SHA": "a" * 40, "GITHUB_RUN_ATTEMPT": "2", "GITHUB_STEP_SUMMARY": str(summary)}
            tag = "a" * 40 + "-123-1"
            with patch.dict(os.environ, env), patch.object(Path, "read_text", return_value=tag + "\n"):
                with patch.object(deploy, "deploy", return_value=DEPLOYMENT_ID) as run:
                    deploy.main()
            self.assertEqual(run.call_args.args[:2], (APP_ID, tag))
            self.assertIn("Status: **ACTIVE**", summary.read_text())
            self.assertIn(tag, summary.read_text())
            self.assertNotIn("secret", summary.read_text())

    def test_mismatched_artifact_commit_is_rejected_before_api_access(self):
        env = {"DIGITALOCEAN_ACCESS_TOKEN": "secret", "DIGITALOCEAN_APP_ID": APP_ID, "GITHUB_SHA": "a" * 40}
        for tag in ("b" * 40 + "-123-1", "latest", "a" * 40 + "-123-1\ninjected"):
            with self.subTest(tag=tag), patch.dict(os.environ, env):
                with patch.object(Path, "read_text", return_value=tag), patch.object(deploy, "api_request") as request:
                    with self.assertRaisesRegex(deploy.DeploymentError, "does not match"):
                        deploy.main()
                    request.assert_not_called()


if __name__ == "__main__":
    unittest.main()
