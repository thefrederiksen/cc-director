# Step 2 proof ledger

**A row is CLOSED only by evidence. An honest account of failing to obtain evidence leaves it OPEN.**

That rule exists because the opposite is so comfortable: "this run produced no result" is candid, everyone
nods, and the obligation quietly evaporates because nobody wrote down that it was still owed. Reporting a
no-result is required AND it never closes anything. Every open row below names who owes the answer.

Status values: **CLOSED** (evidence in hand, and the detector was watched failing) - **OPEN** (owed) -
**PARTIAL** (one arm closed, another owed, both named).

**A no-result gets its CAUSE recorded, not just the fact.** "Produced no result" is a row somebody
re-runs hopefully; "produced no result, contention on the fleet-wide test lock, holder identified" is a
row somebody can act on. Name the cause when it is known and say so when it is not.

**A test that fails partway leaves every later assertion in it UNEXECUTED.** One failure can silently
un-prove several claims that were never reached, and a report listing "1 failed" reads as one lost
claim. This is a false-coverage mechanism hiding inside an ordinary test report, and it is invisible in
every summary format anyone uses: a count of passing tests says nothing about which assertions inside a
failing one never ran. **A partially-executed test must NAME the assertions that did not run, and the
rows depending on them stay OPEN.**

**A FIXTURE THAT CANNOT DISTINGUISH THE BUG FROM ITS ABSENCE IS REFUSED, NOT RUN.** Worker 3's
fixture-shape guard caught its own author's fixture: two tenants both totalling the same turns on a
repository whose display spelling they share is exactly the fixture in which two tenants coalescing into
one surrogate identity is invisible, because the numbers are identical whether the defect is present or
absent. Every assertion downstream would have passed with the defect sitting there.

Adopt the SHAPE, not that specific check: before a fixture is used, assert that it COULD show the
failure - distinct values where a collapse would be visible, more than one tenant where partitioning is
the claim, a second row where a lost update would show. It does not ask the author to be thoughtful; it
refuses a fixture that cannot fail. The author is always the last person able to see the gap, so a guard
that rejects its own author's work is the only kind that reliably works.

Last updated by the Step 2 Manager. Branch `nosqlite-stats`.

| # | Proof row | Status | Evidence, or what is owed and by whom |
|---|---|---|---|
| 1 | The hosted role can create its own schema, tables and migrations history | **PARTIAL - the creation proof is CLOSED, the RIG-FIDELITY arm is OPEN and UNOWNED** | Restricted role holding only CREATE on the database creates `gateway_stats`, owns it, applies a two-migration chain with history at `gateway_stats.__EFMigrationsHistory`. Role asserted from the catalog to mirror the live-measured hosted grants, **including that it is NOT the database owner**. Watched failing: CREATE revoked gives SQLSTATE 42501 inside `NpgsqlHistoryRepository.CreateIfNotExists`; the failing direction is a PERMANENT test (revoke, assert, restore in a finally, re-migrate green). `docs/step2-postgres-privilege-proof.md`.

**WHAT IS NOT CLOSED, and it is the precondition rather than the proof.** Review 2 (branch
`nosqlite-stats-step2-rig-review2`, head `6711c2018`) PROVED BY RUNNING that a REUSED rig can keep extra
role memberships, or acquire database or gateway-schema ownership, and still exit zero - it built those
drifted rigs and the restricted login really did create a table in `public` and really did drop the
`gateway` schema. Worker 1's test asserts the mirror from the catalog, so the guard may well exist at
the point of use - **but the run that would settle it never executed, because the lock never yielded.**
So it is unverified whether that test CATCHES a drifted rig or merely asserts a shape it can be handed.
Until that runs, the creation proof stands on a rig whose fidelity is assumed. Review 2's other findings
- a non-container port squatter passing readiness, an arbitrary `-Port` accepted on an existing
container, plaintext passwords in output and process arguments, a changed superuser password producing a
green rig with an invalid connection string - are unaddressed script defects. |
| 2 | All sixteen tables, write path and read projection | **OPEN** | Worker 8 (contract suite, not yet seated). Worker 3 has the read side building; worker 4 the write side. |
| 3 | Interleaved writers on the high-water paths - no lost update | **OPEN - owed by WORKER 4** | The assertion the step cannot ship without. The deliberate red is a **mutation of the PRODUCT code** - the three high-water upserts in `GatewayStatsWriter` replaced by change-tracked read-then-save, keeping the comparison so it still LOOKS correct - not a fake writer inside the test. Red and green both owed against a real Postgres; the run is queued and has **produced no result**. **When closed this is a CONCURRENCY proof and NOT a schema proof** - see the fixture note below. |
| 4 | Idempotency - replaying one snapshot ten times equals replaying it once | **OPEN - owed by WORKER 4** | Run queued, **no result** yet. **When closed this is a CONCURRENCY proof and NOT a schema proof** - see the fixture note below. |
| 5 | Tenant partitioning, every table, on the canonicalised identifier | **PARTIAL** | Worker 3 closed the accessor half: the two `SessionCounts` accessors take a tenant and cannot return a foreign row (explicit LINQ join to the identity tables), the membership mirror is keyed by `(TenantId, Id, SessionId)`, five scope tests green. The whole-store sweep across all sixteen tables is OWED by worker 8. |
| 6 | Boundaries - hour and day rollover, clock skew, out-of-order, integer limits, decreasing counters, null and absent fields, non-ASCII | **OPEN** | Worker 8. Worker 5's concurrency boundary cases (hour and day rolls, weekly window, retention boundary, empty rosters) hold **but EXCLUDE out-of-order observation** - see row 7. |
| 7 | Output parity - identical `/stats/data` bodies | **OPEN - owed by WORKER 5 and WORKER 8** | **REOPENED by review 3, which found a REAL DIVERGENCE, not a test gap. FIXED and pushed at `26b0bf48b`; the fix is unrun so the row stays open.** Observe hour H with session-a, then hour H+1, then hour H again with session-b: the original `Snapshot` reports `sessions=1`, the database port reports `2`. The original CLEARS its in-memory dedup set whenever the hour key changes - including moving BACKWARDS - so a returning hour starts counting again and its stored maximum does not grow; the port keeps member rows and unions them. Out-of-order timestamps are explicitly named in the boundary list, so this is in scope, not an edge nobody promised. **Worker 5's own parity suite had NAMED this divergence in a scope comment and left it uncovered, on the grounds that production time is monotonic. Naming a gap is not closing it, and that comment is now replaced by the test** - which is the general lesson, not a note about one comment. The fix makes `concurrency_hour_member` hold the CURRENT HOUR ONLY, discarding every other hour's rows for that tenant the moment the hour changes in either direction, because that is exactly what the three in-memory lists in the JSON file were. **Worker 2 needs this for the merge: that table is not 90 days of hours.** The 90-day prune stays, because it is what clears the last hour of a tenant that stopped being observed. Worker 5 owes the re-run. Worker 3's read parity is **DTO-level, NOT body-level, and must not be counted as covering this**. The body-level proof for the sixteen tables is OWED by worker 8. |
| 8 | The suite proven to DETECT - break the implementation on purpose | **PARTIAL** | Closed ONLY for the privilege proof (worker 1). Worker 4's write-path red IS a genuine product mutation and closes that half when its run lands. **The concurrency lost-update red is NOT closed:** review 3 established that the committed red uses a FAKE WRITER inside the test rather than a mutation of the PRODUCT code, and those are two different claims - a fake writer proves the assertion can fail, not that the shipped upserts are what makes it pass. **I recorded that worker 5 had the product mutation BUILT. It had not said that, and it corrected me - the statement came from review 3, and I attributed it to the worker's own work.** Worker 5's own account: the fake-writer red was watched; the product mutation is now written and ready to apply, unrun. **AND IT ESTABLISHED WHY THE OBVIOUS MUTATION PROVES NOTHING HERE:** a read-then-save mutation of its product code would PASS its DETERMINISTIC race, and that is not a weak mutation, it is where the window actually is - the store decides whether to write from its in-memory SHADOW rather than from a database read, so a read-then-save re-reads at write time, sees the other container's value, and correctly declines. The lost update for a read-then-save lives between ITS read and ITS write, and only genuine concurrency opens that window. So the product mutation must be judged by the THREADED four-container test; the deterministic test keeps its separate job of proving the assertion can fail at all. Two tests, two different claims - and running the mutation against the wrong one would have produced a green that read as vindication. **Worker 3's read-parity detector IS closed and is PERMANENT** (`232f34782`), not a red anyone must remember to run: it renders both readers, asserts they AGREE first so a later failure means something, then reduces one number the FROZEN pre-port reader returns by exactly what a lost turn would cost and requires the comparison to reject the pair AND name the field that moved - so if the comparison ever goes blind, that test fails rather than the parity test quietly agreeing. Owed by worker 5; also owed for the sixteen-table suite by worker 8 and the read parity comparison by worker 3. |
| 9 | The no-SQLite guard proven by making it trip | **CLOSED for the TRIP PROOF; head re-run owed by WORKER 7** | A Mono.Cecil METADATA scan over the compiled `CcDirector.Gateway.dll` and `CcDirector.Gateway.Host.dll` with ALLOWLIST polarity (four types named, each with a written reason it cannot reach hosted), plus `HostedSqliteGuard` as a runtime refusal. **Static rather than startup-or-composed-host deliberately: both of those are satisfied by NOTHING HAVING HAPPENED, which is the false green this row exists to prevent**, and `Microsoft.Data.Sqlite` exposes no process-wide hook, so a runtime-only guard binds only the call sites that remember to call it. Watched tripping FOUR times: allowlist emptied (the scan detects at all); a new SQLite-backed store that nothing constructs and no test calls (the case a runtime guard passes GREEN); the same store behind a namespace alias that a grep for `new SqliteConnection` misses; and both failure directions of the anti-rot rule. Fixture is the COMPILED PRODUCTION ASSEMBLY read as bytes, sharing no model with what it scans, so it cannot agree by construction. Evidence: `docs/step2-nosqlite-guard-proof.md`. **Owed: a narrow re-run confirming the four rules still pass AT BRANCH HEAD** - three earlier runs produced NO RESULT (two full-suite, one killed by the harness and one on my hold-off; one 17-class run timed out at 10 minutes). The trip proof itself is not rebase-sensitive and is in hand. |
| 10 | The Gateway starts and serves a roster with the statistics database unreachable | **OPEN - owed by WORKER 6 (seated)** | Worker 6. Must be watched failing against a build where the statistics migration is fatal. |
| 11 | The SQLite baseline is structurally equivalent to a real version 5 file, and the comparison is proven to detect | **OPEN - owed by WORKER 2** | **This is one of the two rows the authorised desktop release will be cut on. Do not read it as closed because the literal-DDL decision was made: that decision made equivalence ACHIEVABLE; only the diff makes it DEMONSTRATED.** Baseline being rewritten as the literal version 5 DDL so equivalence is true by construction; structural `sqlite_master` diff against a file built by RUNNING the existing code, table by table including index names, primary key column order, uniqueness, nullability and column order; detector proven by renaming an index on purpose and watching the failure name it. An independent reviewer already found four divergences here by probing - dropped `tenant` defaults on eight tables, rowid key nullability metadata, named primary key constraints, and `user_version` left at 0. |
| 12 | Self-host adoption of an existing version 5 store, non-fatal, with the fixture built by running the real old code | **OPEN** | Worker 2. Evidence packaged for the authorised desktop release at `docs/evidence/step2-self-host-adoption.md`, for a release seat that will not have us to ask. |
| 13 | A migration added without moving `PRAGMA user_version` fails a test that NAMES the omission | **OPEN - owed by WORKER 2** | Enforcement must be MECHANICAL, not a comment and not a remembered rule. The expected stamp is DERIVED FROM THE CHAIN - four plus the number of migrations, where the 4 encodes that the baseline collapses versions 1 through 5 into one migration and must be commented as such or someone will "correct" it as an off-by-one. A constant is the same forgettable rule wearing a different hat. Proven by adding a throwaway migration without a bump and watching it go red naming the omission. |
| 16 | A fresh statistics file stamps `PRAGMA user_version = 5`, so an OLDER build meeting a NEWER file refuses loudly instead of crashing | **OPEN - owed by WORKER 2** | **The second of the two rows the authorised desktop release will be cut on.** Found by an independent reviewer, and named by nobody before that: a file left at `user_version = 0` is not safely openable by the OLD build - it reads 0, tries to run migrations 1 through 5 against tables that already exist, and dies on a duplicate `ALTER TABLE`. A user rolling back a desktop build is a thing that will actually happen, so this is our regression arriving on their machine by a route none of us was looking at. Stamping 5 turns that crash into the loud refusal the original author already designed for, since the old code ALREADY fails correctly on a file whose version exceeds its build. Owed: the stamp, and a test that a downgrade gets the refusal rather than the crash. |
| 18 | An interrupted first migration leaves a history table with NO baseline recorded, and adoption reports the store USABLE | **DETECTION CLOSED IN STEP 2 by worker 2; only REPAIR remains out of scope (issue 1132)** | Found by worker 2 in its own step and reported rather than quietly handled. `Adopt` treats the mere PRESENCE of `__EFMigrationsHistory` as meaning the store is tracked, and never inspects WHICH migrations it records. An interrupted first `Migrate` - history table created, baseline not yet recorded - comes back as an empty history beside tables that already exist; adoption reports it usable and the chain then dies on "table stat_delta already exists" **OUTSIDE the containment**. Adoption cannot create that state itself, because it stamps history and baseline in one transaction, and this is the ordinary interrupted-migration failure for anything on this layer rather than an adoption-specific defect - which is why worker 2 did NOT build partial-migration repair. It is named in the evidence document because "the store HAS a history table" and "the store is AT the baseline" are two different claims and only the first is checked.

**Worker 2 then CLOSED the detection half rather than only documenting it** (`e43922319`): the step now
reads the APPLIED MIGRATIONS instead of merely checking the history table exists, and refuses that state
as `MigrationHistoryIncomplete`, contained and named. Deliberately DETECTED and NOT REPAIRED - which
half of an interrupted migration landed is a guess, and guessing it loses data quietly. Its test builds
the state with Entity Framework's own create script rather than a lookalike, and it lives in the
adoption class so it costs no extra lock time. **What remains out of scope is REPAIR only.**

**Architect ruling on the repair half: out of Step 2, because CONTAINMENT is what makes it survivable.** With ruling 1 built, an interrupted-migration failure becomes statistics unavailable with a named reason instead of a crash outside containment. **If worker 6 finds that containment CANNOT cover a `Migrate` that throws mid-chain, part-applied, this row comes straight back into Step 2 and BLOCKS.** **Worker 6 has now SETTLED it: containment DOES cover a mid-chain throw** - `Migrate` runs inside the
awaited task the constructor unwraps, so a throw from migration three of five reaches the boundary catch
exactly like a refused connection, and there is no other call site. Three caveats it volunteered: this
is a CODE READING and a permanent mid-chain fault test is being written to replace it; containment
catches the THROW but cannot prevent the part-applied STATE, because that state is made by a process
that DIES mid-migration and a try-catch catches nothing when the process is gone - what containment buys
is that the NEXT startup over that state is survivable; and the 20-second deadline ABANDONS rather than
cancels, so a HUNG migration keeps running against the database after the Gateway has declared
statistics unreachable and started serving, meaning a later restart can put a second migration alongside
one that never died. |
| 17 | Every no-SQLite allowlist entry carries a MACHINE-CHECKABLE EXPIRY, and the stale `GatewayStatsDatabase` exemption fails the guard the moment the port lands | **WRITTEN, NEVER EXECUTED - OPEN and UNOWNED** | Promoted from a note to a row on the Architect's ruling, because a note is a rule keyed to somebody REMEMBERING and it guards the exact hole this mission exists to close. The guard must FAIL when `GatewayStatsDatabase` is in the allowlist AND `GatewayStatsDbContext` exists in the scanned assembly - that context existing is a machine-checkable signal that the port HAS HAPPENED and the exemption has expired. The red then arrives when the justification stops being true, not when somebody next reads a document. Generalised: **every entry carries an expiry CONDITION whose becoming true fails the guard, and entries justified only in prose are refused** - an exemption that cannot expire is a permanent hole wearing a temporary label. An entry that genuinely cannot be given a checkable expiry is itself a finding and must be reported, not invented around. Proven the same way as the other four trips: add a stub `GatewayStatsDbContext` to the scanned assembly, watch it go red NAMING the stale exemption, remove it, watch it go green. |
| 14 | The concurrency store on Postgres | **OPEN - owed by WORKER 5** | Corrected by review 3. The SQLite arm is NOT fully closed: the lost-update red is a fake-writer red rather than a product mutation (row 8), and the parity arm is reopened by the out-of-order divergence (row 7). The Postgres arms are written, gated and NOT YET RUN. Review 3's own baseline run TIMED OUT waiting for the fleet lock and PRODUCED NO RESULT - recorded here as no result, which closes nothing. |
| 15 | `gateway-concurrency-stats.json` no longer written on the hosted path | **OPEN - claim owed by WORKER 6, INDEPENDENT VERIFIER UNOWNED** | **I wrote that this would be 'verified by the no-SQLite guard's sibling check'. Worker 7 has told me NO SUCH CHECK EXISTS and never did - its scan matches SQLite TYPES and says nothing whatever about a JSON file being written. I invented a verifier. Recorded here rather than quietly reassigned, because a ledger row naming a verifier that does not exist is worse than a row naming none: it reads as covered.** Worker 6 owns the wiring and the claim. The independent check - that nothing writes that file on the hosted path - is assigned to worker 8's contract suite. |

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

## (Row 17 covers what this section used to only warn about)

## PARKED WORK - rows that currently have NO LIVE OWNER

The mission was cut to three concurrent workers (2, 3 and 4) because seven sessions were queueing on
one fleet-wide test lock, which is a seven-deep queue rather than seven times the work. **Rows 6, 7, 8
(concurrency arm), 14, 15 and 17 are OWED BY NOBODY RIGHT NOW.** They are not abandoned and they are not
closed; they are parked, and they must be reassigned when a slot frees. Written here because an unowned
row reads exactly like a covered one, which is the defect that has bitten this ledger twice already.

**Worker 5, parked - branch `nosqlite-stats-w5-concurrency`, head `d4885c414`, verified identical to
origin, tree clean, rebase aborted rather than parked half-finished.** Still owed on its rows:

1. The Postgres arms of both proofs - written, gated, UNRUN. Three attempts produced NO RESULT (one
   killed at a ten-minute timeout, two that never reached a test behind the lock). It did not fall back
   on its earlier per-class greens.
2. The PRODUCT-code read-then-save mutation, written as
   `docs/step2-w5-mutate-product-to-read-modify-write.py`, UNRUN. **Whoever picks it up must judge it
   with the THREADED four-container test, not the deterministic race** - this store decides from its
   in-memory shadow rather than a database read, so a read-then-save re-reads at write time and
   correctly declines. Only real concurrency opens that window.
3. The SQLite migration for its three tables, plus rebuilding its fixtures on it. Rebase onto
   `e0c401b50`; three DbSets through the single `ConcurrencyStatsModel.Configure` call placed **BEFORE**
   the `IsNpgsql` block so its text columns get the C collation; then the second migration with
   `PRAGMA user_version` 5 to 6 in `Up` and the reset in `Down`. **Until that migration exists its suite
   is model-built and is NOT a schema proof.**

Its rig container `cc-pg-stats-proof-w5` on port 55435 is left running deliberately, for whoever takes
these rows. It holds no test lock.

**Worker 6, RE-SEATED and live** (the cut to three workers parked the owner of ruling 1, the safety property this step exists to protect; the constraint was the test LOCK, not the worker count, and serialising slots already fixed the queue) - branch `nosqlite-stats-w6-startup`, head `f64646697`, tree clean. Rows 10 and 15,
both OPEN. **NOTHING ON THIS BRANCH HAS BEEN RUN.** Its one narrow filtered run produced no output
against the fleet lock and was stopped at stand-down - no result, closes nothing. The only standing fact
is that both projects COMPILE. Built but unexecuted: `GatewayStatsStore` containing every
provider-selection, connection and migration failure non-fatally with a named reason and a 20-second
bound so a hanging database cannot hold the port bind; `StatsConnectionSelection` with override-wins,
blank-is-NOT-CONFIGURED, derive-from-`CC_GATEWAY_DB_CONNECTION` with application name `gateway-stats`
and its own pool, never a SQLite file on hosted, database name carried unaltered by construction;
`NotConfigured` and `Unreachable` as distinct reason codes; `IStatsFailureState` carrying exactly Step
1's four members with no endpoint wiring; `SessionConcurrency` nullable and NOT constructed on hosted so
the concurrency file is never written there. **It has NOT watched the Gateway refuse to start** - that
failing-direction arm exists as a permanent test and was never executed. State document:
`docs/step2-w6-startup-boundary-state.md`.
**Worker 7, parked** - branch `nosqlite-stats-w7-guard`, head `ae799c2f5`, remote verified equal to local,
tree clean, rebased on `origin/main`. Rows 17 and the head re-run on row 9, both OPEN and now UNOWNED.

**Row 17 is WRITTEN AND HAS NEVER BEEN RUN - not once.** That is a weaker position than untested code
that has at least executed, and worker 7 wrote that caveat into its own proof document rather than only
reporting it. All four allowlist entries now carry a machine-checkable condition in one of two
directions: TRANSITIONAL entries (both statistics ones) expire when a statistics `DbContext` appears,
which is the signal the port landed; STRUCTURAL entries assert the property they rest on still holds.
**The record type has no constructor without a condition, so a prose-only entry CANNOT be written** -
the rule is structural rather than remembered. No entry had to be declared un-expirable, so there is no
finding of that kind. The stub-context red-and-green pair was NOT done.

Four separate runs produced NO RESULT today: two full-suite (one killed by its harness, one on my
hold-off), one 17-class run timed out at ten minutes, and the last aborted in the lock queue after 572
seconds. It did not fall back on the per-class greens it had in hand.

## The allowlist entry that is knowingly FALSE today

`GatewayStatsDatabase` sits in the no-SQLite guard's allowlist with a written reason, and **that reason
is currently false - it really does open SQLite on hosted.** It is listed so the guard can land before
the port does. Nothing machine-checks that an allowlist reason is TRUE, which worker 7 stated plainly
rather than leaving to be discovered, and this entry is the proof that it can be false. **When workers 2
and 6 land and the hosted path stops opening that store, this entry must be REMOVED, or the guard
carries a permanent hole in the exact place the mission was about.**

## The merge-time obligation nobody may forget

**Worker 4's Postgres fixtures and worker 5's SQLite fixtures are both built from the MODEL, not from a
migration, because worker 2's migration did not exist when they were written. Both MUST be rebuilt on
that migration once it lands.** Until they are, those suites prove behaviour and prove nothing about the
schema - a fixture built from the same model the test exercises agrees by construction and passes just
as happily when both are wrong together. This is written here rather than left with the workers because
both of them will be reaped long before the merge, and the obligation must outlive them.
