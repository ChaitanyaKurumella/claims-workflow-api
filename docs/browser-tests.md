# Browser regression suite

The suite starts the real ASP.NET Core API in Production environment with public demo isolation enabled and serves the Angular production bundle. Each test receives a fresh Chromium context and server-issued workspace. No external downstream service, API mocks, credentials, or production test endpoints are required.

## Run

Requires .NET 10 SDK and Node.js 22.12+.

```sh
cd web
npm ci
npm run build
npx playwright install chromium
npm run test:e2e
npm run test:e2e:report
```

Port 5086 must be free. The runner deliberately refuses to reuse an existing server so a successful result cannot accidentally refer to an old release. Playwright owns server startup and shutdown. Database workspaces are temporary and disposed with the application. An optional `PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH` points to a local Chrome executable; CI uses Playwright's pinned Chromium instead.

## Coverage

| Scenario | Evidence asserted |
|---|---|
| High-value approval | Intake, assignment, reviewer sign-off request, independent supervisor approval, four audit entries with actors and rationale, immutable final controls |
| Authorization | Reviewer monitor control disabled; operations and simulation requests return 403; direct final-approval attempt returns 403 and leaves state unchanged |
| Integration recovery | Five failures produce a dead letter; payload and error visible; retry delivers the same event, clears the error and removes retry controls; filters and search work |
| Visitor isolation | A second browser context cannot see the first visitor's register entry; detail and history endpoints return 404 |
| Session loss | Removing the real cookie produces an actual 401; register, claim and audit data clear; reconnect creates a fresh seeded workspace |
| Stale review | Two tabs share one session and review the same version; one approves, the other's rejection returns 409; refreshed state and audit retain only the winning decision |

Session loss exercises cookie removal rather than waiting for the 30-minute server expiry. Server expiry with a controlled clock is covered by the backend suite. Browser tests do not replace the backend's transaction rollback, migration, JWT validation, delivery lease or deduplication tests.

## CI and failure diagnosis

GitHub Actions runs formatting, nine frontend unit tests, 40 backend tests, Angular compilation, API packaging, and these six browser scenarios. Browser dependencies are installed with `npx playwright install --with-deps chromium`. A failing browser scenario fails the job and prevents the application artifact upload.

Download `browser-test-results` from the Actions run. Open the HTML report or inspect a retained failure trace with:

```sh
cd web
npx playwright show-trace /path/to/trace.zip
```

Reports are retained for 14 days. Screenshots and traces are captured on failure only; traces can contain browser-visible synthetic data and request details. Do not run this suite against customer accounts or real claims. CI retries once for diagnosis; a retry does not establish that intermittent behavior is fixed.

## Local verification: 2026-10-27 (America/Chicago)

- Six browser scenarios passed using the installed Google Chrome executable on macOS arm64.
- All 40 backend tests and nine frontend unit tests passed.
- Angular production build and Prettier checks passed.
- This local result does not claim that GitHub CI has run the unpublished change, or establish cross-browser compatibility, accessibility compliance, production capacity, or a provisioned identity-provider integration.
