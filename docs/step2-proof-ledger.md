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
| 3 | Interleaved writers on the high-water paths - no lost update | **OPEN - owed by WORKER 4** | The assertion the step cannot ship without. The deliberate red is a **mutation of the PRODUCT code** - the three high-water upserts in `GatewayStatsWriter` replaced by change-tracked read-then-save, keeping the comparison so it still LOOKS correct - not a fake writer inside the test. Red and green both owed against a real Postgres; the run is queued and has **produced no result**. **When closed this is a CONCURRENCY proof and NOT a schema proof** - see the fixture note below. |
| 4 | Idempotency - replaying one snapshot ten times equals replaying it once | **OPEN - owed by WORKER 4** | Run queued, **no result** yet. **When closed this is a CONCURRENCY proof and NOT a schema proof** - see the fixture note below. |
| 5 | Tenant partitioning, every table, on the canonicalised identifier | **PARTIAL** | Worker 3 closed the accessor half: the two `SessionCounts` accessors take a tenant and cannot return a foreign row (explicit LINQ join to the identity tables), the membership mirror is keyed by `(TenantId, Id, SessionId)`, five scope tests green. The whole-store sweep across all sixteen tables is OWED by worker 8. |
| 6 | Boundaries - hour and day rollover, clock skew, out-of-order, integer limits, decreasing counters, null and absent fields, non-ASCII | **OPEN** | Worker 8. Worker 5's concurrency boundary cases (hour and day rolls, weekly window, retention boundary, empty rosters) hold **but EXCLUDE out-of-order observation** - see row 7. |
| 7 | Output parity - identical `/stats/data` bodies | **OPEN - owed by WORKER 5 and WORKER 8** | **REOPENED by review 3, which found a REAL DIVERGENCE, not a test gap.** Observe hour H with session-a, then hour H+1, then hour H again with session-b: the original `Snapshot` reports `sessions=1`, the database port reports `2`. The original CLEARS its in-memory dedup set whenever the hour key changes - including moving BACKWARDS - so a returning hour starts counting again and its stored maximum does not grow; the port keeps member rows and unions them. Out-of-order timestamps are explicitly named in the boundary list, so this is in scope, not an edge nobody promised. Worker 5 owes the fix (match the original exactly - parity is the bar and a port carries semantics forward) and a re-run. Worker 3's read parity is **DTO-level, NOT body-level, and must not be counted as covering this**. The body-level proof for the sixteen tables is OWED by worker 8. |
| 8 | The suite proven to DETECT - break the implementation on purpose | **PARTIAL** | Closed ONLY for the privilege proof (worker 1). Worker 4's write-path red IS a genuine product mutation and closes that half when its run lands. **The concurrency lost-update red is NOT closed:** review 3 established that the committed red uses a FAKE WRITER inside the test rather than a mutation of the PRODUCT code, and those are two different claims - a fake writer proves the assertion can fail, not that the shipped upserts are what makes it pass. The isolated read-then-save product mutation is built but NOT RUN. Owed by worker 5; also owed for the sixteen-table suite by worker 8 and the read parity comparison by worker 3. |
| 9 | The no-SQLite guard proven by making it trip | **OPEN** | Worker 7. |
| 10 | The Gateway starts and serves a roster with the statistics database unreachable | **OPEN - owed by WORKER 6 (seated)** | Worker 6. Must be watched failing against a build where the statistics migration is fatal. |
| 11 | The SQLite baseline is structurally equivalent to a real version 5 file, and the comparison is proven to detect | **OPEN - owed by WORKER 2** | **This is one of the two rows the authorised desktop release will be cut on. Do not read it as closed because the literal-DDL decision was made: that decision made equivalence ACHIEVABLE; only the diff makes it DEMONSTRATED.** Baseline being rewritten as the literal version 5 DDL so equivalence is true by construction; structural `sqlite_master` diff against a file built by RUNNING the existing code, table by table including index names, primary key column order, uniqueness, nullability and column order; detector proven by renaming an index on purpose and watching the failure name it. An independent reviewer already found four divergences here by probing - dropped `tenant` defaults on eight tables, rowid key nullability metadata, named primary key constraints, and `user_version` left at 0. |
| 12 | Self-host adoption of an existing version 5 store, non-fatal, with the fixture built by running the real old code | **OPEN** | Worker 2. Evidence packaged for the authorised desktop release at `docs/evidence/step2-self-host-adoption.md`, for a release seat that will not have us to ask. |
| 13 | A migration added without moving `PRAGMA user_version` fails a test that NAMES the omission | **OPEN - owed by WORKER 2** | Enforcement must be MECHANICAL, not a comment and not a remembered rule. The expected stamp is DERIVED FROM THE CHAIN - four plus the number of migrations, where the 4 encodes that the baseline collapses versions 1 through 5 into one migration and must be commented as such or someone will "correct" it as an off-by-one. A constant is the same forgettable rule wearing a different hat. Proven by adding a throwaway migration without a bump and watching it go red naming the omission. |
| 16 | A fresh statistics file stamps `PRAGMA user_version = 5`, so an OLDER build meeting a NEWER file refuses loudly instead of crashing | **OPEN - owed by WORKER 2** | **The second of the two rows the authorised desktop release will be cut on.** Found by an independent reviewer, and named by nobody before that: a file left at `user_version = 0` is not safely openable by the OLD build - it reads 0, tries to run migrations 1 through 5 against tables that already exist, and dies on a duplicate `ALTER TABLE`. A user rolling back a desktop build is a thing that will actually happen, so this is our regression arriving on their machine by a route none of us was looking at. Stamping 5 turns that crash into the loud refusal the original author already designed for, since the old code ALREADY fails correctly on a file whose version exceeds its build. Owed: the stamp, and a test that a downgrade gets the refusal rather than the crash. |
| 14 | The concurrency store on Postgres | **OPEN - owed by WORKER 5** | Corrected by review 3. The SQLite arm is NOT fully closed: the lost-update red is a fake-writer red rather than a product mutation (row 8), and the parity arm is reopened by the out-of-order divergence (row 7). The Postgres arms are written, gated and NOT YET RUN. Review 3's own baseline run TIMED OUT waiting for the fleet lock and PRODUCED NO RESULT - recorded here as no result, which closes nothing. |
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
- Worker 4: **half and half, and it corrected me on this itself.** The SQLite facts are built by RUNNING
  the existing `GatewayStatsDatabase`. The **Postgres** facts create their tables from the MODEL via
  `GenerateCreateScript`, because no statistics migration exists on that branch yet - so those are
  CONCURRENCY proofs and NOT schema proofs.

## The merge-time obligation nobody may forget

**Worker 4's Postgres fixtures and worker 5's SQLite fixtures are both built from the MODEL, not from a
migration, because worker 2's migration did not exist when they were written. Both MUST be rebuilt on
that migration once it lands.** Until they are, those suites prove behaviour and prove nothing about the
schema - a fixture built from the same model the test exercises agrees by construction and passes just
as happily when both are wrong together. This is written here rather than left with the workers because
both of them will be reaped long before the merge, and the obligation must outlive them.
