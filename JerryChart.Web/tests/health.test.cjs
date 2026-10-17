const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const { transformSync } = require("esbuild");

const file = path.join(__dirname, "..", "src", "app", "health", "route.ts");
const route = { exports: {} };
new Function("module", "exports", transformSync(fs.readFileSync(file, "utf8"), {
  loader: "ts",
  format: "cjs",
  target: "es2022",
}).code)(route, route.exports);

test("GET reports healthy without caching or contacting the statistics API", async () => {
  const originalFetch = globalThis.fetch;
  globalThis.fetch = () => assert.fail("The health route must not call other services.");
  try {
    const response = await route.exports.GET();
    assert.equal(response.status, 200);
    assert.equal(response.headers.get("Cache-Control"), "no-store");
    assert.deepEqual(await response.json(), { status: "Healthy" });
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("HEAD returns an empty successful response", async () => {
  const response = route.exports.HEAD();
  assert.equal(response.status, 200);
  assert.equal(response.headers.get("Cache-Control"), "no-store");
  assert.equal(await response.text(), "");
});
