# Step 2 proof ledger

**A row is CLOSED only by evidence. An honest account of failing to obtain evidence leaves it OPEN.**

That rule exists because the opposite is so comfortable: "this run produced no result" is candid, everyone
nods, and the obligation quietly evaporates because nobody wrote down that it was still owed. Reporting a
no-result is required AND it never closes anything. Every open row below names who owes the answer.

Status values: **CLOSED** (evidence in hand, and the detector was watched failing) - **OPEN** (owed) -
**PARTIAL** (one arm closed, another owed, both named).

Last updated by the Step 2 Manager. Branch `nosqlite-stats`.

| # | Proof row | Status | Evidence, or what is owed and by whom |
|---|---|---|---|
| 1 | The hosted role can create its own schema, tables and migrations history | **CLOSED** | Restricted role holding only CREATE on the database creates `gateway_stats`, owns it, applies a two-migration chain with history at `gateway_stats.__EFMigrationsHistory`. Role asserted from the catalog to mirror the live-measured hosted grants, **including that it is NOT the database owner**. Watched failing: CREATE revoked gives SQLSTATE 42501 inside `NpgsqlHistoryRepository.CreateIfNotExists`; the failing direction is a PERMANENT test (revoke, assert, restore in a finally, re-migrate green). `docs/step2-postgres-privilege-proof.md`. Independent review re-running its criticals against the branch head. |
| 2 | All sixteen tables, write path and read projection | **OPEN** | Worker 8 (contract suite, not yet seated). Worker 3 has the read side building; worker 4 the write side. |
| 3 | Interleaved writers on the high-water paths - no lost update | **OPEN** | Worker 4. Deliberate change-tracked read-modify-write build is written; red and green both owed against a real Postgres. This is the assertion the step cannot ship without. |
| 4 | Idempotency - replaying one snapshot ten times equals replaying it once | **OPEN** | Worker 4. |
| 5 | Tenant partitioning, every table, on the canonicalised identifier | **PARTIAL** | Worker 3 closed the accessor half: the two `SessionCounts` accessors take a tenant and cannot return a foreign row (explicit LINQ join to the identity tables), the membership mirror is keyed by `(TenantId, Id, SessionId)`, five scope tests green. The whole-store sweep across all sixteen tables is OWED by worker 8. |
| 6 | Boundaries - hour and day rollover, clock skew, out-of-order, integer limits, decreasing counters, null and absent fields, non-ASCII | **OPEN** | Worker 8. Worker 5 has closed the concurrency store's own boundary cases (hour and day rolls, weekly window, retention boundary, empty rosters). |
| 7 | Output parity - identical `/stats/data` bodies | **PARTIAL** | Worker 5 closed it for the concurrency store, comparing RENDERED bodies across two tenants, 33 of 33 on SQLite. Worker 3's read parity is **DTO-level, NOT body-level, and must not be counted as covering this**. The body-level proof for the sixteen tables is OWED by worker 8. |
| 8 | The suite proven to DETECT - break the implementation on purpose | **PARTIAL** | Closed for the privilege proof (worker 1) and the concurrency lost-update path (worker 5, red pinned permanently in the suite). Owed for the sixteen-table suite by worker 8, and for the read parity comparison by worker 3. |
| 9 | The no-SQLite guard proven by making it trip | **OPEN** | Worker 7. |
| 10 | The Gateway starts and serves a roster with the statistics database unreachable | **OPEN - owed by WORKER 6 (seated)** | Worker 6. Must be watched failing against a build where the statistics migration is fatal. |
| 11 | The SQLite baseline is structurally equivalent to a real version 5 file, and the comparison is proven to detect | **OPEN - owed by WORKER 2** | **This is one of the two rows the authorised desktop release will be cut on. Do not read it as closed because the literal-DDL decision was made: that decision made equivalence ACHIEVABLE; only the diff makes it DEMONSTRATED.** Baseline being rewritten as the literal version 5 DDL so equivalence is true by construction; structural `sqlite_master` diff against a file built by RUNNING the existing code, table by table including index names, primary key column order, uniqueness, nullability and column order; detector proven by renaming an index on purpose and watching the failure name it. An independent reviewer already found four divergences here by probing - dropped `tenant` defaults on eight tables, rowid key nullability metadata, named primary key constraints, and `user_version` left at 0. |
| 12 | Self-host adoption of an existing version 5 store, non-fatal, with the fixture built by running the real old code | **OPEN** | Worker 2. Evidence packaged for the authorised desktop release at `docs/evidence/step2-self-host-adoption.md`, for a release seat that will not have us to ask. |
| 13 | A migration added without moving `PRAGMA user_version` fails a test that NAMES the omission | **OPEN - owed by WORKER 2** | Enforcement must be MECHANICAL, not a comment and not a remembered rule. The expected stamp is DERIVED FROM THE CHAIN - four plus the number of migrations, where the 4 encodes that the baseline collapses versions 1 through 5 into one migration and must be commented as such or someone will "correct" it as an off-by-one. A constant is the same forgettable rule wearing a different hat. Proven by adding a throwaway migration without a bump and watching it go red naming the omission. |
| 16 | A fresh statistics file stamps `PRAGMA user_version = 5`, so an OLDER build meeting a NEWER file refuses loudly instead of crashing | **OPEN - owed by WORKER 2** | **The second of the two rows the authorised desktop release will be cut on.** Found by an independent reviewer, and named by nobody before that: a file left at `user_version = 0` is not safely openable by the OLD build - it reads 0, tries to run migrations 1 through 5 against tables that already exist, and dies on a duplicate `ALTER TABLE`. A user rolling back a desktop build is a thing that will actually happen, so this is our regression arriving on their machine by a route none of us was looking at. Stamping 5 turns that crash into the loud refusal the original author already designed for, since the old code ALREADY fails correctly on a file whose version exceeds its build. Owed: the stamp, and a test that a downgrade gets the refusal rather than the crash. |
| 14 | The concurrency store on Postgres | **PARTIAL** | SQLite arm CLOSED (33 of 33, rendered-body parity, lost-update red pinned permanently). Postgres arms of both proofs are written, gated, and **NOT YET RUN** - owed by worker 5. |
| 15 | `gateway-concurrency-stats.json` no longer written on the hosted path | **OPEN - owed by WORKER 6** | Worker 6 wiring, then verified by the no-SQLite guard's sibling check. |

## Known limitations carried forward deliberately, not defects

- **Two containers in one hour under-count distinct sessions, machines and repositories.** Each keeps its
  own in-memory dedup set until an hour roll rehydrates. This is the JSON store's own behaviour, it is
  strictly better than the whole-file clobber it replaces, and the obvious fix (counting rows in the
  member table) is forbidden because no database collation is equivalent to the `OrdinalIgnoreCase`
  comparer that decides identity. Recorded so the next reader does not "fix" it.
- **`repo_session` and `agent_session` carry no tenant column.** Partitioned indirectly through
  per-tenant surrogate ids. Carried forward unchanged; the new accessors cannot return a foreign row.
- **A global query filter on the tenant is NOT applied.** Deferred deliberately - it needs an unscoped
  path for the startup mirror load, which legitimately reads every tenant. Filed as
  `devthrottle_internal` issue 1120 with a detect-by-removal closing condition.

## Fixture routes, stated so nobody mistakes a green suite for a schema proof

- Worker 3: both fixtures built by RUNNING the existing `GatewayStatsDatabase`. Currently the only place
  the entity mapping meets the real on-disk shape - a wrong `ToTable` or `HasColumnName` throws at query
  time rather than passing quietly.
- Worker 5: **model-built (`EnsureCreated`), therefore NOT a schema proof.** Measured, not assumed, to be
  currently harmless: none of the three concurrency tables exists at version 5 in any spelling, because
  that record was a JSON file and never a table. **Must be rebuilt on worker 2's migration once it lands.**
- Worker 4: fixture built by RUNNING the existing `GatewayStatsDatabase`.
