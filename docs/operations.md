# Operations and recovery

## Configuration

All names below use ASP.NET environment variable notation.

| Setting | Purpose |
|---|---|
| `Claims__DataPath` | SQLite path on local persistent disk |
| `Claims__ReviewerKey`, `Claims__SupervisorKey`, `Claims__AdministratorKey` | Distinct service keys, each at least 24 characters; omit in OIDC mode |
| `Claims__Authority`, `Claims__Audience` | HTTPS OIDC authority and expected API audience; configure together |
| `Claims__DemoMode`, `Claims__SeedDemo` | Loopback synthetic demo only; omit/false outside demo |
| `Claims__DeliveryUrl` | External receiver URL, HTTPS except loopback HTTP; absent selects local sink |
| `Claims__DeliveryToken` | Optional receiver bearer credential supplied from a secret store |
| `Claims__DisableWorker` | Disable all background work during isolated load tests |
| `Claims__SimulateIntegrationFailure` | Local sink failure exercise only; external failures come from HTTP |

OIDC access tokens need `sub` and exactly one recognized `roles` value: Reviewer, Supervisor or Administrator. API consumers send `Authorization: Bearer <token>`. The UI accepts `Bearer ` followed by a token, held only in memory. Token acquisition/refresh and a provider-specific sign-in UI are not included. Configure audience and role mappings with your identity provider; do not enable demo alongside OIDC. Never commit secrets.

## External receiver exercise

Run in a separate synthetic environment:

```sh
python3 scripts/receiver.py --database /tmp/claimdesk-receiver.db --fail-first 1
```

Configure the API's `Claims__DeliveryUrl=http://127.0.0.1:5092/events`, start it, and submit a synthetic claim. The receiver commits its receipt before returning a deliberate 503. The worker retries with the same event key; one durable receipt remains and the sender records two attempts. This receiver binds to loopback and has no production authentication: use it for exercises only. See the checked-in HTTP evidence.

A failed or ambiguous response can represent a committed consumer transaction. Never infer that a timeout means the event did not arrive. Inspect dead letters, restore receiver health, and replay through the supervisor/admin endpoint. Replay can repeat delivery; consumer deduplication remains mandatory. The worker polls every five seconds, up to 50 attempts per cycle. An interrupted external attempt becomes eligible again after its 30-second lease. Multiple workers are fenced at completion but do not guarantee ordered consumption.

## Backup, migration and restore

Before upgrading, create an online backup with SQLite's backup API, not a raw copy of a live database:

```sh
python3 scripts/backup.py data/claimdesk-demo.db /safe/location/claimdesk-before-upgrade.db
```

The destination must not exist. The script runs integrity and foreign-key checks. Store backups on separately protected storage according to your retention/encryption policy. The tool does not configure remote backup storage or a schedule.

For restore: stop every API/worker process using that database, retain the damaged database for investigation, restore the backup to a new path, configure `Claims__DataPath` to that path, then start the compatible application. Verify `/health`, a known claim/history and delivery queue before reopening writes. Starting this version on an older valid schema migrates it transactionally. A failed upgrade retains the previous schema. Downgrade requires the previous binary and its matching backup; there are no down migrations.

A restore deliberately retains event identity so already-consumed events can be deduplicated. It can also lose events created after the backup. Reconcile with the downstream consumer before replaying a restored queue. Do not run restored and original databases as independent active writers with the same database identity.

The restore test backs up through SQLite's online API, modifies the live database, then opens the backup and verifies the earlier claim version, history, outbox and event-key identity. This is a recovery exercise, not proof of a deployment-specific recovery-time or recovery-point objective.

## Reproduce the read probe

Start a Release/published API on port 5091 with a fresh synthetic database, `Claims__DisableWorker=true`, and a generated service key. Supply that key to the probe through the environment:

```sh
python3 scripts/load.py --seed 2000 --requests 2000 --concurrency 8 --output load-results.json
```

The probe reads `CLAIMDESK_LOAD_KEY`; it never prints it. Seed and warmup traffic are excluded from timings. It measures authenticated first-page reads of 50 rows. Reusing the database adds another seed set. Do not run against real claims. The checked-in result is a short local smoke measurement, not a sustained capacity test. Mixed read/write saturation, remote-network latency, database growth and long-running resource behavior remain unmeasured.

## Monitoring and deployment work

Watch request latency/error logs, outbox pending/dead-letter totals, expired leases and escalation backlog. Request IDs support correlation. `Claims.Operations` exposes .NET request counters/duration instruments; connect an approved telemetry listener/collector before relying on dashboards or alerts. There is no telemetry exporter or alert deployment in this repository.

SQLite supports this single-host sample. A multi-node deployment requires a database/storage design that preserves the transaction boundary and claim version guarantees, plus coordinated scheduling, delivery ordering requirements, and a tested migration/backup plan. Keep demo mode off and use TLS, personal identities, managed secrets and deployment-specific access controls.

## Development dependencies

The lockfile includes compatible patched Vite and MCP SDK overrides for Angular build/CLI tooling. `npm audit` reported zero vulnerabilities on 2026-10-21 after these updates. Reassess overrides when upgrading Angular tooling; audit results depend on the advisory database at run time. The application serves a production build rather than the Vite development server. Source formatting is pinned through Prettier, with a CI formatting check.
