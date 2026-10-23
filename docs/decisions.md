# Decision records

## 001 — Keep one deployable application

Workflow, audit and outbox share a transaction. Separate services would introduce coordination without a demonstrated deployment requirement. Keep domain policy independent of persistence and isolate delivery transport so boundaries can evolve. Tradeoff: API and worker compete for the same SQLite writer; a slow migration blocks writes.

## 002 — Relational claims, immutable event snapshots

Use typed claim columns and integer cents for queryable state, constraints and indexes. Preserve JSON for immutable audit/event snapshots. Migrate existing data rather than reseeding over it. Tradeoff: mapping and schema upgrades require care; event payload evolution still needs versioned contracts before external consumers depend on them.

## 003 — Keyset pages

Bound register queries and use `(status,id)` indexing. A last-seen UUID avoids growing offset scans. Tradeoff: UUID order is not chronological, and concurrent inserts can land before a cursor. Refresh discovers those inserts. A consistent export needs a separate snapshot mechanism. Client filters/counts describe loaded pages explicitly.

## 004 — At-least-once HTTP with leases

Keep network I/O outside database locks. Stable database/event identity, expiring leases and fenced completion support retry and worker recovery. Require transactional consumer deduplication. Tradeoff: duplicates remain possible and events can arrive out of order. A broker or ordered per-claim dispatch should be introduced when requirements justify it, with equivalent crash-window tests.

## 005 — Standard JWT validation, exclusive identity modes

Use ASP.NET JWT bearer validation with OIDC discovery rather than custom token cryptography. Map verified subject and one recognized role. Prevent service/demo credentials from bypassing personal identity mode. Tradeoff: operators must provision a provider, role mappings and token acquisition; the repository does not ship an identity tenant or browser OAuth sign-in flow.

## 006 — Evidence has a scope

Keep executable correctness/recovery tests and raw measured results alongside the claims they support. A sub-second local read probe does not establish sustained production throughput. Passing tests do not establish production readiness or years of experience. Keep limitations visible so reviewers can assess judgment as well as working code.
