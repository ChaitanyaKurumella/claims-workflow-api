const { test } = require("node:test");
const assert = require("node:assert/strict");
const { canDecide, isFinal } = require("../.test-build/workflow-view.js");
const { ApiClient, ApiError } = require("../.test-build/api-client.js");
const c = { status: "PendingSupervisor", assignee: "alice" };
test("second approval requires a distinct authorized supervisor", () => {
  assert.equal(canDecide(c, "alice", "Supervisor"), false);
  assert.equal(canDecide(c, "bob", "Reviewer"), false);
  assert.equal(canDecide(c, "bob", ""), false);
  assert.equal(canDecide(c, "", "Administrator"), false);
  assert.equal(canDecide(c, "bob", "Supervisor"), true);
  assert.equal(canDecide(c, "bob", "Administrator"), true);
});
test("only assignee can decide an in-review claim; final decisions hide controls", () => {
  assert.equal(
    canDecide({ ...c, status: "InReview" }, "bob", "Supervisor"),
    false,
  );
  assert.equal(
    canDecide({ ...c, status: "InReview" }, "alice", "Reviewer"),
    true,
  );
  for (const status of ["Approved", "Rejected"]) {
    assert.equal(canDecide({ ...c, status }, "bob", "Supervisor"), false);
    assert.equal(isFinal({ ...c, status }), true);
  }
});
test("conflict rationale survives transport and caller can refresh the stale case", async () => {
  const api = new ApiClient(
    async () =>
      new Response(JSON.stringify({ detail: "This claim changed. Refresh." }), {
        status: 409,
      }),
  );
  await assert.rejects(
    api.request("/api/claims/id/approve", "", { version: 2, note: "checked" }),
    (e) =>
      e instanceof ApiError &&
      e.status === 409 &&
      e.message.includes("Refresh"),
  );
});
test("retry sends unchanged idempotency key and payload; API keys stay in headers", async () => {
  const requests = [];
  const api = new ApiClient(async (path, options) => {
    requests.push({ path, options });
    return new Response('{"id":"claim"}');
  });
  const body = { reference: "TEST", amount: 25 };
  for (let i = 0; i < 2; i++)
    await api.request("/api/claims", "test-secret", body, {
      "Idempotency-Key": "request-123",
    });
  assert.deepEqual(requests[0], requests[1]);
  assert.equal(requests[0].options.headers["X-Api-Key"], "test-secret");
  assert.equal(requests[0].path.includes("test-secret"), false);
});
test("401 with a non-JSON response is an actionable typed authentication error", async () => {
  const api = new ApiClient(async () => new Response("", { status: 401 }));
  await assert.rejects(
    api.request("/api/session", ""),
    (e) => e.status === 401 && e.message.includes("configured API key"),
  );
});

test("default transport calls fetch with the browser global receiver", async () => {
  const original = globalThis.fetch;
  globalThis.fetch = function () {
    assert.equal(this, globalThis);
    return Promise.resolve(new Response('{"id":"person"}'));
  };
  try {
    assert.deepEqual(await new ApiClient().request("/api/session", ""), {
      id: "person",
    });
  } finally {
    globalThis.fetch = original;
  }
});
