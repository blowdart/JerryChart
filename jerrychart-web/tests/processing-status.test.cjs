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
    const file = path.join(root, "src", relative);
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

const { isProcessingStatus, activityMessage } = modules()("lib\\processing-status.ts");
const observedAt = "2026-10-05T12:00:00+00:00";
const worker = (phase = "not-started") => ({
  phase, state: phase, isRunning: ["archive", "live", "backfill-running", "retrying"].includes(phase),
  startedAt: phase === "not-started" ? null : observedAt,
  changedAt: phase === "not-started" ? null : observedAt,
  heartbeatAt: phase === "not-started" ? null : observedAt,
  finishedAt: ["completed", "stopped", "failure"].includes(phase) ? observedAt : null,
});
const status = (phase = "not-started", parentPhase = "not-started") => ({
  observedAt,
  monitor: { activity: worker(phase), checkpointUpdatedAt: null, archiveEstimate: null },
  parentUriBackfill: { activity: worker(parentPhase), pending: 0, retryPending: 0, retryDue: 0, resolved: 0, unavailable: 0 },
});
const response = (body, code = 200) => new Response(JSON.stringify(body), { status: code });
const settle = () => new Promise((resolve) => setImmediate(resolve));

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
  let stateIndex = 0, effectIndex = 0, refIndex = 0, now = 0;
  let visible = "visible";
  const timers = new Map(), listeners = new Set();
  let timerId = 0;
  context.mock.method(performance, "now", () => now);
  context.mock.method(global, "setTimeout", (callback, duration) => {
    assert.equal(duration, 30_000, "Polling must retain the documented 30-second cadence.");
    timers.set(++timerId, callback);
    return timerId;
  });
  context.mock.method(global, "clearTimeout", (id) => timers.delete(id));
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
      stateIndex = effectIndex = refIndex = 0;
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
      now += duration;
      assert.equal(timers.size, 1, "Only one status poll may be scheduled.");
      const [id, callback] = timers.entries().next().value;
      timers.delete(id);
      callback();
    },
    visible(value) {
      visible = value ? "visible" : "hidden";
      for (const listener of listeners) listener();
    },
    timerCount() { return timers.size; },
    unmount() { for (const effect of effects) effect.cleanup?.(); assert.equal(listeners.size, 0); },
  };
}

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

test("client places the approximate archive duration in the Jetstream status", (context) => {
  const component = harness(context);
  const initial = { status: status("archive"), error: null };
  initial.status.monitor.archiveEstimate = { remainingSeconds: 7380, measuredAt: observedAt };
  const html = component.render(initial);
  assert.match(html, /Jetstream:<\/strong> Historical replay in progress\. Approximate time remaining: 2 hours 3 minutes/);
  assert.doesNotMatch(html, /Archive total and completion time are unknown/);
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
