# Zero-cost hosting readiness

Budget: $0. Do not attach a payment method, start a paid trial, provision a paid disk/database, or upgrade compute. Stop if the provider requires billing details. No hosted service has been created by this preparation.

## Verified provider limits

Render's [free-service documentation](https://render.com/docs/free), checked 2026-10-22, says free web services sleep after 15 minutes idle and take about a minute to wake. Files, including SQLite changes, disappear on restart, redeploy or sleep. Free Postgres expires after 30 days, so it is not a durable free solution for this portfolio.

Render also states that without a payment method, bandwidth exhaustion suspends services and pipeline exhaustion disables builds rather than billing supplemental usage. Verify the actual workspace has no payment method and the service explicitly says Free before deployment. Do not rely on a Free service label alone if the workspace has billing attached.

## Current blockers

1. The Render dashboard requires the account owner to sign in directly.
2. The application's demo role-session endpoint accepts only loopback clients. It must not be exposed by stripping that check or by putting a loopback proxy in front of it.
3. The role-switching sandbox needs visitor isolation, bounded session/storage use and automatic expiry/reset. Sharing one mutable database lets visitors interfere with each other's walkthroughs.
4. TLS termination needs a trusted-forwarded-header design and same-origin tests; the current origin comparison uses the backend request scheme.

## Two supported directions

- Local live demo today: use the release and walkthrough. No hosting account, billing or public identity tenant is required.
- Hosted application: the included Dockerfile builds the current application with demo mode off. It still requires a real OIDC authority/audience and deployment validation. This is a packaging starting point, not a ready public role-switching demo.

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
