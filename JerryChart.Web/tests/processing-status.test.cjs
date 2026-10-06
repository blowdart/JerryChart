const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { createRequire } = require("node:module");
const test = require("node:test");
const { transformSync } = require("esbuild");
const React = require("react");
const { renderToStaticMarkup } = require("react-dom/server");

const root = path.resolve(__dirname, "..");
function modules(overrides = {}) {
  const cache = new Map();
  function load(relative) {
    // Tests run on Windows locally and Linux in CI; normalize either separator.
    const file = path.join(root, "src", ...relative.split(/[\\/]/));
    if (cache.has(file)) return cache.get(file).exports;
    const module = { exports: {} };
    cache.set(file, module);
    const nativeRequire = createRequire(file);
    const requireModule = (name) => {
      if (Object.hasOwn(overrides, name)) return overrides[name];
      if (name === "server-only") return {};
      if (!name.startsWith("@/")) return nativeRequire(name);
      const relativePath = name.slice(2);
      return load(relativePath + (fs.existsSync(path.join(root, "src", relativePath + ".ts")) ? ".ts" : ".tsx"));
    };
    const compiled = transformSync(fs.readFileSync(file, "utf8"), {
      loader: file.endsWith(".tsx") ? "tsx" : "ts",
      format: "cjs",
      jsx: "automatic",
      target: "es2022",
      sourcefile: file,
    }).code;
    new Function("require", "module", "exports", compiled)(requireModule, module, module.exports);
    return module.exports;
  }
  return load;
}

const { isProcessingStatus, activityMessage, activityOutcome, activitySummary, replayThroughputMessage } = modules()("lib\\processing-status.ts");
const observedAt = "2026-10-05T12:00:00+00:00";
const worker = (phase = "not-started") => ({
  phase, state: phase, isRunning: ["archive", "live", "backfill-running", "retrying",
    "handle-refresh-idle", "handle-refresh-running", "handle-refresh-waiting"].includes(phase),
  startedAt: phase === "not-started" ? null : observedAt,
  changedAt: phase === "not-started" ? null : observedAt,
  heartbeatAt: phase === "not-started" ? null : observedAt,
  finishedAt: ["completed", "stopped", "failure"].includes(phase) ? observedAt : null,
});
const status = (phase = "not-started", parentPhase = "not-started") => ({
  observedAt,
  monitor: { activity: worker(phase), checkpointUpdatedAt: null, archiveEstimate: null },
  parentUriBackfill: { activity: worker(parentPhase), pending: 0, retryPending: 0, retryDue: 0, resolved: 0, unavailable: 0 },
  handleRefresh: { activity: worker(), pending: 0, due: 0, nextDueAt: null },
});
const response = (body, code = 200) => new Response(JSON.stringify(body), { status: code });
const settle = () => new Promise((resolve) => setImmediate(resolve));

const throughput = () => ({
  deliveredEvents: 240, windowSeconds: 120, windowStartedAt: "2026-10-05T11:58:00Z",
  measuredAt: observedAt, eventsPerSecond: 2, microsecondsPerEvent: 500_000,
});
const throughputStatus = () => {
  const value = status("archive");
  value.monitor.activity.startedAt = value.monitor.activity.changedAt = throughput().windowStartedAt;
  value.monitor.archiveThroughput = throughput();
  return value;
};

test("optional measured throughput validates actual counts, elapsed windows and consistent derived rates", () => {
  assert.ok(isProcessingStatus(status("archive")), "Older APIs can omit throughput.");
  const value = throughputStatus();
  assert.ok(isProcessingStatus(value));
  assert.ok(isProcessingStatus({ ...value, monitor: { ...value.monitor, archiveThroughput: null } }));
  for (const invalid of [
    {}, { ...throughput(), deliveredEvents: 0 }, { ...throughput(), deliveredEvents: -1 },
    { ...throughput(), deliveredEvents: 1.5 }, { ...throughput(), deliveredEvents: Number.MAX_SAFE_INTEGER + 1 },
    { ...throughput(), windowSeconds: 119.999 }, { ...throughput(), windowSeconds: Infinity },
    { ...throughput(), windowSeconds: NaN }, { ...throughput(), eventsPerSecond: 0 },
    { ...throughput(), eventsPerSecond: 3 }, { ...throughput(), microsecondsPerEvent: 1 },
    { ...throughput(), microsecondsPerEvent: Infinity }, { ...throughput(), measuredAt: "invalid" },
    { ...throughput(), windowStartedAt: "2026-10-05T12:00:01Z" },
  ]) assert.equal(isProcessingStatus({ ...value, monitor: { ...value.monitor, archiveThroughput: invalid } }), false);
});

test("throughput is a window average hidden at exact stale boundaries and in other attempts or phases", () => {
  const value = throughputStatus();
  const message = (monitor = value.monitor, elapsed = 0) => replayThroughputMessage(monitor, observedAt, elapsed);
  assert.equal(message(), "Replay throughput (window average): 2 events/s (500,000 µs/event).");
  assert.ok(message(value.monitor, 59_999));
  assert.equal(message(value.monitor, 60_000), null);
  assert.equal(message({ ...value.monitor, archiveThroughput: null }), null);
  for (const phase of ["retrying", "live", "stopped", "failure"]) {
    assert.equal(message({ ...value.monitor, activity: worker(phase) }), null);
  }
  for (const overrides of [
    { state: "stale", isRunning: false }, { isRunning: false },
    { startedAt: observedAt }, { changedAt: observedAt },
    { heartbeatAt: "2026-10-05T11:59:00Z" },
  ]) assert.equal(message({ ...value.monitor, activity: { ...value.monitor.activity, ...overrides } }), null);
  assert.equal(message({ ...value.monitor, archiveThroughput: {
    ...throughput(), measuredAt: "2026-10-05T12:00:01Z",
  } }), null);
  assert.equal(message({ ...value.monitor, archiveThroughput: {
    ...throughput(), measuredAt: "2026-10-05T11:59:00Z",
  } }), null);
  assert.equal(message({ ...value.monitor, archiveReplay: { stalledSince: observedAt } }), null);
});

test("throughput appears only in Jetstream details, expires during failed polling, and preserves countdown", async (context) => {
  context.mock.method(console, "error", () => {});
  context.mock.method(global, "fetch", async () => response({}, 503));
  const component = harness(context);
  const initial = { status: throughputStatus(), error: null };
  component.render(initial);
  component.commit();
  let html = component.render();
  const [summary, details] = html.split("<dialog");
  assert.doesNotMatch(summary, /Replay throughput/);
  assert.match(details, /Jetstream processing[\s\S]*Replay throughput \(window average\): 2 events\/s \(500,000 µs\/event\)/);
  assert.match(details, /not individual event latency/);
  component.tick();
  await settle();
  assert.match(component.render(), /Replay throughput/);
  component.advance(29_000);
  assert.match(component.render(), /Replay throughput/);
  assert.match(component.render(), /Next status refresh in 0:01/);
  component.advance(1_000);
  await settle();
  html = component.render();
  assert.doesNotMatch(html, /Replay throughput/);
  assert.match(html, /Unable to refresh processing status/);
  assert.match(html, /Next status refresh in 0:30/);
  component.unmount();
});

test("throughput continues aging during an outstanding poll independently of the heartbeat", async (context) => {
  let resolveRequest;
  context.mock.method(global, "fetch", () => new Promise((resolve) => { resolveRequest = resolve; }));
  const component = harness(context);
  const value = throughputStatus();
  value.monitor.archiveThroughput.measuredAt = "2026-10-05T11:59:31Z";
  value.monitor.archiveThroughput.windowStartedAt = "2026-10-05T11:57:31Z";
  value.monitor.activity.startedAt = value.monitor.activity.changedAt = value.monitor.archiveThroughput.windowStartedAt;
  component.render({ status: value, error: null });
  component.commit();
  component.tick();
  assert.match(component.render(), /Refreshing status\.\.\./);
  assert.match(component.render(), /Replay throughput/);
  component.advance(1_000);
  const html = component.render();
  assert.doesNotMatch(html, /Replay throughput/);
  assert.match(html, /Historical replay in progress/, "Only the throughput is stale; the heartbeat is still fresh.");
  component.unmount();
  resolveRequest(response(status("live")));
  await settle();
});

test("status validation accepts empty not-started, stale and terminal snapshots but not malformed success", () => {
  assert.ok(isProcessingStatus(status()));
  assert.ok(isProcessingStatus(status("live", "completed")));
  const stale = status("archive", "retrying");
  stale.monitor.activity.state = "stale";
  stale.monitor.activity.isRunning = false;
  assert.ok(isProcessingStatus(stale));
  for (const invalid of [
    null, {}, { ...status(), observedAt: "2026-01-01" },
    { ...status(), monitor: null },
    { ...status(), parentUriBackfill: { ...status().parentUriBackfill, retryDue: 1 } },
    { ...status(), parentUriBackfill: { ...status().parentUriBackfill, pending: -1 } },
    { ...status(), parentUriBackfill: { ...status().parentUriBackfill, unavailable: 1.5 } },
    { ...status(), monitor: { ...status().monitor, activity: { ...worker(), isRunning: true } } },
    { ...status(), monitor: { ...status().monitor, activity: { ...worker("live"), heartbeatAt: null } } },
    { ...status(), monitor: { ...status().monitor, activity: { ...worker("completed"), state: "stale" } } },
  ]) assert.equal(isProcessingStatus(invalid), false, JSON.stringify(invalid));
});

test("activity messages distinguish replay, live, retries, stale, stopped, failure and unknown without invented progress", () => {
  assert.match(activityMessage(worker("archive"), observedAt, 59_999), /Historical replay in progress/);
  assert.match(activityMessage(worker("live"), observedAt, 0), /Listening to live/);
  assert.match(activityMessage(worker("backfill-running"), observedAt, 0), /backfill in progress/);
  assert.match(activityMessage(worker("retrying"), observedAt, 0), /Retrying/);
  for (const phase of ["archive", "live", "backfill-running", "retrying"]) {
    assert.match(activityMessage(worker(phase), observedAt, 60_000), /Heartbeat stale; activity unknown/);
  }
  assert.match(activityMessage(worker("stopped"), observedAt, 90_000), /Stopped/);
  assert.match(activityMessage(worker("failure"), observedAt, 90_000), /failed/);
  assert.match(activityMessage(worker("completed"), observedAt, 90_000), /recorded run completed/);
  assert.match(activityMessage(worker(), observedAt, 90_000), /Not started \/ unknown/);
});

test("activity summaries stay concise while distinguishing stale, stalled and worker phases", () => {
  assert.equal(activitySummary(worker("archive"), observedAt, 0), "Historical replay in progress.");
  assert.equal(activitySummary(worker("live"), observedAt, 0), "Listening to live events.");
  assert.equal(activitySummary(worker("retrying"), observedAt, 0), "Retrying after a temporary failure or waiting for scheduled retries.");
  assert.equal(activitySummary(worker("archive"), observedAt, 60_000), "Heartbeat stale; activity unknown.");
  assert.equal(activitySummary(worker("archive"), observedAt, 0, {
    noProgressSince: observedAt, lastProgressAt: observedAt, stalledSince: observedAt,
    consecutiveGenerationMismatches: 2, nextRetryAt: null,
  }), "Archive stalled.");
});

test("activity outcomes distinguish never-run, completed, active and stale backfills", () => {
  assert.equal(activityOutcome(worker(), observedAt, 0), "Never run.");
  assert.equal(activityOutcome(worker("completed"), observedAt, 0), "Completed.");
  assert.match(activityOutcome({ ...worker("completed"), finishedAt: null }, observedAt, 0), /No finish recorded/);
  assert.equal(activityOutcome(worker("backfill-running"), observedAt, 0), "In progress.");
  assert.match(activityOutcome(worker("backfill-running"), observedAt, 60_000), /Activity stale; the run may have been interrupted/);
  assert.equal(activityOutcome(worker("failure"), observedAt, 0), "Failed.");
  assert.equal(activityOutcome(worker("stopped"), observedAt, 0), "Stopped before completion.");
});

test("handle refresh validates recurring eligibility separately from worker activity and accepts older API snapshots", () => {
  const value = status("archive");
  value.handleRefresh = { activity: worker("handle-refresh-idle"), pending: 12, due: 0, nextDueAt: observedAt };
  assert.ok(isProcessingStatus(value));
  value.handleRefresh.activity = worker();
  value.handleRefresh.due = 12;
  assert.ok(isProcessingStatus(value), "Due rows must not imply an active worker.");
  const legacy = { ...value };
  delete legacy.handleRefresh;
  assert.ok(isProcessingStatus(legacy));
  for (const handles of [
    null, {}, { ...value.handleRefresh, pending: -1 }, { ...value.handleRefresh, due: 13 },
    { ...value.handleRefresh, due: 0.5 }, { ...value.handleRefresh, nextDueAt: "invalid" },
    { ...value.handleRefresh, nextDueAt: null }, { ...value.handleRefresh, pending: 0, due: 0 },
    { ...value.handleRefresh, activity: { ...worker("handle-refresh-idle"), isRunning: false } },
  ]) assert.equal(isProcessingStatus({ ...value, handleRefresh: handles }), false);
  for (const phase of ["handle-refresh-idle", "handle-refresh-running", "handle-refresh-waiting"]) {
    const active = worker(phase);
    assert.ok(isProcessingStatus({ ...value, handleRefresh: { ...value.handleRefresh, activity: active } }));
    assert.doesNotMatch(activityOutcome(active, observedAt, 0), /Completed/);
    assert.match(activityMessage(active, observedAt, 60_000), /Heartbeat stale; activity unknown/);
    assert.match(activityOutcome(active, observedAt, 60_000), /Activity stale/);
    const stale = { ...active, state: "stale", isRunning: false };
    assert.ok(isProcessingStatus({ ...value, handleRefresh: { ...value.handleRefresh, activity: stale } }));
  }
  assert.match(activityMessage(worker("handle-refresh-waiting"), observedAt, 0), /request pacing or a server rate limit/);
});

test("archive stalls remain distinct from heartbeat liveness, including stale cached and stopped workers", () => {
  const replay = {
    noProgressSince: "2026-10-05T11:45:00Z", lastProgressAt: "2026-10-05T11:45:00Z",
    stalledSince: "2026-10-05T11:50:00Z", consecutiveGenerationMismatches: 7,
    nextRetryAt: "2026-10-05T12:05:00Z",
  };
  const value = status("retrying");
  value.monitor.archiveReplay = replay;
  assert.ok(isProcessingStatus(value));
  const message = activityMessage(worker("retrying"), observedAt, 0, null, replay);
  assert.match(message, /heartbeat is fresh, but archive processing is not progressing/);
  assert.match(message, /Archive stalled: 10 minutes stalled; 15 minutes without/);
  assert.match(message, /7 consecutive generation mismatches/);
  assert.doesNotMatch(message, /time remaining|Historical replay in progress/);
  const stale = activityMessage(worker("retrying"), observedAt, 60_000, null, replay);
  assert.match(stale, /Heartbeat stale; activity unknown/);
  assert.match(stale, /Last recorded archive stall: 11 minutes stalled/);
  assert.match(activityMessage(worker("stopped"), observedAt, 0, null, replay), /Worker stopped.*Last recorded archive stall/);
  assert.match(activityMessage(worker("archive"), observedAt, 0, null,
    { ...replay, stalledSince: null, consecutiveGenerationMismatches: 0 }), /Historical replay in progress/);
  for (const invalid of [
    { ...replay, consecutiveGenerationMismatches: -1 }, { ...replay, consecutiveGenerationMismatches: 1.5 },
    { ...replay, noProgressSince: null }, { ...replay, lastProgressAt: "invalid" },
    { ...replay, nextRetryAt: "invalid" }, { ...replay, stalledSince: "invalid" },
  ]) {
    value.monitor.archiveReplay = invalid;
    assert.equal(isProcessingStatus(value), false);
  }
});

test("archive estimates are validated and shown only with fresh forward progress in the current attempt", () => {
  const value = status("archive");
  const estimate = { remainingSeconds: 7380, measuredAt: observedAt };
  value.monitor.archiveEstimate = estimate;
  assert.ok(isProcessingStatus(value));
  assert.match(activityMessage(worker("archive"), observedAt, 0, estimate), /Approximate time remaining: 2 hours 3 minutes/);
  assert.match(activityMessage(worker("archive"), observedAt, 0, { ...estimate, remainingSeconds: 40 }), /less than a minute/);
  assert.match(activityMessage(worker("archive"), observedAt, 0, { ...estimate, remainingSeconds: 86400 }), /1 day/);
  assert.equal(activityMessage(worker("archive"), observedAt, 0, null), "Historical replay in progress.");
  assert.doesNotMatch(activityMessage(worker("retrying"), observedAt, 0, estimate), /time remaining/);
  assert.doesNotMatch(activityMessage(worker("live"), observedAt, 0, estimate), /time remaining/);
  const old = { ...estimate, measuredAt: "2026-10-05T11:59:00Z" };
  assert.equal(activityMessage(worker("archive"), observedAt, 0, old), "Historical replay in progress.");
  assert.doesNotMatch(activityMessage(worker("archive"), observedAt, 60_000, estimate), /time remaining/);
  for (const invalid of [
    { ...estimate, remainingSeconds: -1 }, { ...estimate, remainingSeconds: Infinity },
    { ...estimate, remainingSeconds: "30" }, { ...estimate, measuredAt: "invalid" },
  ]) {
    value.monitor.archiveEstimate = invalid;
    assert.equal(isProcessingStatus(value), false);
  }
});

test("archive estimates remain visible between two-minute bursts but expire after ten minutes", () => {
  const estimate = { remainingSeconds: 900, measuredAt: "2026-10-05T12:00:00Z" };
  const current = { ...worker("archive"), heartbeatAt: "2026-10-05T12:02:03Z" };
  assert.match(activityMessage(current, "2026-10-05T12:02:03Z", 0, estimate), /Approximate time remaining: 15 minutes/);
  current.heartbeatAt = "2026-10-05T12:09:59Z";
  assert.match(activityMessage(current, "2026-10-05T12:09:59Z", 0, estimate), /Approximate time remaining/);
  current.heartbeatAt = "2026-10-05T12:10:00Z";
  assert.equal(activityMessage(current, "2026-10-05T12:10:00Z", 0, estimate), "Historical replay in progress.");
  assert.match(activityMessage(current, "2026-10-05T12:11:00Z", 0, estimate), /Heartbeat stale/);
});

test("server API fetch is uncached, validates data, and initial errors explicitly remain unknown", async (context) => {
  context.mock.method(console, "error", () => {});
  const previous = process.env.API_BASE_URL;
  process.env.API_BASE_URL = "http://private-api.example";
  context.after(() => {
    if (previous === undefined) delete process.env.API_BASE_URL;
    else process.env.API_BASE_URL = previous;
  });
  let body = status("archive", "completed");
  let code = 200;
  context.mock.method(global, "fetch", async (url, options) => {
    assert.equal(url.href, "http://private-api.example/statistics/processing-status");
    assert.equal(options.cache, "no-store");
    assert.ok(options.signal instanceof AbortSignal);
    return response(body, code);
  });
  const api = modules()("lib\\api.ts");
  assert.deepEqual(await api.getProcessingStatus(), body);
  body = {};
  await assert.rejects(api.getProcessingStatus(), /invalid processing status/);
  code = 503;
  const initial = await api.getInitialProcessingStatus();
  assert.equal(initial.status, null);
  assert.match(initial.error, /activity is unknown/);
  assert.equal(console.error.mock.callCount(), 1);
});

test("public proxy returns no-store status or explicit failure and forwards cancellation", async (context) => {
  context.mock.method(console, "error", () => {});
  let fail = false;
  const { GET } = modules({
    "@/lib/api": { getProcessingStatus: async (signal) => {
      assert.ok(signal instanceof AbortSignal);
      if (fail) throw new Error("private database unavailable");
      return status();
    } },
  })("app\\api\\statistics\\processing-status\\route.ts");
  const request = new Request("http://web.example/api/statistics/processing-status");
  const success = await GET(request);
  assert.equal(success.headers.get("cache-control"), "no-store");
  assert.deepEqual(await success.json(), status());
  fail = true;
  const failure = await GET(request);
  assert.equal(failure.status, 502);
  assert.equal(failure.headers.get("cache-control"), "no-store");
  assert.deepEqual(await failure.json(), { error: "Unable to load processing status." });
  const controller = new AbortController();
  controller.abort();
  assert.equal((await GET(new Request(request.url, { signal: controller.signal }))).status, 499);
});

function harness(context) {
  const states = [], effects = [], refs = [];
  let stateIndex = 0, effectIndex = 0, refIndex = 0, idIndex = 0, now = 0;
  let visible = "visible";
  const timers = new Map(), intervals = new Map(), listeners = new Set();
  let timerId = 0;
  context.mock.method(performance, "now", () => now);
  context.mock.method(global, "setTimeout", (callback, duration) => {
    assert.equal(duration, 30_000, "Polling must retain the documented 30-second cadence.");
    timers.set(++timerId, { callback, dueAt: now + duration });
    return timerId;
  });
  context.mock.method(global, "clearTimeout", (id) => timers.delete(id));
  context.mock.method(global, "setInterval", (callback, duration) => {
    assert.equal(duration, 1_000, "The refresh countdown must update once per second.");
    intervals.set(++timerId, { callback, duration, dueAt: now + duration });
    return timerId;
  });
  context.mock.method(global, "clearInterval", (id) => intervals.delete(id));
  const previousDocument = global.document;
  global.document = {
    get visibilityState() { return visible; },
    addEventListener(event, callback) { assert.equal(event, "visibilitychange"); listeners.add(callback); },
    removeEventListener(event, callback) { listeners.delete(callback); },
  };
  context.after(() => { global.document = previousDocument; });
  const hooks = {
    ...React,
    useState(initial) {
      const index = stateIndex++;
      if (!(index in states)) states[index] = initial;
      return [states[index], (value) => { states[index] = value; }];
    },
    useRef(initial) {
      const index = refIndex++;
      if (!(index in refs)) refs[index] = { current: initial };
      return refs[index];
    },
    useId() { return `processing-status-${idIndex++}`; },
    useEffect(action, deps) {
      const index = effectIndex++, previous = effects[index];
      if (!previous || deps.some((dep, i) => dep !== previous.deps[i])) {
        effects[index] = { deps, action, cleanup: previous?.cleanup, pending: true };
      }
    },
  };
  const { ProcessingStatus } = modules({ react: hooks })("components\\processing-status.tsx");
  let initial = { status: status("archive", "backfill-running"), error: null };
  return {
    render(next = initial) {
      initial = next;
      stateIndex = effectIndex = refIndex = idIndex = 0;
      return renderToStaticMarkup(ProcessingStatus({ initial }));
    },
    commit() {
      for (const effect of effects) if (effect.pending) {
        effect.cleanup?.();
        effect.cleanup = effect.action();
        effect.pending = false;
      }
    },
    tick(duration = 30_000) {
      assert.equal(timers.size, 1, "Only one status poll may be scheduled.");
      this.advance(duration);
    },
    advance(duration) {
      const target = now + duration;
      while (true) {
        const nextTimeout = [...timers.entries()].sort((a, b) => a[1].dueAt - b[1].dueAt)[0];
        const nextInterval = [...intervals.entries()].sort((a, b) => a[1].dueAt - b[1].dueAt)[0];
        const timeoutDueAt = nextTimeout?.[1].dueAt ?? Infinity;
        const intervalDueAt = nextInterval?.[1].dueAt ?? Infinity;
        const dueAt = Math.min(timeoutDueAt, intervalDueAt);
        if (dueAt > target) break;
        now = dueAt;
        if (timeoutDueAt <= intervalDueAt) {
          timers.delete(nextTimeout[0]);
          nextTimeout[1].callback();
        } else {
          nextInterval[1].dueAt += nextInterval[1].duration;
          nextInterval[1].callback();
        }
      }
      now = target;
    },
    visible(value) {
      visible = value ? "visible" : "hidden";
      for (const listener of listeners) listener();
    },
    timerCount() { return timers.size; },
    intervalCount() { return intervals.size; },
    unmount() {
      for (const effect of effects) effect.cleanup?.();
      assert.equal(listeners.size, 0);
      assert.equal(timers.size, 0);
      assert.equal(intervals.size, 0);
    },
  };
}

test("refresh countdown decrements to the scheduled poll and resets after success and failure", async (context) => {
  context.mock.method(console, "error", () => {});
  const requests = [];
  context.mock.method(global, "fetch", () => new Promise((resolve) => requests.push({ resolve })));
  const component = harness(context);
  component.render();
  component.commit();
  assert.match(component.render(), /Next status refresh in 0:30/);
  component.advance(1_000);
  assert.match(component.render(), /Next status refresh in 0:29/);
  component.advance(29_000);
  assert.equal(requests.length, 1);
  assert.match(component.render(), /Refreshing status\.\.\./);

  requests[0].resolve(response(status("live", "completed")));
  await settle();
  assert.match(component.render(), /Next status refresh in 0:30/);
  component.advance(30_000);
  assert.equal(requests.length, 2);
  assert.match(component.render(), /Refreshing status\.\.\./);
  requests[1].resolve(response({}, 503));
  await settle();
  const html = component.render();
  assert.match(html, /Unable to refresh processing status/);
  assert.match(html, /Next status refresh in 0:30/);
  component.unmount();
});

test("refresh countdown pauses while hidden, resumes with an immediate serialized poll, and cleans up", async (context) => {
  const requests = [];
  context.mock.method(global, "fetch", (url, options) =>
    new Promise((resolve) => requests.push({ signal: options.signal, resolve })));
  const component = harness(context);
  component.render();
  component.commit();
  component.advance(5_000);
  assert.match(component.render(), /Next status refresh in 0:25/);

  component.visible(false);
  assert.equal(component.timerCount(), 0);
  assert.equal(component.intervalCount(), 0);
  assert.match(component.render(), /Status refresh paused while this page is hidden/);
  component.visible(true);
  assert.equal(requests.length, 1, "Returning to a visible tab immediately refreshes status.");
  assert.match(component.render(), /Refreshing status\.\.\./);

  component.visible(false);
  assert.ok(requests[0].signal.aborted);
  assert.match(component.render(), /Status refresh paused while this page is hidden/);
  component.visible(true);
  assert.equal(requests.length, 1, "An in-flight aborted request is serialized before resuming.");
  requests[0].resolve(response(status("live", "completed")));
  await settle();
  assert.equal(requests.length, 2, "The pending visible refresh starts after the aborted request settles.");
  requests[1].resolve(response(status("live", "completed")));
  await settle();
  assert.match(component.render(), /Next status refresh in 0:30/);
  assert.equal(component.intervalCount(), 1);
  component.unmount();
});

test("localized timestamps preserve the server fallback and use browser formatting after hydration", () => {
  const server = modules()("components\\localized-time.tsx");
  const html = renderToStaticMarkup(React.createElement(server.LocalizedTime, { timestamp: observedAt }));
  assert.match(html, /2026-10-05T12:00:00\+00:00 \(UTC\)/);
  const client = modules({ react: {
    ...React, useSyncExternalStore: (_subscribe, getSnapshot) => getSnapshot(),
  } })("components\\localized-time.tsx");
  const clientHtml = renderToStaticMarkup(React.createElement(client.LocalizedTime, { timestamp: observedAt }));
  assert.doesNotMatch(clientHtml, /\(UTC\)/);
  assert.match(clientHtml, /<time dateTime="2026-10-05T12:00:00\+00:00">/);
  assert.equal(renderToStaticMarkup(React.createElement(client.LocalizedTime, { timestamp: null })), "not recorded");
});

test("client hides the parent-URI section when there are no outstanding rows", (context) => {
  const component = harness(context);
  const initial = { status: status(), error: null };
  const html = component.render(initial);
  assert.match(html, /Not started \/ unknown/);
  assert.doesNotMatch(html, /Parent URIs:|parent-URI rows/);
  assert.doesNotMatch(html, /run completed|in progress/);
  const details = html.match(/<dialog\b[\s\S]*?<\/dialog>/)?.[0];
  assert.match(details, /Parent-URI backfill/);
  assert.match(details, /Activity started: not recorded \(never run\)\./);
  assert.match(details, /Activity finished: not recorded \(never run\)\./);
  assert.match(details, /Parent-URI backfill<\/h3><p>Never run\.<\/p><p>Activity started:/);
  assert.doesNotMatch(details.slice(details.indexOf("Parent-URI backfill"), details.indexOf("Handle refresh")), /Outcome:/);
  component.commit();
  component.unmount();
});

test("handle refresh details follow parent backfill, precede refresh note, and never expand the compact summary", (context) => {
  const component = harness(context);
  const initial = { status: status("live", "completed"), error: null };
  initial.status.handleRefresh = {
    activity: worker("handle-refresh-waiting"), pending: 1234, due: 12, nextDueAt: observedAt,
  };
  const html = component.render(initial);
  const details = html.match(/<dialog\b[\s\S]*?<\/dialog>/)?.[0];
  const summary = html.slice(0, html.indexOf("<dialog"));
  assert.doesNotMatch(summary, /Handle refresh|refreshes scheduled|rate limit/);
  assert.doesNotMatch(summary, /Heartbeat|heartbeat|♥|parent URIs/);
  assert.match(details, /Parent-URI backfill[\s\S]*Handle refresh[\s\S]*Next status refresh in 0:30/);
  assert.match(details, /aria-live="off" class="pt-2 text-right text-xs"/);
  assert.doesNotMatch(details.slice(details.indexOf("Handle refresh")), /Activity finished:|Scheduled rows include/);
  assert.match(details, /Handle refresh<\/h3><p>Active \(request pacing \/ rate-limit wait\)\.<\/p>/);
  assert.doesNotMatch(details.slice(details.indexOf("Handle refresh")), /Outcome:/);
  assert.match(details, /Handle refresh heartbeat:/);
  assert.match(details, /Jetstream heartbeat:/);
  assert.match(details, /Parent-URI heartbeat:/);
  assert.match(details, /Last phase change:/);
  assert.match(details, /1,234 refreshes scheduled \(12 due\)/);
  assert.doesNotMatch(details, /queue counts do not prove worker activity/);
  component.commit();
  component.unmount();
});

test("client polls status only, serializes visible resumes, cancels hidden/unmounted work and never replaces data with old results", async (context) => {
  const requests = [];
  context.mock.method(global, "fetch", (url, options) => {
    assert.equal(url, "/api/statistics/processing-status", "Polling must never fetch charts or summary statistics.");
    assert.equal(options.cache, "no-store");
    return new Promise((resolve) => requests.push({ signal: options.signal, resolve }));
  });
  const component = harness(context);
  assert.match(component.render(), /Historical replay in progress/);
  component.commit();
  assert.equal(requests.length, 0);
  component.tick();
  assert.equal(requests.length, 1);
  assert.equal(component.timerCount(), 0, "No further polling until the first request settles.");
  component.visible(false);
  assert.ok(requests[0].signal.aborted);
  component.visible(true);
  assert.equal(requests.length, 1, "The cancelled request must settle before the immediate visibility refresh.");
  requests[0].resolve(response(status("failure", "failure")));
  await settle();
  assert.equal(requests.length, 2);
  requests[1].resolve(response(status("live", "completed")));
  await settle();
  const html = component.render();
  assert.match(html, /Listening to live events/);
  assert.doesNotMatch(html, /Parent URIs:/);
  assert.doesNotMatch(html, /run failed/);
  component.tick();
  component.unmount();
  assert.ok(requests[2].signal.aborted);
  requests[2].resolve(response(status("archive", "backfill-running")));
  await settle();
  assert.equal(component.timerCount(), 0);
});

test("failed polls show explicit errors, retain counts, and age cached running statuses into stale", async (context) => {
  context.mock.method(console, "error", () => {});
  context.mock.method(global, "fetch", async () => response({}, 503));
  const component = harness(context);
  const initial = { status: status("archive", "backfill-running"), error: null };
  initial.status.parentUriBackfill.pending = 12;
  initial.status.parentUriBackfill.unavailable = 3;
  initial.status.handleRefresh = {
    activity: worker("handle-refresh-running"), pending: 42, due: 7, nextDueAt: observedAt,
  };
  component.render(initial);
  component.commit();
  component.tick();
  await settle();
  assert.match(component.render(), /Unable to refresh processing status/);
  component.tick();
  await settle();
  const html = component.render();
  assert.match(html, /Heartbeat stale; activity unknown/);
  assert.match(html, /12 parent-URI rows remaining/);
  assert.match(html, /3 unavailable \(not retried\)/);
  assert.match(html, /42 refreshes scheduled \(7 due\)/);
  assert.match(html, /Activity stale; the run may have been interrupted \(last phase: handle-refresh-running\)/);
  assert.doesNotMatch(html, /in progress/);
  assert.equal(console.error.mock.callCount(), 2);
  component.unmount();
});

test("router prop refresh replaces status and aborts polls without resetting errors to successful defaults", async (context) => {
  const requests = [];
  context.mock.method(console, "error", () => {});
  context.mock.method(global, "fetch", (url, options) =>
    new Promise((resolve) => requests.push({ signal: options.signal, resolve })));
  const component = harness(context);
  component.render();
  component.commit();
  component.tick();
  component.render({ status: status("stopped", "completed"), error: null });
  component.commit();
  assert.ok(requests[0].signal.aborted);
  requests[0].resolve(response(status("archive", "backfill-running")));
  await settle();
  const html = component.render();
  assert.match(html, /Stopped; saved work/);
  assert.doesNotMatch(html, /Parent URIs:/);
  assert.doesNotMatch(html, /in progress/);
  component.unmount();
});

test("client keeps the main Jetstream summary concise and retains the approximate duration in details", (context) => {
  const component = harness(context);
  const initial = { status: status("archive", "completed"), error: null };
  initial.status.monitor.archiveEstimate = { remainingSeconds: 7380, measuredAt: observedAt };
  const html = component.render(initial);
  const summary = html.match(/<button\b[^>]*aria-label="Processing status details"[^>]*>[\s\S]*?<\/button>/)?.[0];
  const details = html.match(/<dialog\b[\s\S]*?<\/dialog>/)?.[0];
  assert.ok(summary, "The status summary must be an accessible dialog trigger.");
  assert.match(summary, /class="ml-auto block w-fit max-w-full rounded-lg text-right/);
  assert.match(summary, /Jetstream:<\/strong> Historical replay in progress\./);
  assert.doesNotMatch(summary, /Approximate time remaining/);
  assert.match(summary, /View detailed status/);
  assert.match(details, /aria-labelledby="processing-status-0"/);
  assert.match(details, /text-left/);
  assert.match(details, /Processing status details/);
  assert.match(details, /<h3 class="pt-2 font-medium text-foreground">Jetstream processing<\/h3>/);
  assert.match(details, /<h3 class="pt-2 font-medium text-foreground">Jetstream processing<\/h3><p>Historical replay in progress\. Approximate time remaining: 2 hours 3 minutes\./);
  assert.doesNotMatch(details, /Jetstream:<\/strong>/);
  assert.match(details, /Activity started: <time dateTime="2026-10-05T12:00:00\+00:00">/);
  assert.match(details, /Activity finished: <time dateTime="2026-10-05T12:00:00\+00:00">/);
  assert.match(details, /Parent-URI backfill<\/h3><p>Completed\.<\/p><p>Activity started:/);
  assert.doesNotMatch(details.slice(details.indexOf("Parent-URI backfill"), details.indexOf("Handle refresh")), /Outcome:/);
  assert.match(details, /Next status refresh in 0:30/);
  component.commit();
  component.unmount();
});

test("client displays archive stall progress and scheduled retry timestamps without hiding heartbeat", (context) => {
  const component = harness(context);
  const initial = { status: status("archive", "backfill-running"), error: null };
  initial.status.parentUriBackfill.pending = 5;
  initial.status.parentUriBackfill.retryPending = 2;
  initial.status.parentUriBackfill.retryDue = 1;
  initial.status.parentUriBackfill.resolved = 3;
  initial.status.parentUriBackfill.unavailable = 4;
  initial.status.monitor.archiveReplay = {
    noProgressSince: "2026-10-05T11:45:00Z", lastProgressAt: "2026-10-05T11:45:00Z",
    stalledSince: "2026-10-05T11:50:00Z", consecutiveGenerationMismatches: 7,
    nextRetryAt: "2026-10-05T12:05:00Z",
  };
  initial.status.monitor.archiveEstimate = { remainingSeconds: 7380, measuredAt: observedAt };
  const html = component.render(initial);
  const summary = html.match(/<button\b[^>]*aria-label="Processing status details"[^>]*>[\s\S]*?<\/button>/)?.[0];
  const details = html.match(/<dialog\b[\s\S]*?<\/dialog>/)?.[0];
  assert.doesNotMatch(html.slice(0, html.indexOf("<dialog")), /aria-label="Heartbeat"|♥|parent URIs/);
  assert.match(details, /Jetstream heartbeat:/);
  assert.match(summary, /Archive stalled/);
  assert.match(details, /Last successful processing \/ durable progress/);
  assert.match(details, /dateTime="2026-10-05T11:45:00Z"/);
  assert.match(details, /Next scheduled retry/);
  assert.match(details, /dateTime="2026-10-05T12:05:00Z"/);
  assert.ok(details.indexOf("Next scheduled retry") < details.indexOf("Jetstream heartbeat"));
  assert.ok(details.indexOf("Jetstream heartbeat") < details.indexOf("Parent-URI backfill"));
  assert.doesNotMatch(summary, /Approximate time remaining/);
  assert.doesNotMatch(details, /Approximate time remaining/);
  assert.match(details, /Parent-URI heartbeat/);
  assert.match(details, /7 parent-URI rows remaining/);
  assert.match(details, /5 pending; 2 retryable \(1 due\); 3 resolved; 4 unavailable \(not retried\)/);
  component.commit();
  component.unmount();
});

test("initial errors remain explicit and a hidden page waits until visible to recover", async (context) => {
  let calls = 0;
  context.mock.method(global, "fetch", async () => {
    calls++;
    return response(status("live", "completed"));
  });
  const component = harness(context);
  component.visible(false);
  const html = component.render({ status: null, error: "Unable to load processing status. Worker activity is unknown." });
  assert.match(html, /role="alert"/);
  assert.match(html, /activity is unknown/);
  assert.doesNotMatch(html, /in progress|run completed/);
  component.commit();
  assert.equal(component.timerCount(), 0);
  assert.equal(calls, 0);
  assert.match(component.render(), /Status refresh paused while this page is hidden/);
  component.visible(true);
  await settle();
  assert.equal(calls, 1);
  assert.match(component.render(), /Listening to live events/);
  assert.doesNotMatch(component.render(), /role="alert"/);
  component.unmount();
});

test("failed manual server refresh retains old data without resetting its heartbeat age", async (context) => {
  context.mock.method(console, "error", () => {});
  context.mock.method(global, "fetch", async () => response({}, 503));
  const component = harness(context);
  component.render();
  component.commit();
  component.tick(60_000);
  await settle();
  component.render({ status: null, error: "Unable to load processing status." });
  component.commit();
  await settle();
  const html = component.render();
  assert.match(html, /Unable to refresh processing status/);
  assert.match(html, /Heartbeat stale; activity unknown/);
  assert.doesNotMatch(html, /in progress/);
  component.unmount();
});
