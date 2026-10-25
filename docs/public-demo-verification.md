# Public sandbox verification — 2026-10-25

Prepared against GitHub main at `62da474`. Not deployed yet.

- Backend Release suite: 38 passed, 0 failed (35 existing cases plus isolation/expiry, capacity/mutation limits, and proxy HTTPS/cookie tests).
- Frontend: formatting check, six tests and Angular production build passed.
- Locked dependencies: `npm ci` completed; zero npm audit vulnerabilities after updating transitive body-parser from 2.3.0 to 2.4.0.
- ASP.NET Release publish completed with the built Angular assets included.
- Packaged app smoke check: health, seeded reviewer session, two independent cookie workspaces, inaccessible cross-visitor claim, preserved data after supervisor role switch, operations access, and frontend delivery passed locally.

Docker is unavailable locally; no container build or remote HTTPS verification is claimed. Render service creation and public GitHub publication await owner approval. The remote acceptance checks in [free hosting](free-hosting.md) remain required before advertising a live URL.

## Runtime choices

Public demo is opt-in and rejects service keys, OIDC and external delivery configuration. Each visitor receives a random HttpOnly/SameSite cookie and separate SQLite store. Workspaces expire after 30 minutes; the process bounds active workspaces, mutations, request bodies and request concurrency. The default loopback demo restriction remains in place outside public demo mode.

The deployment must terminate TLS directly at Render ingress, forwarding only the scheme with one-hop processing. Forwarded host is not trusted. Free storage is disposable; cold-start, reset and synthetic-data notices appear in the role selector.
