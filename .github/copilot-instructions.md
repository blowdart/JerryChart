## Overview

JerryChart is a .NET 10/Aspire application that tracks Bluesky replies containing
"Jerry no". It replays the Jetstream archive, then follows live events, stores
durable progress and statistics in MySQL, and displays reports in a Next.js
frontend using shadcn/ui.

## Build, test, and lint

* Use the stable .NET 10 SDK selected by `global.json`. The solution is `JerryChart.sln`.
* Build with analyzers and warnings as errors: `dotnet build JerryChart.sln -c Release -warnaserror`.
* Run all .NET tests: `dotnet test --solution JerryChart.sln -c Release --report-trx`.
* Run one test project: `dotnet test --project JerryChart.Tests\JerryChart.Tests.csproj -c Release --report-trx`.
* Run one test or class by adding `--filter "FullyQualifiedName~MonitorMetricsTests"`.
* For unit-only iteration, add `--filter "TestCategory!=Integration"`.
* Tests target `net10.0` and use MSTest with Microsoft.Testing.Platform, not VSTest.
* SonarAnalyzer runs during builds. There are no PublicAPI tracking files or separate documentation analyzers to update.
* **CI builds Release**, with `-warnaserror`, and runs all .NET tests, including Aspire integration tests.
* Integration tests require Docker with Linux containers; the full application test also requires Node 26 and frontend dependencies.
* Frontend CI runs `npm ci`, `npm run lint`, `npm run typecheck`, Node tests, and `npm run build` from `JerryChart.Web`.
* Read `JerryChart.Web\AGENTS.md` and `JerryChart.Web\CLAUDE.md` before frontend changes. ESLint has its own dependency installation under `tooling\lint`.
* Commands above use Windows paths. Use platform-appropriate separators in Linux CI.
* **Never trust an incremental build as the final pre-commit gate.** Use `--no-incremental` so analyzers run again.

### GitHub Actions failed-test protocol

1. Do not rerun, rerun failed jobs, or dispatch a workflow until the original failure is fully diagnosed or the user explicitly authorizes a rerun.
2. Preserve the original run ID, job IDs, attempt number, commit SHA, and failing check names. Retrieve failed logs with `gh run view <run-id> --log-failed`, inspect check-run annotations and summaries with `gh api`, and list and download all artifacts, especially test results, TRX, JUnit, coverage, and diagnostics.
3. Record each failing test, framework, project, exception, and stack trace before recovery.
4. If logs are unavailable because the workflow is still running, wait for it to finalize. A failed job while other jobs are running is not a reason to rerun.
5. Only after preserving evidence may a failed job be rerun. A rerun tests for flakiness; it does not diagnose or fix the original failure.
6. If the original failure details cannot be recovered, say so. Do not infer test names from aggregate counts or claim a green rerun fixed the issue.
7. Merge only after the original failure is diagnosed or explicitly accepted as an unresolvable infrastructure/flaky failure, required checks pass on the final commit, and required CodeQL/Copilot review conditions are satisfied.

Never use `gh run rerun --failed` as the first response to a failed test job.

## Requesting Copilot code review

Use `gh pr edit <number> --repo blowdart/JerryChart --add-reviewer '@copilot'`,
then verify with `gh pr view <number> --repo blowdart/JerryChart --json reviewRequests`.
Do not claim success unless Copilot appears in `reviewRequests`. Otherwise report
that it could not be verified; Copilot review may be disabled or unavailable.
Do not use `gh pr review` to request Copilot: it submits a review as the
authenticated user.

When reviewing Copilot findings, include previously missed findings and earlier
findings that remain unaddressed, not just new comments.

## Pre-commit gate

Before every commit, run these from the repository root and require both to pass:

1. `dotnet build JerryChart.sln -c Release --no-incremental -warnaserror` — zero warnings and errors.
2. `dotnet test --solution JerryChart.sln -c Release --no-build --report-trx` — all tests, including integration tests.

For frontend changes, also run the frontend lint, type-check, Node tests, and
production build used by CI. Use the existing test reporters for readable output
and retained results.

Partial builds and filtered tests are for iteration, not the final gate.
Do not disrupt a running orchestration, overwrite an active frontend build, stop
the real monitor, or reset its database to satisfy this gate. Use an isolated
validation environment when necessary. If prerequisites are unavailable, report
the blocker rather than claiming the gate passed.

## Architecture

* **AppHost** orchestrates MySQL, the API, monitor, and frontend, including connection strings and OTLP configuration.
* **API** serves statistics through minimal HTTP endpoints. **Data** owns shared MySQL registration, schema, and queries. Keep SQL parameterized.
* **Monitor** performs archive replay and live Jetstream processing, durable checkpoints, actor refresh, and parent-URI backfill. Preserve idempotency, ownership fencing, retry behavior, and cancellation.
* **ServiceDefaults** configures shared telemetry, health checks, service discovery, and HTTP-client defaults.
* **Frontend** fetches the API from Next.js server-side code and renders reports. Keep credentials and internal service configuration out of browser code.
* **Source-generated JSON** keeps the API and monitor reflection-free. Register new serializable types in the appropriate `JsonSerializerContext`; do not add a reflection fallback.
* **Source-generated logging and metrics** keep instrumentation compile-time generated. Use bounded, strongly typed metric tags. Never put credentials, exception messages, SQL, DIDs, AT URIs, or unbounded values in application metric tags.
* **Outbound monitor traffic** uses SSRF-protected HTTP handlers, including the Jetstream WebSocket invoker. Preserve redirect restrictions and unsafe-address blocking.
* **Backfill scheduling** uses a hosted background service plus database locking. Do not introduce immediate startup backfills or replay missed slots without an explicit behavior change.

## Key conventions

* **Documentation.** Update directly related README guidance when behavior or configuration changes. There is no package-grouped changelog or PublicAPI ledger to maintain.
* **Targeting and trimming.** API, Monitor, Data, and ServiceDefaults are AOT-compatible. Keep changes trimming-safe, use configuration/source generators, and validate native publish when changing AOT-sensitive code or dependencies.
* **Internal APIs.** API and Monitor expose internals to `JerryChart.Tests` through project configuration. Prefer internal implementation types rather than unnecessary public surface.
* **Configuration.** Preserve central package versions in `Directory.Packages.props` and package source mappings in `nuget.config`. AT Protocol/Bluesky prereleases come from the configured MyGet feed.
* **Infrastructure.** Do not change `.editorconfig`, SDK selection, shared build configuration, package feeds, or workflow policy incidentally. Change them only when requested or directly required by the task, and explain meaningful changes.
* **Secrets.** Use user secrets or environment/platform secret stores. Never commit credentials or real connection strings.
* **Errors.** Preserve diagnostic logs and propagate unexpected failures. Do not hide failures with broad catches, silent defaults, or success-shaped fallback results.

## C# style

* Use C# 13, as pinned by `Directory.Build.props`; do not use preview language features.
* Apply `.editorconfig`. Use file-scoped namespaces and single-line `using` directives outside the namespace, with System imports first and separated groups.
* Put block opening braces on their own line; keep a method's final `return` on its own line.
* Prefer pattern matching and switch expressions. Use `nameof` rather than string literals for member names.
* Use `?.` where applicable and `ObjectDisposedException.ThrowIf` for disposal guards.
* Prefer explicit types where the type is not apparent; follow `.editorconfig` for `var`.
* Follow existing naming: PascalCase constants, `_camelCase` instance fields, and `s_camelCase` static fields.
* Every public API needs XML documentation following [docs.instructions.md](instructions/docs.instructions.md) and [docs.prompt.md](prompts/docs.prompt.md).
* Comment only non-obvious logic; avoid comments that merely narrate the code.

### Nullable reference types

* Nullable is enabled in the C# projects. Use non-nullable declarations when values are required and validate nullable input at entry points.
* Use `is null` / `is not null`, not `== null` / `!= null`.
* Trust null annotations; do not add checks the type system says are unnecessary.
* Preserve type safety rather than suppressing nullability warnings or using unnecessary casts.

## Testing conventions

* Use MSTest attributes and assertions, with Microsoft.Testing.Platform.
* Do not add `// Arrange` / `// Act` / `// Assert` comments. Match nearby test names and conventions.
* Prefer data-driven `[TestMethod]` tests with `[DataRow]` or `[DynamicData]` over near-duplicate tests.
* Unit and integration tests live in `JerryChart.Tests`; mark database/application tests with `[TestCategory("Integration")]`.
* Use isolated MySQL containers for integration tests, never the user's running database.
* Follow [tests.instructions.md](instructions/tests.instructions.md). Tests must work on Windows and Linux; use platform-aware paths and preserve useful failure diagnostics.
* Add focused regressions for behavior changes and actually run them. A successful build is not a substitute for exercising the changed behavior.

## Check-in conventions

* Run the pre-commit gate before committing. Do not claim an incremental or filtered run satisfies it.
* Use the session's feature branch, not `main`. Branch naming and worktree creation are app-managed; never use raw Git branch renaming or modify the main checkout.
* Use short imperative commit subjects, such as "Add monitor metrics" or "Fix archive retries".
* Open a pull request when requested. Summarize the change, link relevant issues, and note breaking behavior changes.
* Never create unsigned commits. Use the configured GPG or SSH signer.
* If signing requires interaction or fails, stop and report it. Do not retry, change signing configuration, delegate a retry, or commit unsigned without explicit instructions to resume the signing attempt.
* Preserve unrelated worktree changes and do not amend commits unless explicitly requested.
