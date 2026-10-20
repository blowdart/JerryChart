const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { createRequire } = require("node:module");
const test = require("node:test");
const { transformSync } = require("esbuild");
const React = require("react");
const { renderToStaticMarkup } = require("react-dom/server");

const root = path.resolve(__dirname, "..");

function loadModule(relative, overrides = {}) {
  const cache = new Map();
  function load(file) {
    const absolute = path.join(root, "src", ...file.split(/[\\/]/));
    if (cache.has(absolute)) return cache.get(absolute).exports;
    const module = { exports: {} };
    cache.set(absolute, module);
    const nativeRequire = createRequire(absolute);
    const requireModule = (name) => {
      if (Object.hasOwn(overrides, name)) return overrides[name];
      if (!name.startsWith("@/")) return nativeRequire(name);
      const modulePath = name.slice(2);
      const extension = fs.existsSync(path.join(root, "src", `${modulePath}.ts`)) ? ".ts" : ".tsx";
      return load(modulePath + extension);
    };
    const compiled = transformSync(fs.readFileSync(absolute, "utf8"), {
      loader: absolute.endsWith(".tsx") ? "tsx" : "ts",
      format: "cjs",
      jsx: "automatic",
      target: "es2022",
      sourcefile: absolute,
    }).code;
    new Function("require", "module", "exports", compiled)(requireModule, module, module.exports);
    return module.exports;
  }
  return load(relative);
}

test("home page explains deleted-reply retention between top posts and history/users", async () => {
  const TopReplyPosts = () => null;
  const noop = () => null;
  const { default: Home } = loadModule("app\\page.tsx", {
    "next/image": { default: noop },
    "@/components/refresh-statistics": { RefreshStatistics: noop },
    "@/components/processing-status": { ProcessingStatus: noop },
    "@/components/profile-hover-card": { ProfileHoverCard: noop },
    "@/components/reply-history-dialog": { ReplyHistoryDialog: noop },
    "@/components/reply-authors-dialog": { ReplyAuthorsDialog: noop },
    "@/components/statistics-last-updated": { StatisticsLastUpdated: noop },
    "@/components/top-reply-posts": { TopReplyPosts },
    "@/lib/api": {
      getReplySummary: async () => ({ totalReplies: 12, rightJerryReplies: 12, wrongJerryReplies: 0 }),
      getTopRightJerryAuthors: async () => [],
      getMonthlyRightJerryReplies: async () => [],
      getStatisticsLastUpdated: async () => ({}),
      getTopRightJerryPosts: async () => [],
      getInitialProcessingStatus: async () => ({}),
    },
  });
  const page = await Home();
  const markup = renderToStaticMarkup(page);
  assert.ok(!markup.includes("(c) 2026"));
  assert.ok(!markup.includes("Made with"));
  const children = React.Children.toArray(page.props.children);
  const topIndex = children.findIndex(child => child.type === TopReplyPosts);
  assert.ok(topIndex >= 0);
  const caveat = children[topIndex + 1];
  assert.equal(caveat.type, "p");
  assert.equal(caveat.props.children,
    '* "Jerry No" replies that are subsequently deleted are not removed from the analysis.');
  const tables = renderToStaticMarkup(children[topIndex + 2]);
  assert.match(tables, /last six months/);
  assert.match(tables, /Top 10 users/);
});

test("root footer links the embedded build commit or identifies a development build", () => {
  const originalSha = process.env.NEXT_PUBLIC_BUILD_COMMIT;
  const sha = "f6fde58037effd58d8c2791e0852d327394c7d53";
  try {
    process.env.NEXT_PUBLIC_BUILD_COMMIT = sha;
    const loadLayout = () => loadModule("app\\layout.tsx", {
      "next/font/google": {
        Geist: () => ({ variable: "font-sans" }),
        Geist_Mono: () => ({ variable: "font-mono" }),
      },
      "next/link": ({ href, children, ...props }) => React.createElement("a", { href, ...props }, children),
      "./globals.css": {},
    }).default;
    const commitLayout = loadLayout();
    const commitMarkup = renderToStaticMarkup(React.createElement(commitLayout, { children: "Page content" }));
    assert.match(commitMarkup, new RegExp(`href="https://github\\.com/blowdart/JerryChart/commit/${sha}"`));
    assert.match(commitMarkup, /Build f6fde58/);
    assert.ok(commitMarkup.indexOf('aria-label="Legal"') < commitMarkup.indexOf("Get the source code on GitHub"));
    assert.ok(commitMarkup.indexOf("Get the source code on GitHub") < commitMarkup.indexOf("Build f6fde58"));

    delete process.env.NEXT_PUBLIC_BUILD_COMMIT;
    const developmentLayout = loadLayout();
    const developmentMarkup = renderToStaticMarkup(
      React.createElement(developmentLayout, { children: "Page content" }),
    );
    assert.match(developmentMarkup, /Development build/);
    assert.doesNotMatch(developmentMarkup, /JerryChart\/commit\//);
  } finally {
    if (originalSha === undefined) {
      delete process.env.NEXT_PUBLIC_BUILD_COMMIT;
    } else {
      process.env.NEXT_PUBLIC_BUILD_COMMIT = originalSha;
    }
  }
});

test("processing status places the last detection directly below its heading", () => {
  const hooks = {
    ...React,
    useEffect() {},
    useId() { return "processing-status-title"; },
    useRef() { return { current: null }; },
    useState(initial) { return [initial, () => {}]; },
  };
  const RefreshStatistics = () => React.createElement("button", { type: "button" }, "Refresh");
  const { ProcessingStatus } = loadModule("components\\processing-status.tsx", {
    react: hooks,
    "@/components/refresh-statistics": { RefreshStatistics },
  });
  for (const lastDetectedAt of [null, "2026-10-19T12:00:00Z"]) {
    const tree = ProcessingStatus({
      initial: { status: null, error: null }, lastDetectedAt,
      lastDetectedPostAtUri: lastDetectedAt === null
        ? null : "at://did:plc:author/app.bsky.feed.post/matching-reply",
    });
    const box = React.Children.toArray(tree.props.children).find(child => child.type === "div");
    const content = React.Children.toArray(box.props.children);
    assert.equal(content[0].props.children, "Processing status");
    assert.equal(content[1].props.updatedAt, lastDetectedAt);
    const refreshIndex = content.findIndex(child => child.type === RefreshStatistics);
    assert.ok(refreshIndex > 1);
    assert.equal(content[refreshIndex + 1].type, "button");
    assert.equal(content[refreshIndex + 1].props["aria-label"], "Processing status details");
    const markup = renderToStaticMarkup(box);
    assert.doesNotMatch(markup, /<button\b[^>]*>[\s\S]*<button\b[\s\S]*<\/button>[\s\S]*<\/button>/);
    assert.match(markup, /<strong>&quot;Jerry No&quot;<\/strong> last detected:/);
    assert.doesNotMatch(markup, /<p\b/);
    if (lastDetectedAt === null) {
      assert.match(markup, /not yet recorded/);
    } else {
      assert.match(markup, /href="https:\/\/bsky\.app\/profile\/did:plc:author\/post\/matching-reply"/);
      assert.match(markup, /dateTime="2026-10-19T12:00:00Z"/i);
      assert.match(markup, /<a\b[^>]*href="https:\/\/bsky\.app\/profile\/did:plc:author\/post\/matching-reply"[^>]*><time[^>]*dateTime="2026-10-19T12:00:00Z"[^>]*>.*?<\/time><span class="sr-only"> \(view the matching reply on Bluesky\)<\/span><\/a>/);
      assert.doesNotMatch(markup, /aria-label="View the matching Jerry no reply on Bluesky"/);
    }
  }
});

test("last-detected timestamp keeps its empty states and never server-renders third-party previews", () => {
  const { StatisticsLastUpdated } = loadModule("components\\statistics-last-updated.tsx");
  for (const props of [
    { updatedAt: null, atUri: null },
    { updatedAt: "2026-10-19T12:00:00Z", atUri: null },
    { updatedAt: "2026-10-19T12:00:00Z", atUri: "at://did:plc:author/app.bsky.feed.post/reply" },
  ]) {
    const markup = renderToStaticMarkup(React.createElement(StatisticsLastUpdated, props));
    assert.doesNotMatch(markup, /iframe|embed\.bsky\.app|<script/);
    assert.equal(markup.includes("<time"), props.updatedAt !== null);
    assert.equal(markup.includes("<a "), props.atUri !== null);
    assert.equal(markup.includes("not yet recorded"), props.updatedAt === null);
  }
});

function replyPreviewHarness() {
  const states = [], refs = [], effects = [];
  let stateIndex = 0, refIndex = 0, effectIndex = 0;
  const listeners = new Map(), timers = new Map();
  let nextTimer = 0;
  const source = {};
  let focused = 0;
  const previousWindow = global.window;
  global.window = {
    addEventListener(name, handler) { listeners.set(name, handler); },
    removeEventListener(name, handler) { if (listeners.get(name) === handler) listeners.delete(name); },
    setTimeout(callback, delay) {
      const id = ++nextTimer;
      timers.set(id, { callback, delay });
      return id;
    },
    clearTimeout(id) { timers.delete(id); },
  };
  const hooks = {
    ...React,
    useId() { return "reply-preview-id"; },
    useRef(initial) {
      const index = refIndex++;
      refs[index] ??= { current: initial };
      return refs[index];
    },
    useState(initial) {
      const index = stateIndex++;
      if (!(index in states)) states[index] = initial;
      return [states[index], value => {
        states[index] = typeof value === "function" ? value(states[index]) : value;
      }];
    },
    useEffect(callback, deps) {
      const index = effectIndex++;
      const prior = effects[index];
      if (prior && deps.every((value, i) => Object.is(value, prior.deps[i]))) return;
      prior?.cleanup?.();
      effects[index] = { deps, cleanup: callback() };
    },
  };
  const { PostHoverCard } = loadModule("components\\post-hover-card.tsx", {
    react: hooks,
    "@base-ui/react/preview-card": {
      PreviewCard: { Root: "section", Trigger: "a", Portal: React.Fragment, Positioner: "div", Popup: "aside" },
    },
  });
  function find(node, type) {
    if (Array.isArray(node)) return node.map(child => find(child, type)).find(Boolean);
    if (!React.isValidElement(node)) return null;
    return node.type === type ? node : find(node.props.children, type);
  }
  return {
    source, timers, listeners, find,
    get focused() { return focused; },
    render(atUri = "at://did:plc:erlxz4sppc5uv7m4efmcvpsf/app.bsky.feed.post/3my5n4tyxfcud") {
      stateIndex = refIndex = effectIndex = 0;
      const tree = PostHoverCard({ atUri, children: "timestamp" });
      find(tree, "a").props.ref.current = { focus() { focused++; } };
      const iframe = find(tree, "iframe");
      if (iframe) iframe.props.ref.current = { contentWindow: source };
      return tree;
    },
    dispose() {
      effects.forEach(effect => effect.cleanup?.());
      global.window = previousWindow;
    },
  };
}

test("reply preview loads only the official privacy-enforcing iframe on demand and removes it on close", () => {
  const harness = replyPreviewHarness();
  const originalFetch = global.fetch;
  global.fetch = () => { throw new Error("JerryChart must not fetch reply preview content."); };
  try {
    let tree = harness.render();
    const link = harness.find(tree, "a");
    assert.equal(link.props.href,
      "https://bsky.app/profile/did:plc:erlxz4sppc5uv7m4efmcvpsf/post/3my5n4tyxfcud");
    assert.equal(harness.find(tree, "iframe"), undefined);
    assert.equal(harness.timers.size, 0);
    tree.props.onOpenChange(true);
    tree = harness.render();
    const iframe = harness.find(tree, "iframe");
    const url = new URL(iframe.props.src);
    assert.equal(url.origin, "https://embed.bsky.app");
    assert.equal(url.pathname,
      "/embed/did:plc:erlxz4sppc5uv7m4efmcvpsf/app.bsky.feed.post/3my5n4tyxfcud");
    assert.equal(url.searchParams.get("id"), "reply-preview-id");
    assert.equal(url.searchParams.get("colorMode"), "system");
    assert.equal(url.searchParams.has("ref_url"), false);
    assert.equal(iframe.props.referrerPolicy, "no-referrer");
    assert.equal(iframe.props.title, "Matching reply on Bluesky");
    assert.equal(iframe.props.sandbox,
      "allow-scripts allow-same-origin allow-popups allow-popups-to-escape-sandbox");
    assert.equal(harness.find(tree, "script"), undefined);
    assert.equal(harness.find(tree, "img"), undefined);
    assert.equal(harness.find(tree, "aside").props["aria-label"], "Matching reply preview");
    assert.match(harness.find(tree, "aside").props.className, /max-h-\[var\(--available-height\)\]/);
    const close = harness.find(tree, "button");
    assert.equal(close.props["aria-label"], "Close reply preview");
    assert.equal(close.props.title, close.props["aria-label"]);
    assert.equal(close.props.children.props["aria-hidden"], "true");
    assert.match(renderToStaticMarkup(close), /<svg/);
    assert.doesNotMatch(renderToStaticMarkup(close), />Close/);
    assert.match(close.props.className, /size-8/);
    assert.match(close.props.className, /focus-visible:outline-foreground/);
    close.props.onClick();
    assert.equal(harness.focused, 1);
    tree = harness.render();
    assert.equal(harness.find(tree, "iframe"), undefined);
    assert.equal(harness.listeners.size, 0);
    assert.equal(harness.timers.size, 0);
    tree.props.onOpenChange(true);
    tree = harness.render();
    assert.equal(harness.find(tree, "iframe").props.height, 400);
    assert.equal(harness.find(tree, "p").props.role, "status");
    assert.equal(harness.timers.size, 1);
    assert.ok(harness.find(tree, "iframe"));
    // Base UI routes keyboard Escape and pointer dismissal through this callback.
    tree.props.onOpenChange(false);
    assert.equal(harness.find(harness.render(), "iframe"), undefined);
  } finally {
    global.fetch = originalFetch;
    harness.dispose();
  }
});

test("image previews resize after media loads while retaining keyboard-accessible bounded scrolling", () => {
  const harness = replyPreviewHarness();
  try {
    harness.render().props.onOpenChange(true);
    let tree = harness.render();
    const region = harness.find(tree, "iframe").props;
    assert.match(region.className, /min-w-\[300px\]/);
    function findRegion(node) {
      if (Array.isArray(node)) return node.map(findRegion).find(Boolean);
      if (!React.isValidElement(node)) return null;
      return node.props.role === "region" ? node : findRegion(node.props.children);
    }
    const scroller = findRegion(tree);
    assert.equal(scroller.props.tabIndex, 0);
    assert.match(scroller.props["aria-label"], /Scroll/);
    assert.match(scroller.props.className, /max-h-\[60vh\] overflow-auto/);
    assert.match(scroller.props.className, /focus-visible:outline-foreground/);
    for (const height of [220, 850, 1200]) {
      harness.listeners.get("message")({
        origin: "https://embed.bsky.app", source: harness.source,
        data: { id: "reply-preview-id", height },
      });
      tree = harness.render();
      assert.equal(harness.find(tree, "iframe").props.height, height);
      assert.match(harness.find(tree, "aside").props.children.at(-1).props.className, /shrink-0/);
      assert.equal(harness.find(tree, "button").props["aria-label"], "Close reply preview");
    }
  } finally {
    harness.dispose();
  }
});

test("reply preview ignores forged resize messages and rejects unsafe AT URIs", () => {
  const harness = replyPreviewHarness();
  try {
    harness.render().props.onOpenChange(true);
    let tree = harness.render();
    const resize = harness.listeners.get("message");
    const valid = { origin: "https://embed.bsky.app", source: harness.source, data: { id: "reply-preview-id", height: 232.2 } };
    for (const event of [
      { ...valid, origin: "https://attacker.example" },
      { ...valid, source: {} },
      ...[null, "message", {}, { id: "wrong-id", height: 200 },
        ...[0, -1, Infinity, NaN, 10001, "300"].map(height => ({ id: "reply-preview-id", height })),
      ].map(data => ({ ...valid, data })),
    ]) {
      resize(event);
      tree = harness.render();
      assert.equal(harness.find(tree, "iframe").props.height, 400);
    }
    resize(valid);
    tree = harness.render();
    assert.equal(harness.find(tree, "iframe").props.height, 233);
    assert.equal(harness.find(tree, "p"), undefined);
    assert.equal(harness.timers.size, 0);
    harness.find(tree, "iframe").props.ref.current = null;
    resize(valid);
    for (const uri of [
      "https://attacker.example", "at://did:plc:author/app.bsky.feed.post/../other",
      "at://did:plc:author/app.bsky.feed.post/key?ref_url=private",
    ]) {
      assert.throws(() => harness.render(uri), /Invalid Bluesky post URI/);
    }
  } finally {
    harness.dispose();
  }
});

test("reply preview reports load failures, retains navigation, clears timeouts and resets on reopen", () => {
  const harness = replyPreviewHarness();
  try {
    harness.render().props.onOpenChange(true);
    let tree = harness.render();
    assert.equal(harness.find(tree, "p").props.role, "status");
    const timer = [...harness.timers.values()][0];
    assert.equal(timer.delay, 15000);
    harness.find(tree, "iframe").props.onLoad?.();
    tree = harness.render();
    assert.equal(harness.find(tree, "p").props.role, "status");
    assert.equal(harness.timers.size, 1,
      "An iframe load event is not evidence that the embed has rendered.");
    timer.callback();
    tree = harness.render();
    assert.equal(harness.find(tree, "p").props.role, "alert");
    assert.match(harness.find(tree, "p").props.children, /Unable to load/);
    assert.match(harness.find(tree, "aside").props.children.at(-1).props.href, /^https:\/\/bsky\.app\//);
    harness.find(tree, "iframe").props.onLoad?.();
    assert.equal(harness.find(harness.render(), "p").props.role, "alert");
    harness.listeners.get("message")({
      origin: "https://embed.bsky.app", source: harness.source,
      data: { id: "reply-preview-id", height: 250 },
    });
    tree = harness.render();
    assert.equal(harness.find(tree, "p"), undefined);
    assert.equal(harness.timers.size, 0);
    harness.find(tree, "iframe").props.onError();
    assert.equal(harness.find(harness.render(), "p").props.role, "alert");
    tree.props.onOpenChange(false);
    tree = harness.render();
    tree.props.onOpenChange(true);
    tree = harness.render();
    assert.equal(harness.find(tree, "p").props.role, "status");
  } finally {
    harness.dispose();
  }
});

test("detail dialogs expose icon-only close buttons with accessible names, tooltips and focus indicators", () => {
  let closed = 0;
  const hooks = {
    ...React,
    useEffect() {},
    useId() { return "details-title"; },
    useRef() { return { current: { close() { closed++; } } }; },
    useState(initial) { return [initial, () => {}]; },
  };
  const { StatisticsDialog } = loadModule("components\\statistics-dialog.tsx", { react: hooks });
  const { ProcessingStatus } = loadModule("components\\processing-status.tsx", { react: hooks });
  const trees = [
    StatisticsDialog({
      title: "All posts", triggerLabel: "Show posts", trigger: "Posts",
      path: "/api/statistics/posts", isValid: Array.isArray, children: () => null,
    }),
    ProcessingStatus({ initial: { status: null, error: null } }),
  ];
  function findClose(node) {
    if (Array.isArray(node)) return node.map(findClose).find(Boolean);
    if (!React.isValidElement(node)) return null;
    if (node.type === "button" && node.props["aria-label"]?.startsWith("Close")) return node;
    return findClose(node.props.children);
  }
  for (const tree of trees) {
    const button = findClose(tree);
    assert.ok(button);
    assert.equal(button.props.type, "button");
    assert.equal(button.props.title, button.props["aria-label"]);
    assert.match(button.props.className, /size-8/);
    assert.match(button.props.className, /focus-visible:outline-foreground/);
    assert.equal(button.props.children.props["aria-hidden"], "true");
    const markup = renderToStaticMarkup(button);
    assert.match(markup, /<svg/);
    assert.doesNotMatch(markup, />Close/);
    button.props.onClick();
  }
  assert.equal(closed, 2);
});

test("closing statistics dialogs aborts requests and removes loaded children", () => {
  const controller = new AbortController();
  const states = [[{ atUri: "post" }], false];
  let stateIndex = 0;
  let refIndex = 0;
  const hooks = {
    ...React,
    useEffect() {},
    useId() { return "details-title"; },
    useRef() { return { current: refIndex++ === 0 ? null : controller }; },
    useState() {
      const index = stateIndex++;
      return [states[index], (value) => { states[index] = value; }];
    },
  };
  const { StatisticsDialog } = loadModule("components\\statistics-dialog.tsx", { react: hooks });
  const props = {
    title: "All posts", triggerLabel: "Show posts", trigger: "Posts",
    path: "/api/statistics/posts", isValid: Array.isArray,
    children: () => React.createElement("p", null, "Loaded posts"),
  };
  let tree = StatisticsDialog(props);
  assert.match(renderToStaticMarkup(tree), /Loaded posts/);
  tree.props.children[1].props.onClose();
  assert.ok(controller.signal.aborted);
  assert.equal(states[0], null);
  stateIndex = refIndex = 0;
  tree = StatisticsDialog(props);
  assert.doesNotMatch(renderToStaticMarkup(tree), /Loaded posts/);
});

test("expanded monthly chart is a labelled, keyboard-focusable scroll region", () => {
  const { MonthlyRepliesChart } = loadModule("components\\monthly-replies-chart.tsx");
  const months = [{ month: "2026-01", replyCount: 3 }, { month: "2026-02", replyCount: 4 }];
  const expanded = renderToStaticMarkup(
    React.createElement(MonthlyRepliesChart, { months, expanded: true }),
  );
  assert.match(expanded, /role="region"/);
  assert.match(expanded, /aria-label="All-time monthly replies chart\. Scroll horizontally to see more months\."/);
  assert.match(expanded, /tabindex="0"/);
  assert.match(expanded, /focus-visible:outline-foreground/);

  const compact = renderToStaticMarkup(React.createElement(MonthlyRepliesChart, { months }));
  assert.doesNotMatch(compact, /role="region"|tabindex="0"/);
});

test("author search retains its outward high-contrast focus outline outside the list scroller and announces results", () => {
  const states = [];
  let stateIndex = 0;
  let idIndex = 0;
  const hooks = {
    ...React,
    useEffect() {},
    useId() { return `test-id-${idIndex++}`; },
    useRef(initial) { return { current: initial }; },
    useState(initial) {
      const index = stateIndex++;
      if (!(index in states)) states[index] = initial;
      return [states[index], (value) => {
        states[index] = typeof value === "function" ? value(states[index]) : value;
      }];
    },
  };
  const authors = [
    { did: "did:plc:alice", handle: "alice.bsky.social", accountStatus: null, replyCount: 12 },
    { did: "did:plc:alicia", handle: "alicia.bsky.social", accountStatus: null, replyCount: 8 },
    ...Array.from({ length: 25 }, (_, index) => ({
      did: `did:plc:other-${index}`,
      handle: `other-${index}.bsky.social`,
      accountStatus: null,
      replyCount: 1,
    })),
  ];
  const { ReplyAuthorsDialog } = loadModule("components\\reply-authors-dialog.tsx", {
    react: hooks,
    "@/components/reply-authors-table": { ReplyAuthorsTable: () => null },
    "@/components/statistics-dialog": { StatisticsDialog: () => null },
    "@/components/ui/button": { Button: () => null },
  });
  const dialog = ReplyAuthorsDialog({ authors });
  assert.equal(dialog.props.scrollContent, false);
  const search = dialog.props.children(authors);

  function renderSearch() {
    stateIndex = 0;
    idIndex = 0;
    return search.type(search.props);
  }

  function findElement(node, type) {
    if (Array.isArray(node)) {
      for (const child of node) {
        const found = findElement(child, type);
        if (found) return found;
      }
      return null;
    }
    if (!React.isValidElement(node)) return null;
    if (node.type === type) return node;
    return findElement(node.props.children, type);
  }

  let tree = renderSearch();
  let markup = renderToStaticMarkup(tree);
  assert.match(markup, /border-foreground\/50/);
  assert.match(markup, /focus-visible:outline-foreground/);
  assert.match(markup, /role="status" aria-live="polite" aria-atomic="true"/);

  const input = findElement(tree, "input");
  assert.match(findElement(tree, "form").props.className, /shrink-0/);
  assert.ok(input.props.className.split(" ").includes("focus-visible:outline-2"));
  assert.ok(input.props.className.split(" ").includes("focus-visible:outline-offset-2"));
  assert.ok(!input.props.className.split(" ").includes("focus-visible:-outline-offset-2"));
  input.props.onChange({ target: { value: "@" } });
  tree = renderSearch();
  assert.equal(React.Children.count(findElement(tree, "datalist").props.children), 0);

  findElement(tree, "input").props.onChange({ target: { value: "@ali" } });
  tree = renderSearch();
  assert.equal(React.Children.count(findElement(tree, "datalist").props.children), 2);

  findElement(tree, "input").props.onChange({ target: { value: "other" } });
  tree = renderSearch();
  assert.equal(React.Children.count(findElement(tree, "datalist").props.children), 20);

  findElement(tree, "input").props.onChange({ target: { value: "alice.bsky.social" } });
  tree = renderSearch();
  findElement(tree, "form").props.onSubmit({ preventDefault() {} });
  tree = renderSearch();
  markup = renderToStaticMarkup(tree);
  assert.match(markup, /Found @alice\.bsky\.social in this list\./);

  findElement(tree, "input").props.onChange({ target: { value: "ali" } });
  tree = renderSearch();
  findElement(tree, "form").props.onSubmit({ preventDefault() {} });
  markup = renderToStaticMarkup(renderSearch());
  assert.match(markup, /Multiple matching handles; refine your search\./);
});

test("detail triggers stay below interactive content instead of covering its scroll area", () => {
  const hooks = {
    ...React,
    useEffect() {},
    useId() { return "details-title"; },
    useRef() { return { current: null }; },
    useState(initial) { return [initial, () => {}]; },
  };
  const { StatisticsDialog } = loadModule("components\\statistics-dialog.tsx", { react: hooks });
  const trigger = React.createElement("div", null, "Scrollable table");
  const tree = StatisticsDialog({
    title: "All authors", triggerLabel: "Show all authors", trigger,
    overlayTrigger: true, path: "/api/statistics/authors",
    isValid: Array.isArray, children: () => null,
  });
  const wrapper = tree.props.children[0];
  assert.equal(wrapper.props.children[0], trigger);
  assert.match(wrapper.props.className, /relative pb-7/);
  const button = wrapper.props.children[1];
  assert.equal(button.type, "button");
  assert.match(button.props.className, /absolute bottom-0 right-0/);
  assert.doesNotMatch(button.props.className, /inset-0|w-full|h-full/);
  assert.doesNotMatch(button.props.children.props.className, /absolute/);
  assert.match(renderToStaticMarkup(button), /View details/);
});

test("author tables expose keyboard-accessible scrolling with sticky headers only in the expanded list", () => {
  const { ReplyAuthorsTable } = loadModule("components\\reply-authors-table.tsx", {
    "@/components/profile-hover-card": { ProfileHoverCard: () => null },
  });
  const props = {
    authors: [{ did: "did:plc:alice", handle: "alice.bsky.social", replyCount: 12 }],
    label: "All reply authors to the right Jerry",
  };
  const expanded = renderToStaticMarkup(React.createElement(ReplyAuthorsTable, { ...props, scrollable: true }));
  assert.match(expanded, /min-h-0 flex-1 overflow-auto/);
  assert.match(expanded, /role="region"/);
  assert.match(expanded, /tabindex="0"/);
  assert.equal((expanded.match(/sticky top-0 z-10 bg-background/g) ?? []).length, 3);
  assert.equal((expanded.match(/scope="col"/g) ?? []).length, 3);
  const compact = renderToStaticMarkup(React.createElement(ReplyAuthorsTable, props));
  assert.doesNotMatch(compact, /sticky|min-h-0 flex-1 overflow-auto/);
  assert.match(compact, /overflow-x-auto/);
  assert.match(compact, /role="region"/);
  assert.match(compact, /aria-label="All reply authors to the right Jerry"/);
  assert.match(compact, /tabindex="0"/);
  assert.match(compact, /focus-visible:outline-foreground/);
});

test("home author handles wrap within the remaining width while detail handles stay on one line", () => {
  const { ReplyAuthorsTable } = loadModule("components\\reply-authors-table.tsx", {
    "@/components/profile-hover-card": {
      ProfileHoverCard: ({ author, children }) => React.createElement("a", { href: "#" }, children ?? `@${author.handle}`),
    },
  });
  const props = {
    authors: [{
      did: "did:plc:alice",
      handle: `${"a".repeat(63)}.bsky.social`,
      replyCount: 1234567,
    }],
    label: "Reply authors",
  };
  const compact = renderToStaticMarkup(React.createElement(ReplyAuthorsTable, props));
  assert.match(compact, /class="[^"]*\btable-fixed\b/);
  assert.match(compact, /class="[^"]*\bw-12\b[^"]*" scope="col"/);
  assert.match(compact, /class="[^"]*\bw-20\b[^"]*" scope="col"/);
  assert.match(compact, /class="[^"]*whitespace-normal \[overflow-wrap:anywhere\]" scope="row"/);
  assert.match(compact, /1,234,567/);
  assert.match(compact, /@a{63}\.<wbr\/>bsky\.<wbr\/>social/);

  const expanded = renderToStaticMarkup(React.createElement(ReplyAuthorsTable, { ...props, scrollable: true }));
  assert.doesNotMatch(expanded, /table-fixed|overflow-wrap:anywhere|<wbr/);
  assert.match(expanded, /class="[^"]*whitespace-nowrap" scope="row"/);
});
