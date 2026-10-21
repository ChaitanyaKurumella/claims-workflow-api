# Operations

## Local demo

Run `./scripts/run-demo.sh` from the source checkout. It builds static Angular files and starts the API on loopback port 5081. Eight synthetic cases are seeded only when the database is empty. The original v0.1 JSON store is not migrated, deleted, or modified. The v2 API is intentionally incompatible with `/decision`; callers must assign and send versioned commands.

Data defaults to `src/Claims.Api/data/claimdesk-demo.db` when launched with the script and standard dotnet project content root. Set `Claims__DataPath` to an absolute path if you want an unambiguous location. Stop the process before copying the SQLite file for backup. Do not put it on a network share. Restore by stopping the process and replacing the database with a known-good copy. The project does not implement automated retention, live backup, or disaster-recovery orchestration.

## Configuration

| Environment variable | Behavior |
|---|---|
| `Claims__ReviewerKey` | Reviewer service identity, at least 24 characters |
| `Claims__SupervisorKey` | Supervisor service identity, distinct key |
| `Claims__AdministratorKey` | Administrator service identity, distinct key |
| `Claims__ApiKey` | Compatibility alias for administrator key if the latter is absent |
| `Claims__DataPath` | SQLite database file; default `data/claims-v2.db` |
| `Claims__DemoMode` | Default false; allows loopback demo role sessions |
| `Claims__SeedDemo` | Default false; seeds only when demo mode is enabled and claims are empty |
| `Claims__DisableWorker` | Default false; used by deterministic integration tests |
| `Claims__SimulateIntegrationFailure` | Default false; makes local sink delivery fail for resilience exercises |

At least one role key is required, even in demo mode. Duplicate keys across roles are rejected at startup. Keys never belong in a repository, screenshot, log, or chat. The run script generates secrets without printing them. Normal API-key operation requires demo mode disabled and the appropriate role key entered by the user.

## Failure exercise

1. Start a new demo with `Claims__SimulateIntegrationFailure=true ./scripts/run-demo.sh`.
2. Choose Supervisor and open Integration monitor.
3. Refresh over the next minute: events move through retry attempts and eventually DeadLetter. The exact time includes the five-second worker cadence and batch limits.
4. Stop the server. Restart against the same database with `Claims__SimulateIntegrationFailure=false`.
5. Choose Supervisor and click Retry delivery for a dead letter. Refresh after one worker cycle and confirm Delivered.

Claim processing remains durable during delivery failure. A failed worker cycle logs its exception and retries at the next cadence. The local receipt sink is an intentional demonstration boundary, not a real external service.

## Logs and diagnostics

`GET /health` checks a database read and reports schema version. Every response has `X-Request-Id`. API failures include Problem Details; workflow errors include the request ID. Structured request logs include ID, method, path, status, and elapsed milliseconds. Secrets and case summaries are not intentionally logged by this middleware.

The `Claims.Operations` .NET Meter exposes `claims.http.requests` and `claims.http.duration`. A listener or OpenTelemetry exporter must be added/configured to collect them externally. There is no preconfigured Prometheus endpoint or Grafana deployment. Integration counters and the latest 200 event records are available to supervisors/admins in the app.

## Build and release

```sh
(cd web && npm ci && npm run build)
mkdir -p src/Claims.Api/wwwroot
cp -R web/dist/dashboard/browser/. src/Claims.Api/wwwroot/
dotnet test tests/Claims.Tests --configuration Release
dotnet publish src/Claims.Api --configuration Release --output artifacts/app
```

On macOS systems with apphost signing trouble, use `-p:UseAppHost=false` and launch the published DLL with `dotnet Claims.Api.dll`. GitHub Actions runs on Ubuntu. No public release is created by the local demo script.
