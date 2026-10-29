# Local telemetry showcase

## Run

Requires .NET 10, Node 22 and Docker Compose. This dashboard is a local development viewer with in-memory retention, not production monitoring.

```sh
docker compose -f compose.observability.yaml up -d
docker compose -f compose.observability.yaml logs dashboard
# Open the login URL printed in the logs; keep its token private.
export Claims__TelemetryEnabled=true
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
export OTEL_EXPORTER_OTLP_PROTOCOL=grpc
export OTEL_METRIC_EXPORT_INTERVAL=5000
bash scripts/run-demo.sh
```

Only the custom Claims.Operations source and meter are exported. Export is disabled by default. Standard OTLP endpoint, protocol and headers environment variables configure the exporter. For remote export use a reviewed HTTPS collector and secret-managed headers. This change does not configure Render or create paid resources.

## Five-minute investigation

1. Visit the local app at http://localhost:5081 and choose Supervisor. Send `curl -i http://localhost:5081/api/session` without a cookie to generate a 401, then `curl -i http://localhost:5081/health` for a 200.
2. Open dashboard metrics for `claimdesk-api`. Inspect `claims.http.requests` by route and status, and `claims.http.duration` histogram by route. Compare latency distributions only over a meaningful traffic window; sparse demo traffic cannot establish an SLO.
3. Open a `claims.request` trace. Its trace ID matches the response `X-Trace-Id`; the custom completion log contains that ID. Filter failures by status. Request spans contain only normalized method, route template, status and the correlation ID. No URL, query, payload, cookie or authorization header is attached by this instrumentation.
4. For delivery instrumentation, run the non-public local demo above: the worker emits `claims.delivery.attempts` and `claims.delivery.duration`. Stop the app, set `Claims__SimulateIntegrationFailure=true`, restart, create a new claim, and observe failures in the Integration monitor. The simulator uses local store dispatch and therefore does **not** emit HTTP dispatcher telemetry. To exercise that telemetry, configure the HTTP receiver below.

## HTTP delivery exercise

Run `python3 scripts/receiver.py --port 5092` in a separate terminal. Set `Claims__DeliveryUrl=http://127.0.0.1:5092/events`, unset `Claims__SimulateIntegrationFailure`, and restart the local demo. Create a claim and inspect a `claims.delivery` span and `outcome=delivered` metric. Stop the receiver and create another claim: `outcome=failed` records transport failure. Restore the receiver, allow retries, and verify delivered state in the monitor. After retry exhaustion, use the monitor's dead-letter retry action.

Outcomes are `delivered`, `failed`, `lease_rejected`, and `cancelled`. A rejected completion means a lease could not be acknowledged; it does not prove the receiver failed. Cancelled attempts leave the lease for recovery. No event IDs or payloads are exported. Async delivery starts a separate trace; persisted events do not carry the originating request trace. Public-demo workspace simulation and local in-store dispatch are excluded from delivery metrics. Backlog gauges, production storage, alert routing and durable trace linkage remain future work.

## Stop

```sh
docker compose -f compose.observability.yaml down
```

Restarting the dashboard loses its telemetry. Production enablement needs a collector endpoint, authentication, retention, redaction review and baseline-based alert thresholds.

## Verification

`ExportTests` captures actual protobuf HTTP OTLP requests for traces and metrics and checks that request paths, queries and credentials are absent. Delivery regression tests cover successful and failed dispatch, retry and lease recovery. The dashboard container requires Docker and is a separate manual smoke check.

References: [OTLP exporter configuration](https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.18.0/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md), [standalone dashboard](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard/standalone).
