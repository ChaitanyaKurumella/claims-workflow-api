# Public sandbox verification — 2026-10-25

Public sandbox: https://claims-workflow-api.onrender.com/

Implementation commit: `06243bbcbcb54bab944ec7bf5edee82fbbe894fe`. Deployed on Render Free on 2026-10-25 with owner approval.

- Backend Release suite: 38 passed, 0 failed (35 existing cases plus isolation/expiry, capacity/mutation limits, and proxy HTTPS/cookie tests).
- Frontend: formatting check, six tests and Angular production build passed.
- Locked dependencies: `npm ci` completed; zero npm audit vulnerabilities after updating transitive body-parser from 2.3.0 to 2.4.0.
- ASP.NET Release publish completed with the built Angular assets included.
- Packaged app smoke check: health, seeded reviewer session, two independent cookie workspaces, inaccessible cross-visitor claim, preserved data after supervisor role switch, operations access, and frontend delivery passed locally.

GitHub CI passed. Render built the Docker image and reported successful deployment. The workspace had no payment card, a $0 pipeline spend limit, and the service used Free compute with no disk/database.

Remote checks passed: HTTPS health and frontend delivery; Secure/HttpOnly/SameSite=Strict cookie; unauthenticated and cross-origin rejection; two visitors with isolated claims; claim creation, assignment, reviewer sign-off, rejected reviewer final approval, independent supervisor approval, audit history and operations access. Browser walkthrough completed the seeded CLM-2026-0843 approval with both rationales visible. Cold-start, synthetic-data and reset notices were visible before role selection.

Expiry/capacity/mutation limits were verified locally using deterministic tests; a full 30-minute hosted expiry observation and sustained hosted load are not claimed. The public demo uses simulated local delivery; external HTTP recovery remains demonstrated separately by repository evidence.

## Runtime choices

Public demo is opt-in and rejects service keys, OIDC and external delivery configuration. Each visitor receives a random HttpOnly/SameSite cookie and separate SQLite store. Workspaces expire after 30 minutes; the process bounds active workspaces, mutations, request bodies and request concurrency. The default loopback demo restriction remains in place outside public demo mode.

The deployment must terminate TLS directly at Render ingress, forwarding only the scheme with one-hop processing. Forwarded host is not trusted. Free storage is disposable; cold-start, reset and synthetic-data notices appear in the role selector.
