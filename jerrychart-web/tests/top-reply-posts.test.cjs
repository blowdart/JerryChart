const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { createRequire } = require("node:module");
const test = require("node:test");
const { transformSync } = require("esbuild");
const React = require("react");
const { renderToStaticMarkup } = require("react-dom/server");

const root = path.resolve(__dirname, "..");

// Exercise the real TS modules without emitting files or adding a browser/test dependency.
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

const load = modules();
const { rightJerryDid, isReplyPostList, blueskyPostUrl } = load("lib\\reply-posts.ts");
const { getBlueskyPosts } = load("lib\\bluesky-posts.ts");
const uri = (key) => `at://${rightJerryDid}/app.bsky.feed.post/${key}`;
const ranking = [
  { atUri: uri("aaa"), replyCount: 12 },
  { atUri: uri("bbb"), replyCount: 9 },
  { atUri: uri("ccc"), replyCount: 9 },
];
const publicPost = (key, text) => ({ uri: uri(key), author: { did: rightJerryDid }, record: { text } });
const response = (body, status = 200) => new Response(JSON.stringify(body), { status });
const settle = () => new Promise((resolve) => setImmediate(resolve));

function replaceFetch(context, fetch) {
  const original = global.fetch;
  global.fetch = fetch;
  context.after(() => { global.fetch = original; });
}

// A small hook lifecycle driver: render the actual JSX with React's server renderer,
// then run effects, rerender, click retry, or unmount without starting the dev server.
function componentHarness() {
  const states = [];
  const effects = [];
  let stateIndex = 0;
  let effectIndex = 0;
  const hooks = {
    ...React,
    useState(initial) {
      const index = stateIndex++;
      if (!(index in states)) states[index] = initial;
      return [states[index], (value) => {
        states[index] = typeof value === "function" ? value(states[index]) : value;
      }];
    },
    useEffect(action, deps) {
      const index = effectIndex++;
      const previous = effects[index];
      if (!previous || deps.some((dep, i) => dep !== previous.deps[i])) {
        effects[index] = { deps, action, cleanup: previous?.cleanup, pending: true };
      }
    },
  };
  const { TopReplyPosts } = modules({ react: hooks })("components\\top-reply-posts.tsx");
  let tree;
  return {
    render(posts = ranking) {
      stateIndex = effectIndex = 0;
      tree = TopReplyPosts({ posts });
      return renderToStaticMarkup(tree);
    },
    commit() {
      for (const effect of effects) {
        if (effect.pending) {
          effect.cleanup?.();
          effect.cleanup = effect.action();
          effect.pending = false;
        }
      }
    },
    retry() {
      function visit(node) {
        if (Array.isArray(node)) return node.some(visit);
        if (!React.isValidElement(node)) return false;
        if (node.type === "button") {
          node.props.onClick();
          return true;
        }
        return visit(node.props.children);
      }
      assert.ok(visit(tree), "The error must expose a retry button.");
    },
    unmount() { for (const effect of effects) effect.cleanup?.(); },
  };
}

test("ranking validation rejects invalid identity, counts, duplicates, ordering and length", () => {
  assert.ok(isReplyPostList([]));
  assert.ok(isReplyPostList(ranking));
  for (const invalid of [
    null, {}, [null], [{ ...ranking[0], atUri: uri("..") }],
    [{ ...ranking[0], atUri: uri("key/extra") }],
    [{ ...ranking[0], atUri: uri("key?query") }],
    [{ ...ranking[0], atUri: uri("a".repeat(513)) }],
    [{ ...ranking[0], atUri: uri("aaa").replace(rightJerryDid, "did:plc:someoneelse") }],
    [{ ...ranking[0], atUri: uri("aaa").replace("app.bsky.feed.post", "app.bsky.feed.like") }],
    ...[0, -1, 1.5, Number.MAX_SAFE_INTEGER + 1, "12"].map((replyCount) => [{ ...ranking[0], replyCount }]),
    [ranking[0], ranking[0]], [ranking[1], ranking[0]], [ranking[2], ranking[1]],
    Array.from({ length: 6 }, (_, i) => ({ atUri: uri(`post${i}`), replyCount: 1 })),
  ]) assert.equal(isReplyPostList(invalid), false, JSON.stringify(invalid));
  assert.equal(blueskyPostUrl(uri("aaa")), `https://bsky.app/profile/${rightJerryDid}/post/aaa`);
  assert.throws(() => blueskyPostUrl("at://wrong/post/key"));
});

test("server ranking fetch uses private API and rejects errors instead of fabricating data", async (context) => {
  const originalBase = process.env.API_BASE_URL;
  process.env.API_BASE_URL = "http://private-api.example";
  context.after(() => {
    if (originalBase === undefined) delete process.env.API_BASE_URL;
    else process.env.API_BASE_URL = originalBase;
  });
  let body = ranking;
  let status = 200;
  replaceFetch(context, async (url, options) => {
    assert.equal(url.href, "http://private-api.example/statistics/right-jerry/top-posts");
    assert.equal(options.cache, "no-store");
    assert.ok(options.signal instanceof AbortSignal);
    return response(body, status);
  });
  const { getTopRightJerryPosts } = load("lib\\api.ts");
  assert.deepEqual(await getTopRightJerryPosts(), ranking);
  body = [];
  assert.deepEqual(await getTopRightJerryPosts(), []);
  body = [ranking[0], ranking[0]];
  await assert.rejects(getTopRightJerryPosts(), /invalid top Jerry posts/);
  status = 503;
  await assert.rejects(getTopRightJerryPosts(), /HTTP 503/);
});

test("one public batch validates each expected URI and preserves empty versus unavailable text", async (context) => {
  let calls = 0;
  replaceFetch(context, async (url, options) => {
    calls++;
    assert.equal(url.origin, "https://public.api.bsky.app");
    assert.equal(url.pathname, "/xrpc/app.bsky.feed.getPosts");
    assert.deepEqual(url.searchParams.getAll("uris"), ranking.map((post) => post.atUri));
    assert.equal(options.credentials, "omit");
    assert.equal(options.cache, "no-store");
    return response({ posts: [publicPost("bbb", ""), publicPost("aaa", "<script>\nHello & Jerry")] });
  });
  const result = await getBlueskyPosts(ranking.map((post) => post.atUri), new AbortController().signal);
  assert.equal(result[uri("aaa")], "<script>\nHello & Jerry");
  assert.equal(result[uri("bbb")], "");
  assert.equal(result[uri("ccc")], undefined);
  assert.deepEqual(await getBlueskyPosts([], new AbortController().signal), {});
  assert.equal(calls, 1);
  await assert.rejects(getBlueskyPosts([uri("aaa"), uri("aaa")], new AbortController().signal));
  await assert.rejects(getBlueskyPosts(Array(6).fill(uri("aaa")), new AbortController().signal));
});

test("public service failures, invalid JSON and forged/duplicate/unexpected posts fail explicitly", async (context) => {
  let nextResponse;
  replaceFetch(context, async () => nextResponse);
  const request = () => getBlueskyPosts([uri("aaa")], new AbortController().signal);
  nextResponse = response({}, 429);
  await assert.rejects(request(), /HTTP 429/);
  nextResponse = new Response("not json");
  await assert.rejects(request());
  for (const body of [
    {}, { posts: null }, { posts: [null] },
    { posts: [publicPost("bbb", "unexpected")] },
    { posts: [publicPost("aaa", "first"), publicPost("aaa", "duplicate")] },
    { posts: [{ ...publicPost("aaa", "wrong"), author: { did: "did:plc:wrong" } }] },
    { posts: [{ uri: uri("aaa"), author: { did: rightJerryDid } }] },
    { posts: [publicPost("aaa", undefined)] },
    { posts: [publicPost("aaa", 123)] },
  ]) {
    nextResponse = response(body);
    await assert.rejects(request(), /invalid/);
  }
});

test("component renders loading, ranked links, escaped multiline text, unavailable and empty text", async (context) => {
  let calls = 0;
  replaceFetch(context, async () => {
    calls++;
    return response({ posts: [publicPost("aaa", "<script>\nHello & Jerry"), publicPost("bbb", "")] });
  });
  const component = componentHarness();
  let html = component.render();
  assert.match(html, /Loading Bluesky posts/);
  assert.match(html, />Rank</);
  assert.match(html, />Post</);
  assert.match(html, />Replies</);
  assert.match(html, />12</);
  assert.match(html, new RegExp(`https://bsky.app/profile/${rightJerryDid}/post/aaa`));
  component.commit();
  await settle();
  html = component.render();
  assert.match(html, /&lt;script&gt;\nHello &amp; Jerry/);
  assert.match(html, /whitespace-pre-wrap/);
  assert.match(html, /Post has no text/);
  assert.match(html, /Post unavailable/);
  assert.doesNotMatch(html, /<script>/);
  assert.match(html, />12</);
  component.render(ranking.map((post) => ({ ...post })));
  component.commit();
  await settle();
  assert.equal(calls, 1, "Equivalent lists must not fetch again on rerender.");
  component.unmount();
});

test("component error is visible, retains counts and retries once on click", async (context) => {
  let calls = 0;
  replaceFetch(context, async () => {
    calls++;
    return calls === 1 ? response({}, 503) : response({ posts: [publicPost("aaa", "Recovered")] });
  });
  const originalError = console.error;
  console.error = () => {};
  context.after(() => { console.error = originalError; });
  const component = componentHarness();
  component.render();
  component.commit();
  await settle();
  let html = component.render();
  assert.match(html, /role="alert"/);
  assert.match(html, /Unable to load post text from Bluesky/);
  assert.match(html, /Post text could not be loaded/);
  assert.doesNotMatch(html, /Post unavailable/);
  assert.match(html, />12</);
  component.retry();
  component.render();
  component.commit();
  assert.match(component.render(), /Loading Bluesky posts/);
  await settle();
  html = component.render();
  assert.match(html, /Recovered/);
  assert.doesNotMatch(html, /role="alert"/);
  assert.equal(calls, 2);
  component.unmount();
});

test("list replacement and unmount cancel requests; stale results cannot overwrite new text", async (context) => {
  const requests = [];
  replaceFetch(context, (url, options) => new Promise((resolve) => {
    requests.push({ url, signal: options.signal, resolve });
  }));
  const component = componentHarness();
  component.render();
  component.commit();
  const replacement = [{ atUri: uri("new"), replyCount: 20 }];
  assert.match(component.render(replacement), /Loading Bluesky posts/);
  component.commit();
  assert.equal(requests[0].signal.aborted, true);
  requests[1].resolve(response({ posts: [publicPost("new", "New list")] }));
  await settle();
  requests[0].resolve(response({ posts: [publicPost("aaa", "Stale list")] }));
  await settle();
  const html = component.render(replacement);
  assert.match(html, /New list/);
  assert.doesNotMatch(html, /Stale list/);
  component.unmount();
  assert.equal(requests[1].signal.aborted, true);
});

test("empty ranking has an honest empty state and performs no public request", async (context) => {
  replaceFetch(context, () => { assert.fail("Empty rankings must not fetch public posts."); });
  const component = componentHarness();
  const html = component.render([]);
  assert.match(html, /No recorded replies to resolved Jerry posts yet/);
  assert.doesNotMatch(html, /Loading Bluesky posts|Post unavailable/);
  component.commit();
  await settle();
  component.unmount();
});
