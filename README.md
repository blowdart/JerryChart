# JerryChart

**Jerry No** tracks Bluesky's collective failure to make Jerry Chen reconsider
his choices. It records replies containing "Jerry no", distinguishes replies
to the correct Jerry from everyone else, and displays counts, user rankings,
and monthly charts.

Built with .NET 10, Aspire 13.6, MySQL 8.4, and Next.js with shadcn/ui and
Tailwind CSS. Historical replay resumes from durable checkpoints before
switching to live Jetstream monitoring. Statistics reflect data recorded so
far, not necessarily complete historical coverage.

| Project | Role |
| --- | --- |
| `JerryChart.AppHost` | Orchestrates MySQL, the API, CLI, and frontend |
| `JerryChart.Api` | Serves Jerry no statistics through HTTP; Aspire resource `api` |
| `JerryChart.Monitor` | Replays Jetstream, records matching replies and durable progress in MySQL; Aspire resource `monitor` |
| `JerryChart.Data` | Shared MySQL connection registration and parameterized queries |
| `JerryChart.ServiceDefaults` | Aspire telemetry, discovery, and health checks |
| `jerrychart-web` | Next.js App Router frontend with shadcn/ui components |
| `JerryChart.Tests` | MSTest tests running on Microsoft.Testing.Platform |

## Minimum requirements

| Requirement | Minimum / supported setup | Installation |
| --- | --- | --- |
| .NET SDK | **10.0.100**, or a later stable .NET 10 feature band selected by `global.json`; the runtime alone is insufficient | [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Node.js and npm | **Node 26.x**, matching CI and the frontend's Node type definitions | [Node.js](https://nodejs.org/en/download) (npm is included) |
| Container engine | Docker running **Linux containers**, able to pull and run `mysql:8.4` | [Docker Desktop](https://docs.docker.com/desktop/) on Windows/macOS, or [Docker Engine](https://docs.docker.com/engine/install/) on Linux |
| Git | Required to clone the repository | [Git](https://git-scm.com/downloads) |
| Aspire CLI | **13.6+**, only required for the `aspire run` option | [Aspire CLI installation](https://aspire.dev/get-started/install-cli/) |
| Jetstream archive API key | Required for the monitor's historical replay | [Bluesky Protocol Services account](https://bsky.network/account#api-keys-section-heading) |

On Windows, install the main tools with PowerShell and WinGet:

```powershell
winget install --id Microsoft.DotNet.SDK.10 --exact
winget install --id OpenJS.NodeJS.LTS --exact
winget install --id Docker.DockerDesktop --exact
winget install --id Git.Git --exact
```

Restart your terminal afterwards. Start Docker Desktop and select Linux
containers; on Windows, complete Docker's WSL 2 setup if prompted. On macOS or
Linux, use the installers linked above for your platform. MySQL does **not**
need to be installed separately: Aspire starts it in Docker.

For `aspire run`, install the CLI after installing .NET:

```powershell
dotnet tool install --global Aspire.Cli --version 13.6.0
```

If already installed, use `dotnet tool update --global Aspire.Cli --version
13.6.0`, or keep a compatible newer version. No separate Aspire workload is
required. Verify the tools before starting:

```powershell
dotnet --version
node --version
npm --version
docker info
aspire --version
```

The last command is optional if you will use only `dotnet run`.

## First-time setup

Clone this repository and open a terminal in the directory containing
`JerryChart.sln`. The following examples use PowerShell; the `dotnet` and
`aspire` commands also work in other shells.

### Get and configure a Jetstream key

1. Open the [Bluesky Protocol Services account page](https://bsky.network/account#api-keys-section-heading) and sign in or create an account.
2. In the API keys section, create a key for accessing the Jetstream archive and copy it. This is **not** a Bluesky app password.
3. Check the service's current archive access, pricing, and bandwidth quota before starting a full historical replay.

Archive downloads are metered. The first replay starts at sequence zero and
can take substantial time and consume quota. The key is required even though
the later live stream is public.

Store both the archive key and a strong MySQL password in **AppHost user
secrets**, from the repository root:

```powershell
dotnet user-secrets set "Parameters:jetstream-api-key" "<your archive API key>" --project JerryChart.AppHost
dotnet user-secrets set "Parameters:mysql-password" "<your MySQL password>" --project JerryChart.AppHost
```

Replace the placeholders with your actual values. Both run methods below load
these same secrets from the AppHost project. AppHost passes the archive key to
the monitor as `Jetstream__ApiKey` and supplies database connection strings to
the API and monitor automatically. Setting only `Jetstream__ApiKey` in the
parent shell does not configure the AppHost's `jetstream-api-key` parameter.

User secrets are development storage, **not encrypted storage**. Never commit
keys, passwords, or connection strings. Commands containing literal secrets
may also remain in shell history. As an alternative to persistent user secrets,
configure AppHost parameters in the current PowerShell session using the exact
parameter names, including hyphens:

```powershell
[Environment]::SetEnvironmentVariable("Parameters__jetstream-api-key", "<your archive API key>", "Process")
[Environment]::SetEnvironmentVariable("Parameters__mysql-password", "<your MySQL password>", "Process")
```

These process-scoped settings apply to commands launched from that shell and
override user secrets. For deployed environments, use your platform's secret
store instead.

### Trust the development certificate

```powershell
dotnet dev-certs https --trust
```

Follow any platform-specific certificate trust instructions printed by .NET.
An HTTP-only development launch is also available below.

## Run the complete application

Run **one** of these commands from the repository root, not both simultaneously.
Both start the same AppHost and the complete application.

### Using Aspire CLI

```powershell
aspire run --apphost JerryChart.AppHost\JerryChart.AppHost.csproj
```

### Using dotnet

```powershell
dotnet run --project JerryChart.AppHost
```

A global Aspire CLI installation is **not required** for this option.

Open the dashboard URL printed by Aspire, then open the **web** endpoint.
The API appears in the dashboard as **api**, and the command-line app as **monitor**.
Aspire runs `npm ci` automatically, starts MySQL, injects connection strings
into both .NET apps, and supplies the API URL to Next.js as `API_BASE_URL`.
The API waits for MySQL readiness and initializes the shared monitoring schema;
the frontend waits for API health. The homepage displays the all-time Jerry no
reply summary, fetched server-side from the API.
The monitor runs continuously, replaying the archive from sequence zero on its
first run and then tailing live posts. On restart it resumes its MySQL progress.
The monitor logs `Listening to live Jetstream after sequence ...` when archive
replay finishes and live monitoring starts. Until then, historical counts can
be incomplete. Use Ctrl+C in the launching terminal to stop the application.

Next.js calls the
API server-side, so no browser CORS
configuration or database credentials in client code are needed.

### Processing activity

The homepage shows **both** Jetstream historical replay/live monitoring and the
parent-URI backfill (daily scheduled or manual), independently of chart refreshes. The uncached
`GET /statistics/processing-status` endpoint (and the browser's same-origin
`/api/statistics/processing-status` proxy) reports recorded phases, UTC start,
phase-change, heartbeat and finish times, the last checkpoint write time, and
an optional approximate archive duration based on recent forward progress
(see [Approximate archive time remaining](#approximate-archive-time-remaining)).
It does not expose run ownership tokens, checkpoint JSON, credentials, or an
invented historical completion percentage.

Workers record activity only after owning their separate resource advisory
locks. `ProcessingActivity` is initialized idempotently by the API or workers,
with shared schema initialization serialized independently of resource locks.
Run IDs fence all subsequent writes so a previous process cannot stop or revive
a newer owner's status. Checkpoints are **not** heartbeats.

An independent, awaited task writes a heartbeat every **15 seconds**, even
during archive quota/network waits, parent-URI rate-limit/retry waits, and idle
live monitoring. Heartbeats use separate pooled connections, verify the resource
lock is still owned, and never share the replay/backfill connection. Requests
are serial within the heartbeat loop and bounded to five seconds. Heartbeat
errors are logged and do not advance the timestamp or discard cursor progress.
A lost database connection requires a new lock acquisition; the old run stops
heartbeating during reconnect waits.

Active phases (`archive`, `live`, `backfill-running`, `retrying`) become
**stale**, not running, when the last successful heartbeat is **60 seconds**
old. Stale means activity is unknown, not a confirmed crash. Graceful cancellation
records `stopped`; normal backfill exhaustion records `completed`; unrecoverable
attempt errors record `failure`. Database reconnect attempts record `retrying`
when possible. If even the final status write fails, it is logged and freshness
expires naturally. Terminal phases remain terminal rather than becoming stale.
No recorded run means `not-started` / unknown, even with existing checkpoints
or a fully resolved legacy queue.
Already-running older worker builds do not publish this status; they remain
unknown until started normally with the updated build. Deploy/restart the API
and workers through your usual process; the website does not restart them.

Parent-URI counts cover **all Hits**, not just right-Jerry replies: pending
means unattempted (status 0); retryable means status 3, with a separate due count;
resolved (1) includes new hits with known parent URIs; unavailable (2) is terminal
and not retried. No outstanding rows describes the queue, not proof that a
backfill ever ran. A completed run does not imply future rows cannot arrive.

The browser polls **only status**, every **30 seconds while visible** (after
the preceding request completes), refreshes immediately when revisible, and
cancels requests on hide/unmount. Under normal response times an abnormal stop
is detected within about **90 seconds** of the last heartbeat (60-second TTL
plus up to 30 seconds until the next poll). Slow requests, unavailable databases,
and hidden tabs can delay observation; fetch errors are explicitly shown and
retained active snapshots age into stale locally. The existing manual statistics
Refresh still has its **150-second cooldown**, refreshes server-provided status
too, and is not invoked by heartbeat polling.

MySQL data is retained in an Aspire-managed Docker volume between runs. The
`mysql-password` secret parameter supplies the container's root password. Aspire
includes that password in the connection strings supplied to both .NET apps;
do not commit credentials. For an existing initialized volume, configure its
current password: changing the parameter does not change MySQL's stored password.
Rotate the database password explicitly before changing the parameter; do not
delete the volume to change a password, as it contains hits and replay progress.
The shared schema removes the obsolete `notes` table at startup; hits, actors,
refresh jobs, and replay checkpoints are preserved.

For an HTTP-only dashboard, use either:

```powershell
aspire run --apphost JerryChart.AppHost\JerryChart.AppHost.csproj --launch-profile http
# Or:
dotnet run --project JerryChart.AppHost --launch-profile http
```

The API and Next.js use HTTP locally; this is a
development scaffold without authentication. Add authentication, least-privilege
database users, a reviewed migration/deployment process, and deployment HTTPS
before exposing it publicly.

### Common startup problems

| Symptom | Check |
| --- | --- |
| Missing parameter / archive key | Set `Parameters:jetstream-api-key` on **JerryChart.AppHost**, not on the API or monitor project. |
| MySQL access denied after changing the password | A persisted volume retains its original password; changing the secret does not rotate it. Restore the correct secret or rotate the database password explicitly. |
| Container startup fails | Start Docker, confirm `docker info` succeeds, select Linux containers, and check access to the `mysql:8.4` image. |
| HTTPS certificate error | Trust the development certificate or use the `http` launch profile. |
| NuGet restore fails for idunno packages | Check access to the MyGet feed configured in `nuget.config`; these are pinned prerelease packages. |
| Only a few users or replies appear | Check monitor logs for ongoing archive replay, retries, or quota waits; the website shows recorded data, not archive completion. |

## Jetstream monitor

Monitor diagnostics use compile-time `LoggerMessage` source generation, with
stable event IDs and structured fields for sequences, reply identities, and
retry delays.

The monitor registers the SDK's Jetstream, AT Protocol HTTP client, and PLC
directory metrics using `AddAtProtoJetstreamMetrics()`,
`AddAtProtoHttpClientMetrics()`, and `AddAtProtoDirectoryMetrics()`, exporting
measurements through the shared OpenTelemetry configuration to the Aspire
dashboard. Each meter produces measurements when its corresponding SDK
component is used.

The monitor uses `idunno.AtProto` and `idunno.Bluesky`
**8.0.0-prerelease.g7b3b498381**, following the documentation and sample on the
[version/v8.0.0 branch](https://github.com/blowdart/idunno.Bluesky/blob/version/v8.0.0/docs/docs/jetstreamReplay.md).
It replays `app.bsky.feed.post` commits for all authors and consumes post commits
and identity events in the live tail. A hit must be a reply and
contain the whole words `Jerry` and `no`, case-insensitively, separated by
whitespace and/or Unicode punctuation. Examples: `Jerry no`, `JERRY, NO!`,
`Jerry...no`. `Jerryno`, `Jerry nobody`, and `Jerry says no` do not match.
Create, update, and sync-backfill records can produce hits; deletes do not
remove historical hits. Malformed matching records are logged and skipped,
never given an invented creation date or author.

`JerryChart.Monitor\Schema.sql` is embedded in the shared data library and applied
idempotently by the API and monitor at startup. The API does not acquire the
monitor's replay cursor lock:

| Table | Contents |
| --- | --- |
| `Actor` | `Did` primary key, nullable `Handle`, `UpdatedAt` initialized on creation and refreshed when handle data is written |
| `ActorRefresh` | One durable refresh job per tracked DID, revision for race-safe invalidation, indexed next-attempt time |
| `MonitorSchemaMigration` | Durable markers for one-time monitor data migrations |
| `Hits` | `CreatedAt` (UTC), `AtUri`, `AuthorDid`, `ParentAuthorDid`, nullable `ParentAtUri` and its hash; indexed durable parent-URI backfill status |
| `JetstreamReplayProgress` | Complete archive checkpoint or live sequence, service, fallback starting sequence, and update time |
| `ProcessingActivity` | Separate monitor/backfill run ownership, recorded phase, UTC heartbeat and lifecycle timestamps |
| `StatisticsUpdate` | UTC ingestion time of the last new hit, committed atomically with that hit |

`ParentAuthorDid` and the immediate parent's full AT URI come from
`reply.parent.uri`, not the thread root. The parent URI is stored with a SHA-256
index so hits can be grouped by parent post without indexing the full 8192-byte URI.
Hits are unique by the SHA-256 hash of their full AT URI (stored alongside the
URI), allowing full-length AT URIs without MySQL's index-length limitation.
Each hit transaction inserts missing `Actor` rows for both author DIDs before
inserting the hit. Existing actors and their identity metadata are left unchanged,
including on replay. Unknown handles remain null. A one-time transactional
migration seeds missing actors from existing hits and seeds missing refresh jobs
without resetting existing jobs or rescanning historical hits on every reconnect.
`UpdatedAt` starts at row creation time and is refreshed when handle data is
written (including a successful lookup returning the same handle, or clearing
an obsolete handle). Duplicate hits do not change it.
Replays preserve the first hit for each URI. Hits are committed before advancing
progress; a crash between those writes causes safe, idempotent redelivery.
Archive checkpoints retain the sealed tip, request fingerprint, segment checksum,
block index, and byte offset. Live cursors advance only after processing succeeds.
A database advisory lock prevents multiple monitors from overwriting one cursor.
The monitor refuses to reuse progress from a different Jetstream service.

To enforce monitor-controlled reconnect delays, it composes the SDK's
`SnapshotAsync` and `StreamAsync` rather than `ReplayAsync`'s fixed-delay retries.
The archive client handles advertised bandwidth quotas and HTTP 429
`Retry-After` instructions; those server-directed waits are not shortened to
the reconnect cap. Archive requests are sequential, and live consumption uses
one parser with bounded buffering. An expired live cursor returns to the archive
after the last durably processed sequence, rather than jumping to the live tip.

The snapshot's `onArchiveError` callback explicitly skips invalid records when
a sequence is available, or invalid blocks when decoding fails before rows are
available. Each skip logs a warning with the exception and, for records, its
sequence. Skips intentionally omit data from statistics, allowing replay and
durable checkpointing to continue rather than repeatedly failing on the same
input. Invalid frame lengths, truncated downloads, and transport failures still
fail the attempt and use the normal reconnect/retry policy.

Failures are logged and retried indefinitely with delays of **1, 5, 15, 30, 90,
150, then 300 seconds**, remaining at 300 seconds for further consecutive failures.
Successfully processed events reset the schedule. Database outages and server
refusals are retried too. Authentication/configuration faults require operator
correction, but do not make an already-running monitor give up. Ctrl+C or Aspire
shutdown cancels replay, quota waits, and reconnect waits.

### Approximate archive time remaining

The processing status shows approximate time remaining for the **pinned archive
snapshot**, not the continually moving live stream. The monitor samples forward
sequence progress as events are processed, using up to ten minutes of
history and requiring at least two minutes of samples. Remaining sequence
distance divided by recent sequence advancement per second gives the estimate.
Sequence distance is a proxy for work, **not a count of remaining events**:
segment density, download speed, bandwidth quotas, and replanning can change
the actual duration substantially.

Each replay attempt builds a fresh rate window. A persisted high-water sequence
excludes redelivery after recovery, and ten minutes without forward progress resets
the window. Repeated checkpoint callbacks retain a still-fresh estimate without
refreshing its measurement time. The optional estimate and high-water mark are saved alongside the
checkpoint without modifying the SDK checkpoint. Old checkpoints remain valid.
The API and browser suppress estimates during retries, live monitoring, stale
heartbeats, or after ten minutes without a fresh measurement. Download and quota
waits within the window count toward elapsed processing time, so ordinary
two-minute batch gaps neither reset warm-up nor inflate throughput. Until enough forward
progress has been observed, the UI omits the estimate. No additional Bluesky
requests are made, and no ETA is inferred from heartbeat or hit counts.

### Archive ETag and segment-generation recovery

An archive segment download can fail with an ETag mismatch or with
`The archive server did not resume the expected segment generation.` These
`InvalidDataException` failures escape the snapshot enumeration; they are **not**
delivered to `onArchiveError`. Skipping records or blocks is therefore not a
recovery mechanism for a changed segment generation.

The monitor follows the recovery approach documented in upstream
[issue #608](https://github.com/blowdart/idunno.Bluesky/issues/608) and
[PR #609](https://github.com/blowdart/idunno.Bluesky/pull/609):

1. Let the failed enumeration unwind and log the exception, then wait using the
   cancellable backoff above.
2. Reload the latest **durably saved** `MonitorProgress` from MySQL and start a
   **new `SnapshotAsync` enumeration**. Reconstruct the same request: starting
   sequence, collections, and event kinds remain unchanged for that snapshot.
3. Pass the **entire saved `SnapshotCheckpoint` unchanged**. It retains the
   pinned sealed tip, request fingerprint, planning/replay cursors, segment
   identity/checksum, block index, byte offset, and live cursor information.
   Fresh planning can observe a changed generation and restart the affected
   segment. Never substitute a checksum, combine a new checksum with an old
   offset, clear the checkpoint, or advance the cursor to bypass a mismatch.
4. Process redelivered events idempotently. Hit transactions commit before
   checkpoint advancement, and the checkpoint callback completes its database
   write before publishing the new in-memory progress. A crash or segment
   restart can repeat events without adding duplicate hits or changing the
   last-new-hit timestamp.

`JetstreamMonitor.ReplayAsync` implements the reload/new-enumeration boundary;
`MonitorStore.SaveProgress` persists the full checkpoint synchronously because
the SDK checkpoint callback is synchronous. The inner retry loop keeps cursor
ownership while reconnecting to Jetstream; database failures escape to the outer
loop, which disposes the connection and reacquires ownership on a new attempt.
This implementation creates a new SDK client per replay attempt, but recovery
requires a new enumeration, **not a process restart or necessarily a new client**.

PR #609 changes documentation and sample code, not SDK runtime behavior:
upgrading the package alone does not introduce automatic ETag recovery. Its
sample narrowly classifies generation-mismatch exceptions by message and permits
five retries after the initial attempt, with a fixed 30-second delay and a
run-wide budget. JerryChart deliberately differs: `RetryLoop.IsRetryable`
includes **all `InvalidDataException` failures** and other listed transport,
database, and operational exceptions, retries indefinitely, and resets its
consecutive-failure backoff after a successfully processed event. This is a
service policy, not a recommendation to retry every data/configuration error
in other applications; permanent faults need operator correction. No dependency
on those ETag exception messages is used for retry classification here.

A fresh plan is an opportunity to recover, not a guarantee. Repeated mismatches
can mean archive planning metadata and segment responses remain inconsistent,
including at the server or CDN. Inspect the logged segment, expected/actual
ETags, and subsequent event/checkpoint progress; preserve the saved checkpoint
while investigating rather than editing it or repeatedly restarting the process.
An offset-zero header request can validate the segment generation even when the
saved resume offset is nonzero, so that request alone does not imply lost progress.
The `retrying` phase and a fresh heartbeat mean the worker is alive and waiting,
not that events are advancing; archive-event logs and checkpoint updates show
actual progress. Explicit record/block skips remain a separate, logged
**data-loss policy**, so recovery does not guarantee complete historical coverage.

Enable `Logging__LogLevel__JerryChart.Monitor=Debug` on the monitor to log each
event's sequence and kind, labeled as archive or live processing. An information
message announces the live subscription's starting cursor. Per-event debug
logging can generate substantial output during replay. AppHost enables this
category at Debug level; standalone execution retains the default Information level.

### Actor handles and API limits

A hosted background worker populates handles from the unauthenticated public
AppView's `app.bsky.actor.getProfiles` endpoint. It batches up to 25 distinct DIDs,
allows only one HTTP request at a time, and waits at least five seconds between
requests: at most 12 requests per minute from one updater. This is conservative
local pacing, not a guarantee against shared-IP quotas or future service limits.
It honors `Retry-After` (seconds or date), and `RateLimit-Reset` when
`RateLimit-Remaining` is zero. A 429 without a longer server-directed wait pauses
all lookups for at least five minutes. Server waits are never shortened to the
monitor's five-minute reconnect cap.

The updater initializes the shared schema before reading its queue, independently
of API or replay startup order. The MySQL queue survives restarts, coalesces repeated notifications by DID, and
is protected by a dedicated updater lock. Successful lookups are refreshed daily.
Missing profiles and `handle.invalid` remain null, are logged, and are retried
after 15 minutes; transport and API failures retry indefinitely with backoff.
Reports must support a temporarily unknown handle and always retain the DID.
Handles are AppView-provided, not independently verified against DID documents.
The monitor keeps SDK `AtUri`, `Did`, and `Handle` types internally, converting
to strings only at SQL and HTTP boundaries. Profile lookup retains its custom
HTTP, pacing, and JSON-validation implementation.

Live identity events invalidate only actors already tracked, clear obsolete
handles, and schedule a refresh without performing HTTP calls in the stream.
Revision checks prevent an in-flight lookup from overwriting a later invalidation.
Identity events are best-effort signals, not trusted handle values. Archive
requests remain commits-only so existing archive checkpoints stay compatible;
daily refresh covers identity changes missed during archive replay or outages.
Replay continues independently while the API is unavailable.

For standalone monitoring, configure `ConnectionStrings__jerrychart` and
`Jetstream__ApiKey` (or `_JetstreamApiKey`), then run:

```powershell
dotnet run --project JerryChart.Monitor
```

The default host is configured in `JerryChart.Monitor/appsettings.json`.
Both commands load the output-directory `appsettings.json` as a required,
lowest-priority baseline, so launching the executable from another working
directory retains its defaults. Working-directory settings, environment
variables, and host command-line configuration (when supplied to the builder)
retain their higher priority. The configuration scope owns and disposes its
physical file provider.
`Jetstream__Host` optionally selects a different WSS v2 host (default:
`wss://jetstream.us-west.bsky.network`). A host change is incompatible with
existing cursors; do not change it without deliberately starting a new replay.
Missing credentials or an invalid host are startup configuration errors
(exit code 2). Commands and help are parsed by System.CommandLine. No command
defaults to `run`; `run` can also be supplied explicitly. `--help` requires
neither MySQL nor a key.

Existing installations may explicitly run the one-off parent URI backfill:

```powershell
$env:ConnectionStrings__jerrychart = "<MySQL connection string>"
dotnet run --project JerryChart.Monitor -- backfill-parent-uris
```

**Just in case:** new hits already include their immediate parent-post URIs,
so daily backfill is a safety net for pending legacy rows and retryable work
left by interrupted invocations, not a requirement to resolve every new hit.
It does not repeatedly retry terminal unavailable posts.

The normal monitor process also schedules this same backfill **once daily at
03:00 UTC**, using the small `ScheduledParentUriBackfill` background service.
`AddParentUriBackfillScheduler()` registers it with the normal monitor host;
schema initialization finishes before host startup. The service uses
`TimeProvider` and cancellation-aware waits, checking the UTC clock at least
once per minute to accommodate wall-clock adjustments.
There is **no immediate startup backfill**. The shared schema is initialized
before starting the scheduler; scheduled invocations also initialize it under
their own resource lock.

Scheduled work runs only while the normal monitor host is running. Missed daily
runs are not durably recorded or replayed after a process restart. The service
awaits each invocation and has an in-process overlap guard; the independent MySQL advisory
lock prevent concurrent backfill workers, including across processes. If a
manual invocation owns the lock at the scheduled time, the scheduled invocation
logs an informational **skip**, leaves its status untouched, and waits for the
next daily slot; it does not fault or retry the lock. A competing manual command
still refuses visibly. Running a manual command does not register a scheduler.

`ScheduledParentUriBackfill` derives from `BackgroundService`,
linking the service cancellation token with the host's
`ApplicationStopping` token. It calls `ParentUriBackfillInvocation.RunAsync`,
the same underlying entrypoint used by the `backfill-parent-uris` command,
which creates `ParentPostClient` and runs `ParentUriBackfiller`. The
`ParentUriBackfillStore` acquires the independent `:parent-uri-backfill`
database advisory lock before any activity status is started.

Each scheduled or manual invocation uses this shared backfiller entrypoint, with
a fresh disposable HTTP client/rate limiter and its own dedicated non-pooled
MySQL lock connection. Both publish the same durable status and independent
heartbeat. Host shutdown cancels scheduled HTTP, retry and cooldown waits;
The background service awaits completion, including heartbeat cleanup, before the host stops.
Known invocation failures are logged without stopping future daily scheduling;
unexpected exceptions are surfaced through the standard hosted-service failure
logging and stop the host rather than silently losing the schedule.
An empty queue completes without a public network request. New hits already
contain full parent URIs, and terminal unavailable hits are never repeatedly
retried by the daily schedule.

In Aspire, run the same command as a separate process with the database
connection string from the dashboard. It does not require the Jetstream API key,
start Jetstream replay, change replay cursors, or start the actor updater.
Schema initialization adds nullable parent-URI fields and marks existing hits
unattempted; new hits include their parent URI directly. The explicit command
requests up to 25 hit posts at a time from the public AppView, verifies each
returned parent DID and post collection against the stored parent author, and
persists results in MySQL. Missing posts are recorded as unavailable and are
not retried on later invocations; AppView omission cannot distinguish deleted,
moderated, or otherwise inaccessible posts. Temporary network, HTTP 429, and
server errors are durably scheduled for retries at 1, 5, 15, 30, 90, 150, then
300 seconds indefinitely. Server-directed rate-limit cooldowns, including the
five-minute fallback for a 429 without a longer instruction, are also persisted.
Ctrl+C leaves unresolved work resumable. Malformed responses and identity
mismatches fail visibly without being treated as unavailable.
Backfill persistence is handled by a dedicated store, separate from HTTP
orchestration. Transient MySQL connection/query failures reconnect and reacquire
the dedicated advisory lock, then resume the durable queue. Database retries
use the same indefinite **1, 5, 15, 30, 90, 150, 300 seconds** schedule, capped
at 300; persisted batches reset this database backoff. Authentication, invalid
configuration, malformed schema/data, and an already-owned backfill lock fail
visibly instead of being retried as temporary outages. Cancellation interrupts
database retry waits as well as HTTP/cooldown waits.
The internal named status enum preserves the database values: `Pending = 0`
(unattempted), `Resolved = 1`, `Unavailable = 2`, and `RetryPending = 3`;
only Pending and due RetryPending rows are selected by later invocations.

## Native AOT readiness

The API, monitor, and their shared Data/ServiceDefaults libraries enable
`IsAotCompatible`, which turns on trimming and AOT analyzers. Both applications
disable reflection-based JSON serialization. The API registers `ApiJsonContext`
for every statistics response and problem-details response; monitor progress
uses `ReplayJsonContext`. Application logging uses compile-time
`LoggerMessage` generation, with CA1848 enabled for the API and monitor.

Analyzer-enabled Release builds and Windows x64 Native AOT publishes of both
applications pass without warnings, with warnings treated as errors:

```powershell
dotnet publish JerryChart.Api -c Release -r win-x64 -p:PublishAot=true -p:TrimmerSingleWarn=false -warnaserror
dotnet publish JerryChart.Monitor -c Release -r win-x64 -p:PublishAot=true -p:TrimmerSingleWarn=false -warnaserror
```

Use the target platform's native compiler toolchain (MSVC and the Windows SDK
on Windows). Native publishing is opt-in, not the default Aspire development
build.

The monitor uses a small background service instead of Coravel to avoid its
reflection-based scheduler binding and invocable construction. AOT and trimming
warnings are not suppressed. `IsAotCompatible` enables readiness analysis,
not a guarantee that every transitive dependency supports AOT; validate native
publishes when changing dependencies.

## API

Use the API endpoint from the dashboard (Aspire assigns endpoints at runtime).

| Method | Path | Result |
| --- | --- | --- |
| GET | `/statistics/reply-summary` | All-time `totalReplies`, `rightJerryReplies`, and `wrongJerryReplies` counts |
| GET | `/statistics/last-updated` | `{ updatedAt }`: UTC ISO timestamp of the last new hit, or null if not yet recorded |
| GET | `/statistics/processing-status` | No-store monitor/backfill activity, heartbeat freshness, and all-Hits parent-URI pending/retry/resolved/unavailable counts |
| GET | `/statistics/right-jerry/top-authors` | Up to ten authors: `did`, nullable current `handle`, and `replyCount` |
| GET | `/statistics/right-jerry/top-posts` | Up to five resolved correct-Jerry parent posts: `atUri` and recorded `replyCount`; count descending, parent URI ascending for ties |
| GET | `/statistics/right-jerry/authors` | Every author with matching replies to the correct Jerry, with `did`, nullable current `handle`, and `replyCount`; count descending, DID ascending for ties |
| GET | `/statistics/right-jerry/monthly-replies` | Exactly six chronological `{ month, replyCount }` entries; `month` is an ISO first-of-month date |
| GET | `/statistics/right-jerry/all-time-monthly-replies` | Monthly right-Jerry counts from the first recorded eligible reply through the current UTC month; empty array if none |
| GET | `/health` | Readiness in every environment, including MySQL connectivity |
| GET | `/alive` | Liveness in every environment; only checks tagged `live` |

Unexpected request failures
are logged and returned as problem details rather than successful empty results.

### Container probes (API and monitor)

Both processes expose **`GET /alive`** and **`GET /health`** in Production as
well as Development. Shared `MapDefaultEndpoints` returns only the plain
health status (`Healthy`, `Degraded`, or `Unhealthy`), never individual check
names, database configuration, exceptions, or secrets. A healthy probe returns
HTTP 200; an unhealthy probe returns HTTP 503. Keep these unauthenticated probe
listeners on trusted/internal networks rather than exposing them publicly.

The **API** uses its ASP.NET Core URL configuration and existing Aspire HTTP
endpoint. `/alive` checks process responsiveness; `/health` also checks the
MySQL dependency registered by `AddJerryChartDatabase`. Shared schema
initialization finishes before the API starts accepting HTTP traffic.

The normal **monitor** now hosts a lightweight ASP.NET Core listener in the
same process/DI container, using `MonitorWebHostBuilder` to retain
application-directory baseline settings and working-directory/environment/CLI
configuration precedence. Its default address is **`http://localhost:8081`**,
separate from the API. Set `ASPNETCORE_URLS=http://0.0.0.0:8081` inside Docker to
allow container-network access; standard `ASPNETCORE_HTTP_PORTS`/
`ASPNETCORE_HTTPS_PORTS` settings are also respected when no explicit URL is
configured. Aspire declares an internal monitor HTTP endpoint on target port
8081 and probes `/health`; it does not mark the endpoint externally exposed.
The standalone `backfill-parent-uris` command remains a generic CLI host with
**no HTTP listener or scheduler**. Invalid Jetstream credentials still exit
with code 2 before starting the monitor listener.

`MonitorSupervisor` owns the replay task as a hosted service and supplies a
`live`-tagged check. Monitor `/alive` requires both HTTP responsiveness and an
active supervisor lifetime; `/health` additionally requires MySQL readiness.
Before startup or after supervisor termination, its check is unhealthy.
Schema initialization completes before the monitor listener/scheduler starts,
so connection refusal during initialization is expected: configure an
appropriate container startup grace period. Graceful host shutdown cancels
and awaits replay, scheduled backfill and their heartbeats; unexpected
supervisor termination is logged and exits with failure.

These checks deliberately do **not** judge event throughput, checkpoint age,
or heartbeat freshness. Quiet live streams, archive quota waits, normal
network retries, and scheduled backfill cooldowns do not themselves make the
supervisor unhealthy or trigger restart storms. A MySQL outage fails readiness
but not process liveness while the supervisor is retrying. A responsive HTTP
listener and independently refreshing heartbeat cannot prove that replay is
making progress: a stalled-but-running worker may remain live. No watchdog,
automatic stall exit, or restart policy is implemented.

For a future Docker image, configure explicit ports, for example
`ASPNETCORE_URLS=http://0.0.0.0:8080` for the API and
`ASPNETCORE_URLS=http://0.0.0.0:8081` for the monitor. Example Dockerfile probes:

```dockerfile
# API image
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
  CMD curl --fail --silent --show-error http://127.0.0.1:8080/alive || exit 1

# Monitor image (use this instead in that image)
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
  CMD curl --fail --silent --show-error http://127.0.0.1:8081/alive || exit 1
```

These examples require `curl` and a shell **already present in the image**;
slim/chiseled .NET images may provide neither. Install an appropriate probe
tool in your future Dockerfile or use your orchestrator's native HTTP probe.
Use `/health` instead for dependency readiness (for example Kubernetes
readiness), while `/alive` is suitable for liveness. Adjust the startup grace
period for database migration time. Docker marking a container **unhealthy**
does not automatically restart it; restart-on-exit policies and orchestrator
liveness/restart decisions are separate deployment configuration.

The reply summary counts rows in `Hits` (one per matching post, not distinct
authors), with no date filter. Right Jerry means the immediate `ParentAuthorDid`
equals `did:plc:vc7f4oafdgxsihk4cry2xpze`; every other immediate parent DID counts
as wrong Jerry, regardless of display name or handle. Total equals right plus
wrong. An empty database returns three zeros. One aggregate SQL statement keeps
the counts internally consistent while replay writes continue. Counts reflect
data recorded so far, not a claim that historical replay is complete.
The right-aligned Refreshing button at the top of the footer re-fetches the homepage's API
statistics and re-renders its tables and charts without a full page reload.
Statistics also refresh automatically every 150 seconds (2.5 minutes) while
the page is visible. Manual and automatic refreshes share the same cooldown:
either resets the automatic timer and disables the button with a countdown.
Hidden pages pause automatic requests and refresh once when visible again if
overdue. This is a page-local UI cooldown, not a server-side rate limit.
The frontend also fetches uncached counts on page load or browser reload; database/API errors
are shown as errors, never as zero counts.

Below the total summary and before the charts, the top-five posts table ranks
Jerry's **parent posts**, not the replies themselves. It counts deduplicated,
recorded matching replies across all time, excluding wrong-Jerry replies and
unresolved/null parent URIs. Grouping uses both the parent URI hash and full URI
so hash collisions cannot merge different posts. An empty database returns an
empty array and an explicit empty state; unavailable parent URIs are never invented.
The website fetches text in one browser-side batch (up to five URIs) from public
Bluesky `app.bsky.feed.getPosts`, validating post identity and author. Links use
Jerry's stable DID and the parent post's record key. Text is escaped plain text
with line breaks preserved. Deleted or omitted posts show “Post unavailable”;
valid empty text shows “Post has no text”. Request or validation failures show an
explicit error and retry button, not a successful empty result. Rankings and
recorded counts remain visible even when public post text cannot be loaded.
Reloading fetches a fresh ranking and text; canceled/unmounted requests cannot
replace the current list. The .NET API does not fetch public post text.

The top-ten table ranks authors by their all-time number of matching replies
whose immediate parent is the right Jerry. It counts posts, not distinct parent
posts, and excludes replies to anyone else. Counts are descending, with DID
ascending as a deterministic tie-breaker; at most ten authors are returned.
Handles come from `Actor` at query time, so renames do not split an author's
counts. Authors without a known handle still appear with their DID. Profile
links use DIDs so they remain stable across handle changes. With no matching
replies the endpoint returns an empty array and the UI displays an empty state.

The monthly bar chart counts matching replies to the right Jerry by the post's
UTC `CreatedAt`, not ingestion time. It includes the current UTC calendar month
through the present instant plus the previous five months, filling missing months
with zeros and excluding future-dated posts.
Bars share a zero baseline and display exact counts; page load and browser reload fetch
the latest data. As with other statistics, incomplete archive replay can produce
incomplete historical counts.

Click the six-month chart (or focus it and press Enter/Space) to open the all-time
monthly chart above the homepage. It counts only replies to the correct Jerry,
includes zero-count months, and excludes future posts. Full history loads on
demand through a server-side Next.js proxy; API addresses stay server-only.
The modal supports Close, Escape, backdrop dismissal, loading/error/retry states,
and horizontal scrolling for long histories.

Click the top-ten table (or focus its popup button and press Enter/Space) to open
the full ranked list of users and their all-time matching reply counts to the
correct Jerry. Profile links still navigate to Bluesky. The list loads on demand
through a server-side proxy, scrolls within the popup, and supports the same
close, loading, error, and retry behavior as the all-time chart.

Hover over a profile link in either user list to preview the user's avatar,
display name, bio, and follower/following/post counts. Profiles are fetched
directly by the browser from Bluesky's public `app.bsky.actor.getProfile` API
only when a card opens. Successful responses are cached by DID in page memory
for 15 minutes, shared between both lists, with concurrent requests coalesced
and at most 250 cached profiles. Reloading the page clears the cache. Failed
requests show an error with retry and are not cached.
HTTP and HTTPS URLs in profile text are clickable and open in a new tab.
Handle mentions such as `@bsky.app` are linked to DID-based Bluesky profiles only
after the browser confirms they exist through the public `resolveHandle` API.
Resolution results are shared and cached in page memory for 15 minutes (up to
250 entries); unknown accounts stay plain text, and transient lookup failures
are logged and not cached. Email addresses are not treated as mentions.

The footer shows when statistics last changed through a new hit insertion, not
the post's creation date or the replay checkpoint time. Duplicate deliveries,
failed transactions, and actor handle updates do not advance this timestamp.
Existing hits cannot be backdated accurately; the timestamp remains unknown
until a new hit is inserted after this feature is deployed.
The browser formats the date using its preferred language and local time zone
(including locale-default hour-cycle preferences). Before hydration or without
JavaScript, a UTC timestamp is displayed instead.

## Packages and testing

The **CI Build** GitHub Actions workflow runs on every push to any branch and
on pull requests. Separate Ubuntu jobs build .NET 10 and run all
Microsoft.Testing.Platform tests (including isolated Docker/MySQL and Next.js
integration), and install, lint, type-check, test, and production-build the
frontend with Node.js 26. The monitor is excluded from integration startup, so
CI needs no archive API key and does not contact the metered archive.
TRX/diagnostic artifacts and frontend JUnit results are retained for 14 days,
including failed runs. Workflow permissions are read-only, and action versions
are pinned to immutable commits.

NuGet Central Package Management is enabled in `Directory.Packages.props`.
All explicit package versions belong there; project `PackageReference` items
have no versions. SDK versions for Aspire and MSTest are managed separately
under `msbuild-sdks` in `global.json`. C# is pinned to version 13.

`nuget.config` follows the
[idunno.Bluesky pre-release instructions](https://github.com/blowdart/idunno.Bluesky#pre-releases):
`idunno.AtProto`, `idunno.AtProto.*`, `idunno.Bluesky`, and `idunno.Bluesky.*`
are mapped exclusively to the public blowdart MyGet feed; other packages use
nuget.org. The monitor's pinned pre-release versions are in
`Directory.Packages.props`; `idunno.AtProto.Jetstream` is a namespace in the
`idunno.AtProto` package, not a separate package.

`.github/dependabot.yml` checks NuGet dependencies at the solution root, npm
dependencies in `jerrychart-web`, and GitHub Actions daily, with a seven-day
cooldown for version updates. Regular update PRs are enabled, not just security
updates. The public MyGet registry is configured explicitly for the idunno
prereleases. Groups retain the upstream test/JWT and CodeQL patterns and add
Aspire, MSTest/Testing.Platform, idunno, OpenTelemetry, Next.js, React, and
Tailwind groups. Dependencies outside those patterns remain eligible for
individual updates. Both frontend and lint-package lockfiles are monitored;
ESLint and TypeScript major updates are held for coordinated tooling migrations.

`global.json` selects the .NET 10 **Microsoft.Testing.Platform** `dotnet test`
runner, and `JerryChart.Tests` uses `MSTest.Sdk` with its native MTP runner.
There is no VSTest or `Microsoft.NET.Test.Sdk` configuration.

```powershell
dotnet test --solution JerryChart.sln --report-trx
```

Integration tests require Docker and Node.js. They start the Aspire application
with an ephemeral MySQL container (no development data volume), omit the live
monitor to avoid contacting the metered archive, and verify MySQL hit
deduplication, author indexes, checkpoint restoration, exclusive cursor ownership,
schema cleanup, statistics API contracts, and frontend report rendering.
Resources are disposed at test completion. To run only the unit tests without Docker:

```powershell
dotnet test --solution JerryChart.sln --filter "TestCategory!=Integration"
```

The full frontend integration test runs `npm ci`; stop any frontend dev server
using this worktree before running it. The monitor-only integration test
`MonitorStateAndHitsSurviveRestart` does not install or run the frontend and
requires only Docker.

Frontend checks:

```powershell
Set-Location jerrychart-web
npm ci
node --test tests\top-reply-posts.test.cjs
npm run lint
npm run typecheck
npm run build
```

With dependencies already installed and a dev server active, skip `npm ci` and
the production build. The isolated Node/TypeScript tests exercise ranking and
public post validation, rendering, loading/error/retry states, and cancellation
without installing packages or contacting Bluesky.

To run Next.js independently, set `API_BASE_URL` to the running API's HTTP
endpoint before `npm run dev`. Keep this server-only variable unprefixed by
`NEXT_PUBLIC_`.

The frontend uses React/React DOM 19.3, TypeScript 7, ESLint 9 with the
Next.js 16.3.8 flat configuration, and the shadcn 4 CLI. Node 26 is required
and its type definitions match the runtime. `npm run typecheck` generates
Next.js route types before invoking TypeScript, so it works on a clean checkout.
Next.js builds use the TypeScript CLI; Node tests use esbuild to transpile
the real source modules because TypeScript 7 no longer exposes the old
JavaScript compiler API. Generated shadcn/ui components remain in the source
tree; package upgrades do not regenerate them.
ESLint remains on 9.x because the Next.js React/import/accessibility plugins
do not yet support ESLint 10. The private `tooling/lint` npm package supplies
TypeScript 6 to `eslint-config-next` and its lint dependencies, which still require the
JavaScript compiler API. The application and Next.js build use TypeScript 7.
The frontend's postinstall installs the locked lint dependencies separately,
avoiding incompatible compiler peers in the application dependency tree.

**Accepted developer-tool advisory:** modern shadcn and Next.js lint tooling
depend transitively on `braces` through glob-processing packages.
[GHSA-vfj7-8cjw-p6xm](https://github.com/advisories/GHSA-vfj7-8cjw-p6xm)
reports stack-exhaustion denial of service from deeply nested patterns, with no
patched `braces` release currently available. These dependencies are development
tools, not application runtime dependencies. Keep tool inputs trusted and review
the advisory when updating packages; the accepted risk is not an audit-clean
result. Do not use `npm audit fix --force` to silently downgrade this toolchain.
