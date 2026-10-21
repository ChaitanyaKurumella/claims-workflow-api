# ClaimDesk — claims operations platform

An end-to-end ASP.NET Core and Angular application for claims intake, assigned review, independent supervisor approval, and reliable event delivery.

Independent portfolio project built with AI assistance and synthetic data. This repository demonstrates engineering decisions and tested guarantees; it does not claim production use or employer ownership.

## Run the demo

Requires .NET 8 SDK, Node.js 22.12+, npm, and OpenSSL. From the repository root:

```sh
bash scripts/run-demo.sh
```

Open **http://localhost:5081** and choose **Reviewer**. The script builds Angular, serves it from the API, generates role keys without displaying them, and seeds eight synthetic cases. The previous v0.1 demo and its JSON data are not used or overwritten. Demo mode is explicitly enabled by the script and must remain local.

Try a meaningful workflow:

1. As Reviewer, select `CLM-2026-0843`, enter your rationale, and request supervisor sign-off.
2. Switch to Supervisor and select the same case. Enter an independent rationale and approve it.
3. Inspect the case's versioned decision trail, including both actors.
4. Open Integration monitor to inspect durable event delivery. Refresh after the next worker cycle.
5. Submit a new claim, assign it, and approve or reject it. Amounts below $10,000 require one reviewer; amounts at or above it require two distinct actors.

## What the implementation proves

| Concern | Behavior | Evidence |
|---|---|---|
| Workflow governance | Assignment ownership, rationale required, two-person approval, immutable final decisions | `Store.Apply`, workflow and persistence tests |
| Conflicting decisions | Every command carries a version; only one competing mutation commits | Eight independent SQLite connections race in a test |
| Duplicate requests | Actor-scoped idempotency key replays original response; changed payload conflicts | Restart-safe idempotency table and test |
| Transactional consistency | Claim, audit entry, and outbox event commit together | SQL transaction; rejected mutations create no additional events |
| Background operations | 4h urgent / 24h normal SLA, one escalation event per active overdue case | Worker and deterministic deadline tests |
| Integration failure | Exponential backoff, five attempts, dead letters, authorized replay | Failure injection and restart/recovery test |
| Observability | Request IDs, structured request logs, .NET Meter counters and duration histogram | API middleware; export requires a configured telemetry listener |
| Operational UI | Search, workflow filters, case detail, policy hints, audit timeline, delivery ledger | Angular production build and browser verification |

## Verify

```sh
dotnet test tests/Claims.Tests --configuration Release
(cd web && npm ci && npm run build)
```

CI runs the suite, builds Angular, publishes the API with static assets, and packages the application. A local passing run does not mean the GitHub workflow has run for unpushed changes.

## API

Use `X-Api-Key` with a configured role key, or the loopback-only opt-in demo session cookie. Claims are synthetic USD amounts.

| Method / route | Purpose |
|---|---|
| GET `/health` | Database readiness and schema |
| GET `/api/session` | Server-derived actor and role |
| GET `/api/claims` | Claims register |
| GET `/api/claims/{id}` | Current case and version |
| GET `/api/claims/{id}/history` | Ordered actor-attributed audit events |
| POST `/api/claims` | Intake; requires `Idempotency-Key` of 8–128 characters |
| POST `/api/claims/{id}/assign` | Assign intake to caller |
| POST `/api/claims/{id}/release` | Release review assignment |
| POST `/api/claims/{id}/approve` | Approve or request supervisor sign-off |
| POST `/api/claims/{id}/reject` | Reject with rationale |
| GET `/api/operations` | Supervisor/admin delivery and SLA monitor |
| POST `/api/operations/outbox/{id}/retry` | Replay a dead letter |

Commands carry `{"version":2,"note":"Evidence checked"}`. Decisions require a nonblank note. Errors use Problem Details: 400 invalid input, 401 missing access, 403 forbidden, 404 missing, 409 stale version or policy conflict. Refresh before retrying a conflicting decision; never blindly retry it.

## Engineering documentation

- [Architecture and tradeoffs](docs/architecture.md)
- [Operations, failure injection, and data](docs/operations.md)
- [Verification record](docs/verification.md)

The integration target is a **local durable receipt sink**, not a live insurer or broker. Role keys identify service/demo actors rather than authenticated people. SQLite serializes writers and the register currently reads all claims; large-scale deployments need pagination, a server database, OIDC, and deployment-specific security and observability work. See the documentation for precise boundaries.
