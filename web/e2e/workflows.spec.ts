import { test, expect, Page } from "@playwright/test";

async function login(page: Page, role = "Reviewer") {
  await page.getByRole("button", { name: role, exact: true }).click();
  await expect(page.locator(".profile b")).toHaveText(
    role.toLowerCase() + "-01",
  );
  await expect(page.getByRole("button", { name: "Refresh" })).toBeEnabled();
}

async function create(page: Page, amount = "15000") {
  const reference = `E2E-${crypto.randomUUID().slice(0, 12)}`;
  await page.getByRole("button", { name: "New claim" }).click();
  await page.getByLabel("Reference", { exact: true }).fill(reference);
  await page.getByLabel("Amount · USD", { exact: true }).fill(amount);
  await page
    .getByLabel("Case summary")
    .fill("Synthetic browser workflow evidence");
  await page.getByRole("button", { name: "Submit claim", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: reference, exact: true }),
  ).toHaveText(reference);
  return reference;
}

// Every test gets a fresh browser context and a server-issued workspace cookie.
test.beforeEach(async ({ page }) => {
  await page.goto("/");
  await login(page);
});

test("high-value claim requires independent supervisor approval and retains audit evidence", async ({
  page,
}) => {
  await create(page);
  const detail = page.locator("case-workspace");
  await page.getByRole("button", { name: "Assign to me" }).click();
  await expect(detail.locator(".badge")).toHaveText("In review");
  await page
    .getByLabel("Decision rationale")
    .fill("Evidence reviewed; independent approval required.");
  await page
    .getByRole("button", { name: "Request supervisor sign-off" })
    .click();
  await expect(detail.locator(".badge")).toHaveText("Supervisor review");
  await expect(
    page.getByRole("button", { name: "Approve claim", exact: true }),
  ).toHaveCount(0);
  await login(page, "Supervisor");
  await page
    .getByLabel("Decision rationale")
    .fill("Independent policy and evidence check complete.");
  await page
    .getByRole("button", { name: "Approve claim", exact: true })
    .click();
  await expect(detail.locator(".badge")).toHaveText("Approved");
  await expect(detail.locator(".timeline li")).toHaveCount(4);
  await expect(detail.locator(".timeline")).toContainText("reviewer-01");
  await expect(detail.locator(".timeline")).toContainText("supervisor-01");
  await expect(detail.locator(".timeline")).toContainText(
    "Independent policy and evidence check complete.",
  );
  await expect(detail.locator(".final-note")).toBeVisible();
  await expect(detail.getByRole("button")).toHaveCount(0);
  await page.reload();
  await expect(page.locator("tbody")).toContainText("E2E-");
});

test("Reviewer restrictions are enforced by both UI and API", async ({
  page,
}) => {
  await expect(
    page.getByRole("button", { name: "Integration monitor" }),
  ).toBeDisabled();
  expect((await page.request.get("/api/operations")).status()).toBe(403);
  expect(
    (
      await page.request.post("/api/operations/demo-failure", {
        data: {},
        headers: { Origin: "http://127.0.0.1:5086" },
      })
    ).status(),
  ).toBe(403);
  await create(page);
  await page.getByRole("button", { name: "Assign to me" }).click();
  await page.getByLabel("Decision rationale").fill("Ready for supervisor.");
  await page
    .getByRole("button", { name: "Request supervisor sign-off" })
    .click();
  await expect(page.locator("case-workspace .badge")).toHaveText(
    "Supervisor review",
  );
  const claims = await (await page.request.get("/api/claims")).json();
  const claim = claims.find((c: { reference: string }) =>
    c.reference.startsWith("E2E-"),
  );
  const response = await page.request.post(`/api/claims/${claim.id}/approve`, {
    headers: { Origin: "http://127.0.0.1:5086" },
    data: { version: claim.version, note: "Attempted role bypass" },
  });
  expect(response.status()).toBe(403);
  expect(
    (await (await page.request.get(`/api/claims/${claim.id}`)).json()).status,
  ).toBe("PendingSupervisor");
});

test("synthetic downstream outage reaches dead letter and recovers with payload intact", async ({
  page,
}) => {
  await login(page, "Supervisor");
  await page.getByRole("button", { name: "Integration monitor" }).click();
  await page
    .getByRole("button", { name: "Simulate downstream failure" })
    .click();
  await page.getByLabel("Delivery state").selectOption("DeadLetter");
  const row = page
    .locator("integration-monitor tbody tr")
    .filter({ hasText: "DeadLetter" });
  await expect(row).toHaveCount(1);
  await expect(row).toContainText("5 / 5");
  const eventName = await row
    .getByRole("button", { name: /Event #/ })
    .innerText();
  await row.getByRole("button", { name: /Event #/ }).click();
  await expect(page.locator(".event-payload")).toContainText(
    "Synthetic downstream outage",
  );
  await expect(page.getByText(/Last delivery error:/)).toBeVisible();
  await row.getByRole("button", { name: "Retry delivery" }).click();
  await expect(page.getByText("No events match these filters.")).toBeVisible();
  await page.getByLabel("Delivery state").selectOption("Delivered");
  const delivered = page.locator("integration-monitor tbody tr").filter({
    has: page.getByRole("button", { name: eventName, exact: true }),
  });
  await expect(delivered).toContainText("Delivered");
  await expect(
    delivered.getByRole("button", { name: "Retry delivery" }),
  ).toHaveCount(0);
  await expect(page.getByText(/Last delivery error:/)).toHaveCount(0);
  await expect(page.locator(".event-payload")).toContainText(
    "Synthetic downstream outage",
  );
  await page.getByLabel("Search events").fill("no-such-event");
  await expect(page.getByText("No events match these filters.")).toBeVisible();
});

test("separate visitor contexts cannot read each other’s claims", async ({
  page,
  browser,
}) => {
  const reference = await create(page, "1250");
  const claims = await (await page.request.get("/api/claims")).json();
  const claim = claims.find(
    (c: { reference: string }) => c.reference === reference,
  );
  const visitor = await browser.newContext();
  try {
    const other = await visitor.newPage();
    await other.goto("http://127.0.0.1:5086/");
    await login(other);
    await expect(other.locator("tbody")).not.toContainText(reference);
    expect(
      (
        await other.request.get(`http://127.0.0.1:5086/api/claims/${claim.id}`)
      ).status(),
    ).toBe(404);
    expect(
      (
        await other.request.get(
          `http://127.0.0.1:5086/api/claims/${claim.id}/history`,
        )
      ).status(),
    ).toBe(404);
  } finally {
    await visitor.close();
  }
});

test("lost session clears claim details and audit data and allows a fresh workspace", async ({
  page,
  context,
}) => {
  const reference = await create(page);
  await expect(page.locator("case-workspace .timeline li")).toHaveCount(1);
  // Expire the actual cookie; no mocked API responses or production test endpoints.
  await context.clearCookies();
  await page.getByRole("button", { name: "Refresh" }).click();
  await expect(page.locator(".profile b")).toHaveText("Not connected");
  await expect(page.locator("case-workspace")).toHaveCount(0);
  await expect(page.locator("tbody tr")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "New claim" })).toBeDisabled();
  await login(page);
  await expect(page.locator("tbody tr")).toHaveCount(8);
  await expect(page.locator("tbody")).not.toContainText(reference);
});

test("a stale review in a second tab cannot overwrite a final decision", async ({
  page,
  context,
}) => {
  const reference = await create(page, "1250");
  await page.getByRole("button", { name: "Assign to me" }).click();
  await expect(page.locator("case-workspace .badge")).toHaveText("In review");
  const stale = await context.newPage();
  await stale.goto("/");
  await stale.getByRole("button", { name: reference, exact: true }).click();
  await expect(stale.locator("case-workspace .badge")).toHaveText("In review");
  await stale
    .getByLabel("Decision rationale")
    .fill("Decision based on an outdated review.");
  await page
    .getByLabel("Decision rationale")
    .fill("Approved with current evidence.");
  await page
    .getByRole("button", { name: "Approve claim", exact: true })
    .click();
  await expect(page.locator("case-workspace .badge")).toHaveText("Approved");
  const conflict = stale.waitForResponse(
    (r) => r.url().endsWith("/reject") && r.request().method() === "POST",
  );
  await stale
    .getByRole("button", { name: "Reject claim", exact: true })
    .click();
  expect((await conflict).status()).toBe(409);
  await expect(stale.locator("case-workspace .badge")).toHaveText("Approved");
  await expect(stale.locator(".notice.error")).toBeVisible();
  await expect(stale.locator("case-workspace .timeline li")).toHaveCount(3);
  await expect(stale.locator("case-workspace .timeline")).not.toContainText(
    "Decision based on an outdated review.",
  );
});
