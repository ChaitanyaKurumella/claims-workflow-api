# Operations and security

## Configuration

| Variable | Purpose |
|---|---|
| `Claims__ApiKey` | Required random local secret, at least 24 characters |
| `Claims__DataPath` | Optional path; defaults to `data/claims.json` relative to process working directory |
| `ASPNETCORE_URLS` | Bind locally, e.g. `http://localhost:5080` |

Use synthetic data only. Keep the listener on loopback. The sample's shared key and public liveness endpoint do not make it ready for an internet deployment. Never commit a key or data file. Rotate the key by replacing the environment variable and restarting the process; reconnect the dashboard using the new value.

## Build artifact

The CI artifact contains a published ASP.NET Core app and the built dashboard. Download and extract it, configure the variables above, then run `dotnet Claims.Api.dll`. A compatible .NET 8 ASP.NET runtime is required. Persist the data directory outside the release directory if replacing builds.

## Recovery limits

Stop the process before backing up the JSON file. Keep a copy before experimenting with recovery. There is no supported online backup protocol or migration tooling. If JSON is corrupt, startup fails; investigate and restore a known-good offline backup. Do not delete it to make startup succeed unless you deliberately intend to reset the demo.

## Reporting an issue

Do not attach real claims, personal data, API keys, or private employer code. Report reproducible problems with synthetic inputs and expected versus actual behavior. For sensitive security findings, use GitHub's private reporting facility if available; otherwise avoid including exploitable secrets in public issues.
