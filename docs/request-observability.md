# Request correlation and incident triage

## Implemented contract

Every response that passes through the application pipeline includes `X-Request-Id` (one server request) and `X-Trace-Id` (the W3C trace). ASP.NET Core creates the server activity, accepts valid `traceparent`, and replaces malformed context. Trace IDs are correlation data supplied by potentially untrusted callers; they confer no identity or authorization.

Problem details include `requestId` and `traceId`. Empty authentication and authorization responses retain their existing status and expose correlation in the headers. No new public diagnostics endpoint is introduced.

`RequestTelemetry` wraps the exception handler, demo workspace selection and authentication. Its completion log therefore sees handled error statuses, including early authentication failures. If an exception escapes the handler (for example, after a response has started), telemetry records 500 and rethrows; it does not imply the client received a complete 500 response. Durations measure application pipeline time, not client network latency or completion of response streaming.

Custom request completion logs contain request ID, trace ID, normalized method, route template, response status and elapsed milliseconds. They omit bodies, credentials, cookies, query strings and concrete claim IDs. This is the contract for this custom log, not a guarantee about framework, hosting, exception or downstream logs; review those independently before handling sensitive information.

The `Claims.Operations` meter exposes:

| Instrument | Kind | Unit | Dimensions |
|---|---|---|---|
| `claims.http.requests` | Counter | requests | `http.request.method`, `http.route`, `http.response.status_code` |
| `claims.http.duration` | Histogram | ms | same dimensions |

Methods outside the known set use `OTHER`; endpoints without a route use `unmatched`. IDs and paths never become custom metric dimensions. Version 3 changes the former status-only metric dimensions, so a future collector must use this schema.

## Reproduce locally

Start the app with `bash scripts/run-demo.sh`. In another terminal:

```sh
curl -i http://localhost:5081/health \
  -H 'traceparent: 00-0123456789abcdef0123456789abcdef-0123456789abcdef-01'

# A rejected request still has both correlation headers.
curl -i http://localhost:5081/api/session
```

The first response should return `X-Trace-Id: 0123456789abcdef0123456789abcdef`. Match that trace and the response request ID with the custom completion log. Each request has its own request ID even if its parent trace is shared. Do not send actual credentials or customer data as diagnostic examples.

For structured local console output, set `Logging__Console__FormatterName=json` and `Logging__Console__FormatterOptions__IncludeScopes=true` before running the demo script. The completion message also carries structured fields without requiring scopes.

## Triage procedure

1. Record UTC time, request ID, trace ID, route and observed status. Never copy tokens or payloads into an incident note.
2. Find the custom completion log using request ID; inspect its final status and duration. A trace may span multiple requests and is not an audit identity.
3. For workflow conflicts (409), refresh the claim and read the recorded audit outcome before issuing another decision. Do not blindly retry commands.
4. For delivery failures, use the supervisor Integration monitor to inspect event state, attempts and error. Retry only dead letters after checking the failure cause; receiver deduplication remains necessary.
5. Check `/health` for readiness. Public demo health does not validate every visitor's ephemeral database; inspect workspace-specific errors separately.
6. Verify recovery using a new request and the monitor's delivered state. Record evidence and the remaining uncertainty.

## Deployment work still required

This change provides correlation, structured completion logs and in-process instruments. It does not provision an OpenTelemetry collector, trace storage, dashboards, alerts, retention policies or measured service objectives. HTTP delivery already uses .NET HttpClient instrumentation when a listener is installed; this change does not propagate a durable trace through persisted outbox events. Request IDs, event IDs and audit history remain separate evidence sources.

Before claiming operational readiness, provision telemetry export with reviewed redaction and retention, run controlled failure exercises, and collect a baseline. Candidate objectives are API success ratio and p95 latency over a defined route set, plus oldest pending event age and dead-letter count. Define exclusions, traffic volume, thresholds and time windows from measured behavior; these are proposed indicators, not achieved SLOs.

## Verification

`TelemetryTests` checks valid and malformed trace context, successful and rejected requests, problem correlation, final failure status and bounded metric dimensions. Existing workflow, isolation and browser tests remain the regression gate.
