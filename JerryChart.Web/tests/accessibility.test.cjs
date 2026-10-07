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
  input.props.onChange({ target: { value: "alice.bsky.social" } });
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

test("expanded author list owns scrolling and keeps every column header sticky without changing the home table", () => {
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
  assert.doesNotMatch(compact, /sticky|role="region"|tabindex/);
});
