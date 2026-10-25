# Three-minute ClaimDesk walkthrough

Use synthetic information only. Open [the public sandbox](https://claims-workflow-api.onrender.com/) or start with `bash scripts/run-demo.sh` and open the printed local address. This is a script for a live walkthrough or recording; no recording is included.

| Time | Action | What to explain |
|---|---|---|
| 0:00–0:30 | Open the claims workspace as Reviewer. | Server-derived roles; register filters and counts cover loaded pages. |
| 0:30–1:15 | Select an in-review claim requiring supervisor sign-off, claim its assignment if needed, enter a rationale and request sign-off. | Workflow policy checks assignment, version and rationale. |
| 1:15–2:00 | Switch to Supervisor, inspect the claim, enter a separate rationale and approve. | An independent actor completes the decision; both rationales remain in history. |
| 2:00–2:30 | Inspect the audit trail and integration monitor. | The decision and outbox record commit atomically; the worker delivers afterward. |
| 2:30–3:00 | Open the verification evidence. | A real HTTP exercise committed a receiver receipt before a 503, then retried using the same event key. |

For a fresh scenario, use a new synthetic claim rather than trying to edit an immutable final decision. Rehearse once before recording. The seeded claim CLM-2026-0843 may already be final in an existing database.

## Optional recovery segment

Follow [operations](operations.md) to run the loopback receiver with `--fail-first 1`. Show two attempts and one durable receiver receipt. The normal local integration sink is not evidence of external HTTP recovery; use the receiver exercise or the checked-in HTTP evidence.

## Clean local reset

Stop the API and worker, choose a new unused `Claims__DataPath`, and rerun the demo script. This creates a separate seeded database without deleting the existing demonstration. Never reset a database containing real information.

## Interview talking points

- Why SQLite: a clear single-host transaction boundary, with explicit limits on multi-node writes.
- Why an outbox: avoid losing a delivery event after committing a decision.
- Why stable event keys: a failed acknowledgement does not prove that the receiver failed to commit.
- What remains: hosted identity setup, telemetry export, sustained deployment tests and sustained hosted load/expiry observation.
