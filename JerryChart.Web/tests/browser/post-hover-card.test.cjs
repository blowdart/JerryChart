const assert = require("node:assert/strict");
const http = require("node:http");
const path = require("node:path");
const { once } = require("node:events");
const test = require("node:test");
const { build } = require("esbuild");
const { chromium } = require("playwright");

test("real popover preserves link navigation and keyboard access through its portal", { timeout: 60000 }, async (t) => {
  const root = path.resolve(__dirname, "..", "..");
  const bundle = await build({
    stdin: {
      contents: `
        import React from "react";
        import { createRoot } from "react-dom/client";
        import { PostHoverCard } from "./src/components/post-hover-card";
        createRoot(document.getElementById("root")).render(<>
          <button>Before preview</button>
          <PostHoverCard atUri="at://did:plc:example/app.bsky.feed.post/reply">
            <time dateTime="2026-10-20T12:00:00Z">Last reply timestamp</time>
          </PostHoverCard>
          <button>Refresh</button>
        </>);
      `,
      resolveDir: root, loader: "tsx",
    },
    bundle: true, write: false, jsx: "automatic",
    tsconfig: path.join(root, "tsconfig.json"),
    define: { "process.env.NODE_ENV": '"production"' },
  });
  const server = http.createServer((request, response) => {
    if (request.url === "/app.js") {
      response.setHeader("Content-Type", "text/javascript");
      response.end(bundle.outputFiles[0].contents);
    } else {
      response.setHeader("Content-Type", "text/html");
      response.end(`<!doctype html><html lang="en"><title>Preview regression</title>
        <style>[role="region"] { max-height: 150px; overflow: auto; }</style>
        <div id="root"></div><script src="/app.js"></script></html>`);
    }
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  t.after(() => new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve())));
  const browser = await chromium.launch({
    channel: process.env.BROWSER_CHANNEL ?? (process.platform === "win32" ? "msedge" : undefined),
  });
  t.after(() => browser.close());
  const page = await browser.newPage();
  await page.route("https://embed.bsky.app/**", route => route.fulfill({
    contentType: "text/html",
    body: `<!doctype html><title>Embed fixture</title>
      <p>Rendered embed fixture</p><a href="https://bsky.app/">Bluesky</a>
      <script>parent.postMessage({id:new URLSearchParams(location.search).get("id"),height:900},"*")</script>`,
  }));
  const navigations = [];
  await page.route("https://bsky.app/**", route => {
    navigations.push(route.request().url());
    return route.fulfill({ contentType: "text/html", body: "Post navigation succeeded" });
  });
  const errors = [];
  page.on("pageerror", error => errors.push(error.message));
  const url = `http://127.0.0.1:${server.address().port}`;
  await page.goto(url);
  const timestamp = page.getByRole("link", { name: "Last reply timestamp", exact: true });
  const close = page.getByRole("button", { name: "Close reply preview" });
  const region = page.getByRole("region", { name: "Bluesky post. Scroll to see the full preview." });
  const refresh = page.getByRole("button", { name: "Refresh", exact: true });
  const focused = locator => locator.evaluate(element => element === document.activeElement);
  assert.equal(await page.locator("iframe").count(), 0);
  await page.getByRole("button", { name: "Before preview" }).focus();
  await page.keyboard.press("Tab");
  assert.ok(await focused(timestamp));
  await close.waitFor();
  assert.ok(await focused(timestamp), "Opening must not steal focus from the timestamp.");
  await page.keyboard.press("Tab");
  assert.ok(await focused(close), "Tab from the timestamp must enter the portal, not skip to Refresh.");
  await page.keyboard.press("Shift+Tab");
  assert.ok(await focused(timestamp));
  await page.keyboard.press("Tab");
  assert.ok(await focused(close));
  await page.keyboard.press("Tab");
  assert.ok(await focused(region), "Keyboard users must reach the scroll region.");
  await page.waitForFunction(() => document.querySelector("iframe")?.height === "900");
  await page.keyboard.press("ArrowDown");
  await page.waitForFunction(() => document.querySelector('[role="region"]').scrollTop > 0);
  await page.keyboard.press("Tab");
  const frameLink = page.frameLocator("iframe").getByRole("link", { name: "Bluesky", exact: true });
  assert.ok(await focused(frameLink), "The embedded document must be keyboard reachable.");
  await page.keyboard.press("Tab");
  assert.ok(await focused(page.getByRole("link", { name: "View the reply on Bluesky", exact: true })));
  await page.keyboard.press("Tab");
  assert.ok(await focused(refresh), "Tab after the popup must continue at the next page control.");
  await close.waitFor({ state: "hidden" });
  await timestamp.focus();
  await close.waitFor();
  await page.keyboard.press("Tab");
  await page.keyboard.press("Enter");
  await close.waitFor({ state: "hidden" });
  assert.ok(await focused(timestamp), "Closing must restore focus without reopening.");
  await page.keyboard.press("Tab");
  assert.ok(await focused(refresh));
  await timestamp.focus();
  await close.waitFor();
  await page.keyboard.press("Escape");
  await close.waitFor({ state: "hidden" });
  assert.ok(await focused(timestamp));
  await page.keyboard.press("Tab");
  await timestamp.hover();
  await close.waitFor();
  assert.ok(await focused(refresh), "Hover must not steal keyboard focus.");
  await timestamp.click();
  await page.waitForURL("https://bsky.app/profile/did:plc:example/post/reply");
  assert.equal(navigations.length, 1);
  await page.goto(url);
  await timestamp.focus();
  await close.waitFor();
  await page.keyboard.press("Enter");
  await page.waitForURL("https://bsky.app/profile/did:plc:example/post/reply");
  assert.equal(navigations.length, 2, "Enter must navigate the original timestamp link.");
  assert.deepEqual(errors, []);
});
