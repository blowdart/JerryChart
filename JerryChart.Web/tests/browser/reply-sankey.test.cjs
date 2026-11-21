const assert = require("node:assert/strict");
const fs = require("node:fs");
const http = require("node:http");
const path = require("node:path");
const { createRequire } = require("node:module");
const { once } = require("node:events");
const test = require("node:test");
const { build } = require("esbuild");
const postcss = require("postcss");
const tailwind = require("@tailwindcss/postcss");
const { chromium } = require("playwright");

test("reply Sankey loads on demand, preserves all months and offers accessible counts", { timeout: 60000 }, async (t) => {
  const root = path.resolve(__dirname, "..", "..");
  const fixture = `
    import React from "react";
    import { ReplyFlowDialog } from "./src/components/reply-flow-dialog";
    function Fixture() {
      return <main>
        <button>Before summary</button>
        <ReplyFlowDialog>
          <table aria-label="Jerry no reply summary"><tbody>
            <tr><th scope="row">All replies</th><td>152</td></tr>
          </tbody></table>
        </ReplyFlowDialog>
        <button>After summary</button>
      </main>;
    }
  `;
  const options = {
    bundle: true, write: false, jsx: "automatic",
    tsconfig: path.join(root, "tsconfig.json"),
    define: { "process.env.NODE_ENV": '"production"' },
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
  const cssPath = path.join(root, "src", "app", "globals.css");
  const styles = await postcss([tailwind({ base: root })]).process(fs.readFileSync(cssPath, "utf8"), { from: cssPath });
  const months = Array.from({ length: 49 }, (_, index) => ({
    month: `${2023 + Math.floor(index / 12)}-${String(index % 12 + 1).padStart(2, "0")}-01`,
    replyCount: 3,
  }));
  let flow = { summary: { totalReplies: 152, rightJerryReplies: 147, wrongJerryReplies: 5 }, months };
  let requests = 0;
  let gate = Promise.withResolvers();
  const server = http.createServer(async (request, response) => {
    if (request.url === "/app.js") {
      response.setHeader("Content-Type", "text/javascript");
      response.end(bundle.outputFiles[0].contents);
    } else if (request.url === "/styles.css") {
      response.setHeader("Content-Type", "text/css");
      response.end(styles.css);
    } else if (request.url === "/api/statistics/reply-flow") {
      requests++;
      if (gate) await gate.promise;
      response.setHeader("Content-Type", "application/json");
      response.end(JSON.stringify(flow));
    } else {
      response.setHeader("Content-Type", "text/html; charset=utf-8");
      response.end(`<!doctype html><html lang="en"><title>Sankey regression</title>
        <link rel="stylesheet" href="/styles.css">
        <div id="root">${serverModule.exports.markup}</div><script src="/app.js"></script></html>`);
    }
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  t.after(() => new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve())));
  const browser = await chromium.launch({
    channel: process.env.BROWSER_CHANNEL ?? (process.platform === "win32" ? "msedge" : undefined),
  });
  t.after(async () => {
    gate?.resolve();
    await browser.close();
  });
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  const errors = [];
  page.on("pageerror", error => errors.push(error.message));
  await page.goto(`http://127.0.0.1:${server.address().port}`);
  const trigger = page.getByRole("button", { name: "View More: Show all replies by Jerry, year, and month" });
  const dialog = page.getByRole("dialog");
  const chartNodes = page.locator(".recharts-sankey-nodes > .recharts-layer");
  assert.equal(requests, 0, "The chart must not load data before the dialog opens.");
  assert.equal(await page.getByRole("table", { name: "Jerry no reply summary" }).locator("button").count(), 0);
  assert.equal(await trigger.locator('svg[aria-hidden="true"]').count(), 1);
  await page.getByRole("button", { name: "Before summary" }).focus();
  await page.keyboard.press("Tab");
  assert.ok(await trigger.evaluate(element => element === document.activeElement));
  await page.keyboard.press("Enter");
  await dialog.waitFor();
  await page.getByRole("status", { name: "" }).filter({ hasText: "Loading statistics..." }).waitFor();
  gate.resolve();
  gate = null;
  try {
    await chartNodes.last().waitFor();
  } catch (error) {
    console.error("Sankey browser diagnostics:", {
      errors, text: await dialog.textContent(), nodes: await chartNodes.count(),
      containers: await page.locator(".recharts-responsive-container").evaluateAll(elements =>
        elements.map(element => ({ width: element.clientWidth, height: element.clientHeight }))),
    });
    throw error;
  }
  assert.equal(await chartNodes.count(), 3 + 5 + 49, "Every recorded month and year must be drawn.");
  const x = async (name) => Number(await chartNodes.filter({ hasText: name }).first().locator("rect").getAttribute("x"));
  assert.equal(await x("The Right Jerry"), await x("The Wrong Jerry"), "Both Jerrys belong in the second column.");
  assert.ok(await x("All replies") < await x("The Right Jerry"));
  assert.ok(await x("The Right Jerry") < await x("2024"));
  assert.ok(await x("2024") < await x("January 2024"));
  assert.ok(await page.locator(".recharts-sankey-link").evaluateAll(elements =>
    elements.every(element => !element.innerHTML.includes("NaN"))));
  await page.getByText("View exact reply counts as a table", { exact: true }).click();
  const counts = page.getByRole("table", { name: "All-time reply flow counts, with right-Jerry years and months in UTC." });
  await counts.waitFor();
  assert.equal(await counts.getByRole("row").count(), 1 + 3 + 5 + 49);
  assert.ok((await counts.textContent()).includes("January 2027"));
  await page.getByRole("button", { name: "Close details" }).click();
  await dialog.waitFor({ state: "hidden" });
  await chartNodes.first().waitFor({ state: "detached" });
  assert.equal(await chartNodes.count(), 0, "Closing must remove the loaded chart.");
  assert.ok(await trigger.evaluate(element => element === document.activeElement));

  await page.setViewportSize({ width: 390, height: 844 });
  await trigger.click();
  await chartNodes.last().waitFor();
  const region = page.getByRole("region", { name: /All-time reply Sankey diagram/ });
  await region.focus();
  await page.keyboard.press("ArrowRight");
  await page.waitForFunction(() => document.querySelector('[aria-label^="All-time reply Sankey"]')?.scrollLeft > 0);
  await page.keyboard.press("Escape");
  await dialog.waitFor({ state: "hidden" });

  for (const summary of [
    { totalReplies: 4, rightJerryReplies: 0, wrongJerryReplies: 4 },
    { totalReplies: 0, rightJerryReplies: 0, wrongJerryReplies: 0 },
  ]) {
    flow = { summary, months: [] };
    await trigger.click();
    await page.getByRole("status").waitFor({ state: "hidden" });
    if (summary.totalReplies) {
      await chartNodes.last().waitFor();
      assert.equal(await chartNodes.count(), 2);
      assert.ok((await dialog.textContent()).includes("No matching replies to The Right Jerry recorded yet."));
    } else {
      await page.getByText("No matching replies recorded yet.", { exact: true }).waitFor();
      assert.equal(await chartNodes.count(), 0);
    }
    await page.keyboard.press("Escape");
    await dialog.waitFor({ state: "hidden" });
  }
  await page.setViewportSize({ width: 1280, height: 900 });
  flow = {
    summary: { totalReplies: 17, rightJerryReplies: 12, wrongJerryReplies: 5 },
    months: [
      { month: "2023-12-01", replyCount: 2 },
      { month: "2024-01-01", replyCount: 3 },
      { month: "2024-02-01", replyCount: 6 },
      { month: "2030-01-01", replyCount: 1 },
    ],
  };
  await trigger.click();
  await chartNodes.last().waitFor();
  if (process.env.SANKEY_SCREENSHOT) await page.screenshot({ path: process.env.SANKEY_SCREENSHOT });
  assert.deepEqual(errors, []);
});
