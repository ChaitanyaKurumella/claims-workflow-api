# Zero-cost hosting readiness

Budget: $0. Do not attach a payment method, start a paid trial, provision a paid disk/database, or upgrade compute. Stop if the provider requires billing details. No hosted service has been created by this preparation.

## Verified provider limits

Render's [free-service documentation](https://render.com/docs/free), checked 2026-10-25, says free web services sleep after 15 minutes idle and take about a minute to wake. Files, including SQLite changes, disappear on restart, redeploy or sleep. Free Postgres expires after 30 days, so it is not a durable free solution for this portfolio.

Render also states that without a payment method, bandwidth exhaustion suspends services and pipeline exhaustion disables builds rather than billing supplemental usage. Verify the actual workspace has no payment method and the service explicitly says Free before deployment. Do not rely on a Free service label alone if the workspace has billing attached.

## Public sandbox deployment

Implementation passes 38 backend tests and six frontend tests locally, and the frontend production build and application publish are verified. Publication, container build and remote validation remain pending.

In Render select Docker, branch `main`, **Free ($0/month)**, no disk and health check `/health`. Set:

- `Claims__PublicDemo=true`
- `Claims__DemoMode=true`
- `Claims__SeedDemo=false` (each workspace seeds itself)

Do not configure API keys, OIDC or an external delivery URL in public demo mode; startup rejects these combinations. The Dockerfile remains demo-off by default. Free hosting may take about a minute to wake; this is disclosed in the demo workspace.

Each browser cookie identifies an isolated SQLite workspace. Role changes preserve that workspace. Workspaces expire after 30 minutes (not sliding), reset to synthetic samples on the next role selection, and are swept every minute. Restart discards access to all sessions. Limits: 32 concurrent workspaces, 200 mutations per workspace, 16 KiB request bodies, one active sandbox request and a two-second queue wait. Capacity returns HTTP 429; expired sessions return 401 until a role is selected again. Operations advance on sandbox requests rather than an unattended background worker. This demo uses local simulated delivery only.

Render is the TLS terminator. Public mode accepts the final forwarded scheme, with a one-hop limit, while preserving the original Host for origin checks. This setting requires deployment directly behind Render's ingress, with no alternate public backend port. Remote HTTPS checks remain required.

## Container configuration for an authenticated deployment

Build with `docker build -t claimdesk .`. Set `ASPNETCORE_URLS=http://0.0.0.0:10000`, `Claims__Authority` to your HTTPS issuer, `Claims__Audience` to the API audience, and `Claims__DataPath=/tmp/claimdesk/claims.db`. Keep `Claims__DemoMode` and `Claims__SeedDemo` false and omit service role keys. Configure the service health check as `/health`.

The free filesystem is disposable: do not enter real claims or expect recovery across restarts. A real identity provider and production data lifecycle are outside this disposable demonstration. No automatic keep-alive traffic is needed.

## Acceptance checks before calling a public demo complete

- No billing method, paid resource or trial; explicit Free instance.
- HTTPS health check, browser sign-in, same-origin commands and role separation work remotely.
- Visitors cannot access another visitor's mutable sandbox.
- Session expiry and reset restore a usable synthetic scenario with bounded resource use.
- Cold-start and data-loss notices are visible before visitors enter information.
- The published URL passes the walkthrough and restart/reset exercise.

No public demo URL should be added to the README until these checks pass.
