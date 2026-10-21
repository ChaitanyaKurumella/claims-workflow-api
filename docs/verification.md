# Verification record

## Local release-mode checks

- Backend: **19 passed, 0 failed, 0 skipped**, .NET 8 release configuration.
- Angular: production build passes with strict TypeScript and template checking. Initial raw bundle approximately **261 kB**, estimated transfer **74 kB**. These are build estimates, not browser performance measurements.
- Build uses Node.js 22.23.3 and .NET SDK 8.0.426 on macOS ARM64.
- The test runner needs local socket access. macOS verification used `-p:UseAppHost=false` to avoid an apphost signing issue.

## Scenarios covered by automated tests

1. Invalid references and amounts are rejected (five cases).
2. Invalid category, priority, and null description are rejected (three cases).
3. HTTP workflow requires assignment, current version, and rationale; final decisions conflict.
4. Missing authentication is rejected, client role spoofing fails, reviewer operations are forbidden, and demo sessions are disabled by default.
5. Missing claims return 404.
6. High-value claims require a different supervisor; restart preserves claim, audit, and outbox state.
7. Idempotency replays the original response, detects changed payloads, and rejects case-insensitive duplicate references.
8. Eight independent store connections race to decide one version: exactly one wins, with no extra committed audit or outbox entries.
9. Stale and unauthorized commands leave state and event counts unchanged.
10. SLA escalation occurs once and invalidates stale review commands.
11. Delivery failure backs off, dead-letters after five attempts, and recovers through replay after reopening storage.
12. Final claims do not escalate.
13. An injected SQLite trigger aborts outbox insertion after the claim update: the entire mutation rolls back, including claim state and audit insertion.

Parameterized cases explain the difference between the scenario count and the 19 test results.

## Browser checks

The local app was opened in the existing app tab at http://localhost:5081.

- Reviewer session loaded the eight seeded cases.
- Selecting a case displayed summary, owner, version, policy, and audit history.
- Reviewer requested supervisor sign-off for a $12,600 claim with rationale. The case moved to PendingSupervisor and the audit trail retained the rationale.
- Reviewer did not see final supervisor decision controls for that case.
- Switching to Supervisor exposed those controls and enabled the integration monitor.
- The monitor showed the new event Delivered, with no pending events or dead letters in the observed snapshot.
- The live worker recorded overdue-case escalation and its audit event.

The browser case was deliberately left pending for the user to exercise final approval. Final approval and rejection are verified by automated tests. Browser screenshot capture returned a tool error, so pixel-level visual QA and mobile viewport checks are not claimed. Intake is covered through API tests; the complete browser intake form sequence has not been exercised.

## Boundaries

These changes are local. GitHub CI has not run on them, no release has been published, and no public deployment, external integration, throughput benchmark, or production security assessment is claimed. The v0.1 JSON data remains separate.
