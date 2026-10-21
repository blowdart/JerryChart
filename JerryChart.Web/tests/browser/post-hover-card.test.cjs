const assert = require("node:assert/strict");
const http = require("node:http");
const path = require("node:path");
const { createRequire } = require("node:module");
const { once } = require("node:events");
const test = require("node:test");
const { build } = require("esbuild");
const { chromium } = require("playwright");

test("hydrated processing status preserves reply preview navigation and keyboard access", { timeout: 60000 }, async (t) => {
  const root = path.resolve(__dirname, "..", "..");
  const fixture = `
    import React from "react";
    import { ProcessingStatus } from "./src/components/processing-status";
    function Fixture() {
      return <main>
        <button>Before preview</button>
        <ProcessingStatus initial={{status: null, error: null}}
          lastDetectedAt="2026-10-20T12:00:00Z"
          lastDetectedPostAtUri="at://did:plc:example/app.bsky.feed.post/reply" />
        <a href="#after">After processing status</a>
      </main>;
    }
  `;
  const options = {
    bundle: true, write: false, jsx: "automatic",
    tsconfig: path.join(root, "tsconfig.json"),
    define: { "process.env.NODE_ENV": '"production"' },
    plugins: [{
      name: "isolated-router",
      setup(build) {
        build.onResolve({ filter: /^next\/navigation$/ }, () => ({ path: "router", namespace: "fixture" }));
        build.onLoad({ filter: /.*/, namespace: "fixture" }, () => ({
          contents: "export const useRouter = () => ({ refresh() {} });",
        }));
      },
    }],
  };
  const serverBundle = await build({
    ...options, platform: "node", format: "cjs", packages: "external",
    stdin: {
      contents: `${fixture}
        import { renderToString } from "react-dom/server";
        export const markup = renderToString(<Fixture />);
      `,
      resolveDir: root, loader: "tsx",
    },
  });
  const serverModule = { exports: {} };
  new Function("require", "module", "exports", serverBundle.outputFiles[0].text)(
    createRequire(path.join(root, "package.json")), serverModule, serverModule.exports,
  );
  const bundle = await build({
    ...options,
    stdin: {
      contents: `${fixture}
        import { hydrateRoot } from "react-dom/client";
        hydrateRoot(document.getElementById("root"), <Fixture />);
      `,
      resolveDir: root, loader: "tsx",
    },
  });
  const server = http.createServer((request, response) => {
    if (request.url === "/app.js") {
      response.setHeader("Content-Type", "text/javascript");
      response.end(bundle.outputFiles[0].contents);
    } else {
      response.setHeader("Content-Type", "text/html; charset=utf-8");
      response.end(`<!doctype html><html lang="en"><title>Preview regression</title>
        <style>[role="region"] { max-height: 150px; overflow: auto; }</style>
        <div id="root">${serverModule.exports.markup}</div><script src="/app.js"></script></html>`);
    }
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  t.after(() => new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve())));
  const browser = await chromium.launch({
    channel: process.env.BROWSER_CHANNEL ?? (process.platform === "win32" ? "msedge" : undefined),
  });
  let embedGate = null;
  t.after(async () => {
    embedGate?.response.resolve();
    await browser.close();
  });
  const page = await browser.newPage();
  let embedRequests = 0;
  await page.route("https://embed.bsky.app/**", async route => {
    embedRequests++;
    if (embedGate) {
      embedGate.requested.resolve();
      await embedGate.response.promise;
    }
    await route.fulfill({
      contentType: "text/html",
      body: `<!doctype html><title>Embed fixture</title>
        <p>Rendered embed fixture</p><a href="https://bsky.app/">Bluesky</a>
        <script>parent.postMessage({id:new URLSearchParams(location.search).get("id"),height:900},"*")</script>`,
    });
  });
  const navigations = [];
  await page.route("https://bsky.app/**", route => {
    navigations.push(route.request().url());
    return route.fulfill({ contentType: "text/html", body: "Post navigation succeeded" });
  });
  const errors = [];
  page.on("pageerror", error => errors.push(error.message));
  const url = `http://127.0.0.1:${server.address().port}`;
  await page.goto(url);
  const timestamp = page.getByRole("link", { name: /view the matching reply on Bluesky/ });
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
  for (const timing of ["loading", "rendered"]) {
    await t.test(`Tab after hover dismissal enters the focus-opened card while embed is ${timing}`, async (t) => {
      t.after(() => {
        embedGate?.response.resolve();
        embedGate = null;
      });
      await page.goto(url);
      const before = page.getByRole("button", { name: "Before preview" });
      await before.focus();
      await timestamp.hover();
      await close.waitFor();
      await page.waitForFunction(() => document.querySelector("iframe")?.height === "900");
      await before.hover();
      await close.waitFor({ state: "hidden" });
      if (timing === "loading") {
        embedGate = { requested: Promise.withResolvers(), response: Promise.withResolvers() };
      }
      await page.keyboard.press("Tab");
      assert.ok(await focused(timestamp), "Tab must focus the last-detected timestamp.");
      if (embedGate) {
        await embedGate.requested.promise;
        assert.equal(await page.getByRole("status").textContent(), "Loading Bluesky preview...");
      } else {
        await page.waitForFunction(() => document.querySelector("iframe")?.height === "900");
      }
      await page.keyboard.press("Tab");
      assert.ok(await focused(close), "Next Tab must enter the card, not the underlying Refresh button.");
      const requestsBeforeReverseTab = embedRequests;
      await page.keyboard.press("Shift+Tab");
      assert.ok(await focused(timestamp));
      await page.keyboard.press("Tab");
      assert.ok(await focused(close));
      assert.equal(embedRequests, requestsBeforeReverseTab, "Returning to the trigger must not reload the embed.");
      if (embedGate) {
        embedGate.response.resolve();
        embedGate = null;
      }
      await page.waitForFunction(() => document.querySelector("iframe")?.height === "900");
      assert.ok(await focused(close), "Embed loading must not move keyboard focus.");
      await page.keyboard.press("Tab");
      assert.ok(await focused(region));
      await page.keyboard.press("Tab");
      assert.ok(await focused(frameLink));
      await page.keyboard.press("Tab");
      assert.ok(await focused(page.getByRole("link", { name: "View the reply on Bluesky", exact: true })));
      await page.keyboard.press("Shift+Tab");
      assert.ok(await focused(frameLink));
      await page.keyboard.press("Shift+Tab");
      assert.ok(await focused(region));
      await page.keyboard.press("Shift+Tab");
      assert.ok(await focused(close));
      await page.keyboard.press("Shift+Tab");
      assert.ok(await focused(timestamp));
      await page.keyboard.press("Shift+Tab");
      assert.ok(await focused(before), "Reverse tabbing must leave the card at the preceding page control.");
      await close.waitFor({ state: "hidden" });
      await page.keyboard.press("Tab");
      await page.keyboard.press("Tab");
      assert.ok(await focused(close));
      await page.keyboard.press("Escape");
      await close.waitFor({ state: "hidden" });
      assert.ok(await focused(timestamp));
      await page.keyboard.press("Tab");
      assert.ok(await focused(refresh));
    });
  }
  assert.deepEqual(errors, []);
});
