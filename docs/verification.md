# Verification record — 2026-10-21

## Automated checks

- 35 backend cases pass in Release mode: HTTP validation and roles, workflow correctness, independent approval, transaction rollback, eight-store concurrency, idempotency, SLA, retries/dead letters, migration preservation/rollback/future-version rejection, online backup restoration, lease fencing/crash recovery, consumer deduplication and JWT issuer/audience/signature/expiry/role validation.
- Six frontend tests pass: independent approval display policy, assignment/final-state controls, typed conflict handling, unchanged idempotent retries and non-JSON authentication errors and native fetch receiver binding.
- Angular production compilation validates component templates and strict TypeScript models.
- API Release publish succeeds.

JWT tests use signed local fixtures and static discovery configuration. They do not demonstrate a connection to a real OIDC tenant. Frontend tests exercise policy and transport rather than rendered browser components.

## Real HTTP acknowledgement gap

[evidence/http-delivery.json](evidence/http-delivery.json) records a separate loopback receiver/database. The receiver durably accepted an event and returned 503, then accepted the retry. Sender: Delivered, two attempts. Consumer: one durable receipt. Lease-expiry tests separately simulate sender termination before acknowledgement and reject stale completion tokens.

## Local read measurement

[evidence/load-local.json](evidence/load-local.json) contains the raw result: 2,000 seeded claims, 2,000 authenticated first-page reads, 50 rows/page, eight clients, 20 warmups, worker disabled. Sequential seed and warmup time are excluded.

Measured on this macOS arm64 host: 0.883 seconds, 2,265 requests/second, p50 3.15 ms, p95 5.08 ms, p99 6.27 ms, zero errors. This short loopback probe includes client/network/API work but benefits from warm caches and no competing writes. It is a smoke benchmark, not sustained capacity or production latency evidence. Repeat with `scripts/load.py` before comparing environments.

[evidence/query-plans.json](evidence/query-plans.json) confirms status-page use of `ix_claims_status_id` and SLA use of `ix_claims_due`. Query plans and performance vary with SQLite, data distribution and host conditions.

## Recovery evidence

The migration test creates a version-1 database containing an existing claim plus audit/outbox children, upgrades it, checks preserved amounts/version/content and validates foreign keys. A corrupt legacy row forces migration rollback, retaining version 1 and original data. Backup recovery uses the online backup API and verifies the restored earlier claim state and event identity after the live database changes.

## Browser workflow

[evidence/browser-workflow.json](evidence/browser-workflow.json) records the isolated demo check: reviewer sign-off request, hidden reviewer final controls, independent supervisor approval, retained rationales/version history and delivered events. The online backup CLI also completed against that preview database.

## Boundaries

No production traffic, provider tenant, deployment SLO, sustained mixed workload, telemetry collector, full browser regression suite or disaster-recovery timing has been verified. GitHub CI status belongs to the exact published commit; consult its Actions run. The work does not imply production certification.
