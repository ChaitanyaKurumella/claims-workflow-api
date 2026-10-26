const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const ts = require("typescript");
const { ApiError } = require("../.test-build/api-client.js");
const source = fs
  .readFileSync("src/app.component.ts", "utf8")
  .replace(/^import .*?;\n/gms, "")
  .replace(/@Component\(\{[\s\S]*?\}\)\n/, "")
  .replace("export class AppComponent", "class AppComponent");
const context = {
  ApiError,
  ApiClient: class {},
  crypto: globalThis.crypto,
  label: (x) => x,
  canDecide: () => false,
};
vm.runInNewContext(
  ts.transpile(source + "\nglobalThis.App = AppComponent;", {
    target: ts.ScriptTarget.ES2022,
  }),
  context,
);
function stale() {
  const app = new context.App();
  Object.assign(app, {
    actor: "reviewer",
    role: "Reviewer",
    claims: [{ id: "old" }],
    selected: { id: "old" },
    events: [{ id: 1 }],
    note: "old rationale",
    ops: {},
    nextCursor: "old",
  });
  return app;
}
test("expired session clears stale claim, history, register and operations", async () => {
  const app = stale();
  app.transport = {
    request: async () => {
      throw new ApiError(401, "Choose a role");
    },
  };
  await app.refreshAll();
  for (const key of ["actor", "role", "note"]) assert.equal(app[key], "");
  for (const key of ["claims", "events"]) assert.equal(app[key].length, 0);
  for (const key of ["selected", "ops", "nextCursor"])
    assert.equal(app[key], null);
  assert.equal(app.error, true);
});
test("reset claim is discarded while refreshed register remains available", async () => {
  const app = stale();
  app.transport = {
    request: async (path) => {
      if (path === "/api/claims/page")
        return { items: [{ id: "new" }], nextCursor: null };
      throw new ApiError(404, "Missing");
    },
  };
  await app.refresh();
  assert.equal(app.claims[0].id, "new");
  assert.equal(app.selected, null);
  assert.equal(app.events.length, 0);
  assert.equal(app.note, "");
  assert.match(app.message, /no longer available/);
});
test("history failure never pairs new detail with old audit records", async () => {
  const app = stale();
  app.transport = {
    request: async (path) => {
      if (path.endsWith("/history")) throw new ApiError(503, "Unavailable");
      return { id: "new" };
    },
  };
  await app.select({ id: "new" });
  assert.equal(app.selected, null);
  assert.equal(app.events.length, 0);
  assert.equal(app.error, true);
});
