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

**REFUSED AND UNMODIFIED ARE TWO CLAIMS.** A guard that rejects an input has TWO obligations - decline
it, and leave it untouched - and almost every test written for one asserts only the first. That is not
hypothetical here: adoption certified a FOREIGN database as fresh and then wrote sixteen statistics
tables and a baseline row INTO IT. **The harm was the side effect, not the verdict**, and a test
asserting only that it said no would have stayed green throughout. Wherever a guard refuses something,
assert that nothing was CHANGED as well as that something was DECLINED - and check the other direction
too, that it does not condemn a healthy input.

**The inverse is arguably the worse direction IN THIS DESIGN, and the reason is the containment
itself.** Containment makes a statistics failure non-fatal and quiet by construction. So a guard that
falsely REFUSES a healthy store produces a Gateway that serves perfectly well, with statistics switched
off and a named reason that is a LIE, and nobody looking: it never pages anyone and it never throws. It
would go unnoticed for months. A false ACCEPT at least eventually breaks loudly on the chain. The
side-effect direction is the one that damages data; this one is the one nobody would ever find.

**A FIXTURE THAT CANNOT DISTINGUISH THE BUG FROM ITS ABSENCE IS REFUSED, NOT RUN** - and its twin,
which fails independently: **CAN THE ASSERTION NOTICE?** We ask whether the FIXTURE could exhibit the
failure. The other half is whether the ASSERTION could see it if it did.

Those two fail separately and can fail together. One test here had a fixture that could not exhibit the
defect (a foreign database with no foreign history row) AND an assertion that could not have noticed it
(counting our tables rather than checking theirs survived) - and it passed for both reasons at once,
while being NAMED for the thing it was not doing.

**The name was doing the reassuring.** Anyone reading the suite saw a foreign-database-with-history case
listed and stopped worrying. That is worse than no test, because **no test at least looks like a gap**. Worker 3's
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
| 3 | Interleaved writers on the high-water paths - no lost update | **REOPENED. The watermark claim holds; the PROOF DID NOT COVER THE DEFECT THAT MATTERS.** | The assertion the step cannot ship without. The deliberate red is a **mutation of the PRODUCT code** - the three high-water upserts in `GatewayStatsWriter` replaced by change-tracked read-then-save, keeping the comparison so it still LOOKS correct - not a fake writer inside the test. **RED, against the mutated shipped upserts** (the three high-water writes replaced by change-tracked
read-then-save, with the comparison KEPT so the code still looks correct) - Failed 4, Passed 3:
`SessionHighWater_InterleavedWriters_DoNotLoseAnUpdate` gave `Assert.Equal() Failure: Values differ /
Expected: 10 / Actual: 7`; `TokenHighWater` gave `Expected: 900 / Actual: 300`; `AgentDrivenHighWater`
gave `Expected: 12 / Actual: 6`; and `ManyConcurrentWriters` gave `Assert.Empty() Failure: Collection
was not empty` carrying `23505: duplicate key value violates unique constraint PK_session_highwater` -
**read-then-save does not only lose an update, it races two INSERTs into a primary key violation.**
**GREEN after reverting the mutation**, same assembly, same rig, same interleave: Passed 7, Failed 0,
plus 80 SQLite facts green.

**Assertions that did NOT execute in the red, and are therefore not covered by it:** session chars, the
other three token columns, agent-driven chars, and the highest-value readback in `ManyConcurrentWriters`.
All four executed and passed in the green.

**REVIEW 8 (the fix round): four of five FIXED and proved by running - the delta invariant holds under
a four-writer probe including a no-change and a stale-lower observation, a mid-transaction failure rolls
back both halves, concurrent pruners archive only what their own delete removed, and the load-bearing
mutation reproduced EXACTLY at 10 passed 2 failed, failing on the LEDGER with every watermark assertion
green. BUT THE FIXES INTRODUCED A PRODUCTION DEADLOCK AND FINDING 5 IS STILL NOT FIXED.**

**NEW - identity upserts DEADLOCK.** `ResolveIdentities` takes row locks in the INSERTION order of the
batch, which the aggregator builds in OBSERVATION order - no canonical sort, no retry. Two writers
minting the same two repositories in opposite order produced a real `40P01 deadlock_detected` that
aborted an actual `Commit`. The unique-index upsert that fixed the duplicate-identity race is what
created the conflicting row locks. **A dual-container fold seeing novel repositories in different roster
order can now fail a live request.**

**NOT FIXED - the race witness still certifies the wrong lock.** It is stronger now (names writer B,
requires not-granted, restricts lock type, requires A in the blocking set) but it never binds that lock
to the CONTESTED WATERMARK ROW. Identity upserts run BEFORE any raise, so writer B blocks on A's
IDENTITY row and satisfies every clause. The reviewer made writer B's watermark operation a NO-OP - **no
watermark work at all - and the fact still PASSED.**

**REMAINING - case-variant mints still split one identity.** The unique index is exact-spelling; the
documented invariant is case-INSENSITIVE. Two mirrors minting `Owner/Repo` and `owner/repo` produced two
rows and two ids.

**THE RESIDUAL WAS UNDERSTATED AND IS NOW CORRECTED.** Worker 4 bounded the cross-incarnation error to
"one poll interval". Review 8 proved it is **PERMANENT and NOT poll-bounded**: seeded 100 banked turns,
a new-incarnation reset of 5, then a delayed old-incarnation observation of 110 left the ledger at 210
against honest activity of 115. **One collision overcounted by 95, forever, and the magnitude scales
with the pre-reset watermark.** One poll interval describes how long both readings are normally
available - it does not bound the durable error.

**REVIEW 7 REOPENED THIS ROW, PROVED BY RUNNING.** The explicit upserts do stop the watermark
regressing - that was audited, not sampled, and every named high-water and membership write uses the
right conflict clause. But **concurrent writers APPEND THE SAME LOGICAL GROWTH TWICE**: each computes
its delta against its OWN private mirror, and raising a shared high-water row does not arbitrate WHO
OWNS that growth. Seeded at 100, two real concurrent transactions from that stale baseline - one
proposing 300 with growth 200, the other 200 with growth 100 - left the watermark correctly at 300 and
`token_delta` summing to **300 where 200 is correct**. So the all-time and hourly numbers PERMANENTLY
OVERCOUNT during exactly the two-container overlap this port exists to survive. **The shipped proof
asserted only the high-water rows and never the delta totals after an interleave, which is why it went
green.** A proof that does not assert the quantity that breaks is not a weaker proof, it is a proof of a
different claim.

Three more, all proved by running: two containers MINT TWO IDENTITIES for one display (2 rows where 1 is
correct, splitting a repository across surrogate ids); concurrent retention pruning ARCHIVES THE SAME
ROWS TWICE (202 where 102 is correct, permanently doubling aged history); and the race witness itself
can FALSE-GREEN - it counts any backend in the database waiting on a Lock without identifying writer
B's backend, transaction, table or row, and the reviewer made it accept a race that never happened by
holding an unrelated advisory lock. **I had recorded that refusal as exemplary. The idea is right and
the identification is too loose.**

**The race is FORCED, not hoped for**, and this belongs with the numbers or a later reader will assume
it is timing-dependent: two real threads, two connections, two transactions open at once, with only the
ORDER arranged. Writer B starts only after A signals from inside `beforeCommit` - statement executed,
not yet committed - and A does not commit until the SERVER reports another session blocked on a lock,
which can only be B's `UPDATE` and therefore only after B's `SELECT`. So B is guaranteed to read the
pre-A value. **When closed this is a CONCURRENCY proof and NOT a schema proof** - see the fixture note below. |
| 4 | Idempotency - replaying one snapshot ten times equals replaying it once | **REOPENED - the ordinary replay holds; a Director counter RESET followed by a Gateway RESTART double-counts.** | Green in the same window as row 3, 7 of 7 on Postgres plus 80 SQLite facts. **When closed this is a CONCURRENCY proof and NOT a schema proof** - see the fixture note below. |
| 5 | Tenant partitioning, every table, on the canonicalised identifier | **PARTIAL** | Worker 3 closed the accessor half: the two `SessionCounts` accessors take a tenant and cannot return a foreign row (explicit LINQ join to the identity tables), the membership mirror is keyed by `(TenantId, Id, SessionId)`, five scope tests green. The whole-store sweep across all sixteen tables is OWED by worker 8. |
| 6 | Boundaries - hour and day rollover, clock skew, out-of-order, integer limits, decreasing counters, null and absent fields, non-ASCII | **OPEN** | Worker 8. Worker 5's concurrency boundary cases (hour and day rolls, weekly window, retention boundary, empty rosters) hold **but EXCLUDE out-of-order observation** - see row 7. |
| 7 | Output parity - identical `/stats/data` bodies | **DTO-level arm CLOSED (worker 3); BODY-level for the sixteen tables OPEN, owed by WORKER 8; concurrency arm's re-run OPEN and UNOWNED** | **REOPENED by review 3, which found a REAL DIVERGENCE, not a test gap. FIXED and pushed at `26b0bf48b`; the fix is unrun so the row stays open.** Observe hour H with session-a, then hour H+1, then hour H again with session-b: the original `Snapshot` reports `sessions=1`, the database port reports `2`. The original CLEARS its in-memory dedup set whenever the hour key changes - including moving BACKWARDS - so a returning hour starts counting again and its stored maximum does not grow; the port keeps member rows and unions them. Out-of-order timestamps are explicitly named in the boundary list, so this is in scope, not an edge nobody promised. **Worker 5's own parity suite had NAMED this divergence in a scope comment and left it uncovered, on the grounds that production time is monotonic. Naming a gap is not closing it, and that comment is now replaced by the test** - which is the general lesson, not a note about one comment. The fix makes `concurrency_hour_member` hold the CURRENT HOUR ONLY, discarding every other hour's rows for that tenant the moment the hour changes in either direction, because that is exactly what the three in-memory lists in the JSON file were. **Worker 2 needs this for the merge: that table is not 90 days of hours.** The 90-day prune stays, because it is what clears the last hour of a tenant that stopped being observed. Worker 5 owes the re-run. Worker 3's read parity is **DTO-level, NOT body-level, and must not be counted as covering this**. Its
run: `Total tests: 8, Passed: 8` - including `EveryProjection_RendersIdenticallyBeforeAndAfterThePort`,
`TheComparison_RejectsAOneNumberDifference_AndNamesTheField`, and
`TheFixtureExercisesTheShapesParityIsSupposedToCatch`. **Nothing failed, so nothing went unexecuted** -
the four assertions that never ran in the earlier partial failure (archive row excluded from the hourly
and token-hour series while included in all-time, the null-model bucket in both model projections, a
genuine turns-and-characters tie in the repository ranking, more than one hour bucket) all executed this
time. The body-level proof for the sixteen tables is OWED by worker 8. |
| 8 | The suite proven to DETECT - break the implementation on purpose | **PARTIAL** | Closed ONLY for the privilege proof (worker 1). Worker 4's write-path red IS a genuine product mutation and closes that half when its run lands. **The concurrency lost-update red is NOT closed:** review 3 established that the committed red uses a FAKE WRITER inside the test rather than a mutation of the PRODUCT code, and those are two different claims - a fake writer proves the assertion can fail, not that the shipped upserts are what makes it pass. **I recorded that worker 5 had the product mutation BUILT. It had not said that, and it corrected me - the statement came from review 3, and I attributed it to the worker's own work.** Worker 5's own account: the fake-writer red was watched; the product mutation is now written and ready to apply, unrun. **AND IT ESTABLISHED WHY THE OBVIOUS MUTATION PROVES NOTHING HERE:** a read-then-save mutation of its product code would PASS its DETERMINISTIC race, and that is not a weak mutation, it is where the window actually is - the store decides whether to write from its in-memory SHADOW rather than from a database read, so a read-then-save re-reads at write time, sees the other container's value, and correctly declines. The lost update for a read-then-save lives between ITS read and ITS write, and only genuine concurrency opens that window. So the product mutation must be judged by the THREADED four-container test; the deterministic test keeps its separate job of proving the assertion can fail at all. Two tests, two different claims - and running the mutation against the wrong one would have produced a green that read as vindication. **Worker 3's read-parity detector IS closed and is PERMANENT** (`232f34782`), not a red anyone must remember to run: it renders both readers, asserts they AGREE first so a later failure means something, then reduces one number the FROZEN pre-port reader returns by exactly what a lost turn would cost and requires the comparison to reject the pair AND name the field that moved - so if the comparison ever goes blind, that test fails rather than the parity test quietly agreeing. Owed by worker 5; also owed for the sixteen-table suite by worker 8 and the read parity comparison by worker 3. |
| 9 | The no-SQLite guard proven by making it trip | **CLOSED for the TRIP PROOF; head re-run owed by WORKER 7** | A Mono.Cecil METADATA scan over the compiled `CcDirector.Gateway.dll` and `CcDirector.Gateway.Host.dll` with ALLOWLIST polarity (four types named, each with a written reason it cannot reach hosted), plus `HostedSqliteGuard` as a runtime refusal. **Static rather than startup-or-composed-host deliberately: both of those are satisfied by NOTHING HAVING HAPPENED, which is the false green this row exists to prevent**, and `Microsoft.Data.Sqlite` exposes no process-wide hook, so a runtime-only guard binds only the call sites that remember to call it. Watched tripping FOUR times: allowlist emptied (the scan detects at all); a new SQLite-backed store that nothing constructs and no test calls (the case a runtime guard passes GREEN); the same store behind a namespace alias that a grep for `new SqliteConnection` misses; and both failure directions of the anti-rot rule. Fixture is the COMPILED PRODUCTION ASSEMBLY read as bytes, sharing no model with what it scans, so it cannot agree by construction. Evidence: `docs/step2-nosqlite-guard-proof.md`. **Owed: a narrow re-run confirming the four rules still pass AT BRANCH HEAD** - three earlier runs produced NO RESULT (two full-suite, one killed by the harness and one on my hold-off; one 17-class run timed out at 10 minutes). The trip proof itself is not rebase-sensitive and is in hand. |
| 10 | The Gateway starts and serves a roster with the statistics database unreachable | **CLOSED for STARTS-AND-ANSWERS; the SERVES-A-NON-EMPTY-ROSTER arm is OPEN and UNOWNED** | 15 tests across three classes, Passed 15 Failed 0, run TWICE. **The failing direction was produced by breaking the PRODUCT, not the test**: one throw at the end of the boundary catch in `GatewayStatsStore`, which is precisely "the statistics migration is fatal to startup", with nothing in the test project touched. Both rows then failed with `Npgsql.NpgsqlException : Failed to connect to 127.0.0.1:1` in a stack reading `GatewayStatsStore..ctor` line 216, `FromEnvironment` line 143, **`GatewayHost..ctor` line 825** - the host constructor is in the stack, so the Gateway did not start. Reverted, rebuilt, re-ran, fifteen green again, so the greens belong to the clean tree and the red belongs to the mutation. **It committed BEFORE injecting the fault**, so undoing it could not take real work with it. **What did not execute:** every assertion in both tests, because the throw lands on the first statement of each. For this particular red that is the point rather than a gap - the claim under test is that the Gateway starts AT ALL - but "2 failed" must not be read as two claims tested and disagreeing.

Actual output: `STATISTICS: available=False reason=unreachable source=ExplicitOverride`, then
`GET /sessions -> 200 OK` and `BODY: []`. The reason is `unreachable` and not `not_configured`, so the
store TRIED and failed rather than skipping statistics - the two named reasons are distinguishable in
practice and not only in the enum.

**THE LIMITATION, VOLUNTEERED BY THE WORKER, AND IT IS THE INCIDENT'S OWN DEFECT.** That roster body is
an EMPTY ARRAY. What is earned is that the route ANSWERED IN ROSTER SHAPE with statistics dead - **not**
that any session was enumerated. Issue 8 of the incident is precisely this: the post-swap health check
returned 200 with zero rows because the tunnels had not reconnected, and any check looking for HTTP 200
would have declared success over an empty fleet. **Absent reads identical to empty.** Closing this row
on an empty body would rebuild the same false green inside the proof that the outage was about.
Enumerating sessions needs a pushed snapshot over a tunnel, which is a different rig. **OWED and
UNOWNED.** |
| 11 | The SQLite baseline is structurally equivalent to a real version 5 file, and the comparison is proven to detect | **CLOSED for the BASELINE, independently confirmed. The MODEL is a separate artefact and it still encodes the four rejected divergences - see row 20.** | Review 4 built one database by constructing `GatewayStatsDatabase` and another by running the migration, and compared `sqlite_master` unnormalised: only Entity Framework bookkeeping differed. It also read the normaliser and confirmed it changes only whitespace and does NOT hide quoting or casing. Detector proven by renaming `ix_stat_delta_hour`. **My claim that this diff "guards the model against the DDL" was WRONG and the reviewer said so - the test never compares model metadata to anything.** | **This is one of the two rows the authorised desktop release will be cut on. Do not read it as closed because the literal-DDL decision was made: that decision made equivalence ACHIEVABLE; only the diff makes it DEMONSTRATED.** Baseline being rewritten as the literal version 5 DDL so equivalence is true by construction; structural `sqlite_master` diff against a file built by RUNNING the existing code, table by table including index names, primary key column order, uniqueness, nullability and column order; detector proven by renaming an index on purpose and watching the failure name it. An independent reviewer already found four divergences here by probing - dropped `tenant` defaults on eight tables, rowid key nullability metadata, named primary key constraints, and `user_version` left at 0. |
| 12 | Self-host adoption of an existing version 5 store, non-fatal, with the fixture built by running the real old code | **STILL OPEN after two review rounds. The FIX ROUND INTRODUCED TWO NEW DEFECTS, one of which reintroduces this mission's own failure mode.** | Worker 2.

**ROUND 3: the replacement bound is not a bound.** `PRAGMA busy_timeout` does NOT govern
`BeginTransaction` - the provider executes `BEGIN IMMEDIATE` through an internal command that retries
according to the CONNECTION's default timeout (30 seconds), and the native busy handler and the managed
retry loop COMPOUND rather than one replacing the other. Measured: **35.065 seconds** to return the
named busy result; a seven-second configured timeout took 12.126. **The startup boundary gives up at 20
seconds and reports UNREACHABLE** - so the exact operator-misdirection this work set out to remove is
still there, a local writer lock surfaced as a database-or-network problem, because the inner bound is
LONGER than the outer deadline. **The bound was believed rather than measured.** The pragma also leaks:
still 5,000 on the connection after adoption and after `Migrate()`, never restored.

**ROUND 2, now superseded:** the adoption path took Entity
Framework's SQLite MIGRATION LOCK, and that lock is **UNBOUNDED**. Review 5 built a genuine version-5
store carrying `__EFMigrationsLock` with a persisted lock row, called `Adopt`, watched it fail to
complete within 2.5 seconds, then read the provider source and confirmed the synchronous acquisition
**retries forever with no timeout and no cancellation path**. That is inside a path whose entire
contract is containment. **A statistics store that HANGS startup is precisely what this mission exists
to remove**, arriving through the fix for the check-then-create race.

**NEW, and it breaks existing installs:** both baseline migration IDs were changed. Review 5 created the
exact history state written by the previous revision over a genuine, healthy version-5 store, and
adoption returned `NotAdoptable/NotAStatisticsStore`. A healthy store tracked by the earlier build is
now REJECTED, and the PostgreSQL baseline appears pending.

**Finding 3 remains PARTIAL:** `stat_delta` recreated with the exact expected column NAMES but no
primary key, no `NOT NULL`, no tenant default and no indexes was Adopted and stamped.

Confirmed FIXED by the same review: the target-model divergences (and the new rebuild test genuinely
DETECTS - run against the restored old model it failed on the tenant default), the invalid tracked and
foreign store certifications, the check-then-create race, and the evidence table. All five refused
states returned the intended reason AND the database file SHA-256 was identical before and after each
call - refused AND unmodified, both asserted. Evidence packaged for the authorised desktop release at `docs/evidence/step2-self-host-adoption.md`, for a release seat that will not have us to ask. |
| 13 | A migration added without moving `PRAGMA user_version` fails a test that NAMES the omission | **STILL OPEN - review 5 confirmed NOT FIXED by running it** | Enforcement must be MECHANICAL, not a comment and not a remembered rule. The expected stamp is DERIVED FROM THE CHAIN - four plus the number of migrations, where the 4 encodes that the baseline collapses versions 1 through 5 into one migration and must be commented as such or someone will "correct" it as an off-by-one. A constant is the same forgettable rule wearing a different hat. **Review 5 defeated it again, PROVED BY RUNNING:** it added two deliberately malformed migrations - one
that does not move the stamp, one that jumps straight to the final arithmetic value - both with empty
`Down()` methods, and the test STILL PASSED. The final-sum check remains defeatable exactly as
originally reported, and no `Down()` is exercised at all.

**RED, actual string:** `The SQLite statistics chain has 2 migration(s), so a freshly migrated store
should stamp PRAGMA user_version = 6, but it stamps 5. A MIGRATION WAS ADDED TO THE CHAIN WITHOUT MOVING
THE VERSION STAMP. Every migration that changes this schema must raise the stamp by one, in its own
Up(), with a matching reset in its Down(). The stamp is what an OLDER build of DevThrottle reads to
decide whether it understands a statistics file: leave it behind and a user who rolls their desktop
build back gets that build running its own version 1 to 5 steps against tables that already exist,
instead of the clean refusal it would otherwise give. (The expected value is 4 plus the migration count,
because the baseline migration collapses schema versions 1 to 5 into one migration.)` - Failed 1,
Passed 1. **No assertion went unexecuted** - that test has one assertion and it fired, and the other
test in the class passed in the same run. **GREEN** after deleting the throwaway: `Passed! - Failed: 0,
Passed: 17, Skipped: 0, Total: 17` across the baseline-equivalence, version-stamp and adoption classes,
including the new interrupted-migration test.

The message is the artefact. It does not report a failed assertion - it names the omission and then
explains the CONSEQUENCE in plain words, so somebody hitting it in eighteen months needs no archaeology,
and it explains the offset inside itself so the 4 cannot be mistaken for an off-by-one. |
| 16 | A fresh statistics file stamps `PRAGMA user_version = 5`, so an OLDER build meeting a NEWER file refuses loudly instead of crashing | **STAMP half CLOSED; the REFUSAL half is BROKEN, not merely unproven - PROVED BY RUNNING** - the stamp is the mechanism, the refusal is the property, and a live desktop downgrade has never been exercised end to end.

**Review 9 proved the refusal does not happen for TRACKED stores.** The version gate runs ONLY on stores
with no history table. Once history exists, the inspection asks only whether the current baseline id
appears somewhere in the applied list and whether the model's required shape is present - it **never
reads `user_version`**, never rejects applied migration ids unknown to this build, and tolerates extra
objects. A store carrying the baseline plus a future migration, `user_version=6`, an extra table, an
extra column and a future unique index came back **AlreadyTracked**; `Migrate()` was a no-op because
this older chain had nothing pending; and a write valid for the current model then failed with SQLite
error 19 **OUTSIDE the containment**.

So the downgrade safety that the per-migration stamp test exists to PRESERVE is not present for the case
that matters - a machine that ran a NEWER build and rolled back, which is precisely the desktop release
scenario. It also contradicts the reason type's own promise that a newer file is refused. | **The second of the two rows the authorised desktop release will be cut on.** Found by an independent reviewer, and named by nobody before that: a file left at `user_version = 0` is not safely openable by the OLD build - it reads 0, tries to run migrations 1 through 5 against tables that already exist, and dies on a duplicate `ALTER TABLE`. A user rolling back a desktop build is a thing that will actually happen, so this is our regression arriving on their machine by a route none of us was looking at. Stamping 5 turns that crash into the loud refusal the original author already designed for, since the old code ALREADY fails correctly on a file whose version exceeds its build. Owed: the stamp, and a test that a downgrade gets the refusal rather than the crash. |
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
| 19 | The WHOLE SOLUTION builds and its whole test suite passes, for a COMMIT on this mission | **A GREEN ARRIVED AND IT PROVED NOTHING ABOUT THE PORT. The row stays OPEN.** | No whole-suite answer has existed for this mission at any point. Local full-suite runs were banned (they take a fleet-wide lock and starve every other run on the machine) and CI did not fire on these branches, because `ci.yml` triggers only on push to `main` and pull requests based on `main` - so for about two hours there was no whole-suite signal of any kind, and nobody noticed, because the absence of a signal looks exactly like a signal that has not spoken yet. Draft pull request **2319** now makes the trigger fire on every push to `nosqlite-stats`; a draft lands nothing and cannot be merged. Runs are in flight on `2feb674e`, `a3872bfd` and `54633ac6`. **THE FALSE GREEN, kept on the record rather than deleted, because a row that records one is worth more
than a row that never existed.** Run 30567613382 on commit `2feb674e7` returned SUCCESS - a real verdict,
not a cancellation. It proved nothing about this work. **The mission branch contained ONLY
DOCUMENTATION**: measured, `git diff origin/main...origin/nosqlite-stats -- src/` was EMPTY, and the
whole branch was three files and 765 insertions of plan, contract and this ledger. **Not one of the six
worker branches was merged in.** So the whole solution built and passed with NONE of the Step 2 work
present - a green for `origin/main` plus my notes.

Both halves of how that happened belong here. The Architect opened the draft pull request specifically
to give this mission whole-suite coverage, announced that CI now covered the branch, and never checked
what was ON the branch - the same defect he had corrected himself for two hours earlier, asserting an
authority exists without verifying it reaches the thing it is aimed at, committed again inside the fix
for the first one. And I reported CI runs in flight for an hour as though the answer coming would be
about our work, without checking either; I measured it before recording the row, which is the only
reason it did not close.

**A cancelled run is ALSO not a result** - it reports `completed` with conclusion `cancelled`, so
anything matching on status alone reads it as an answer. Only `success`, `failure` or `timed_out` mean
anything. The Architect's own CI watcher had that defect and would have reported CI DONE on the first
superseded run.

**The rule this row now carries: a green on the mission branch means what the branch CONTAINED at that
moment. Every row that closes on a CI result must record WHICH worker branches were present in that
run.** Branches merge in as each clears independent review, in dependency order, so coverage grows
incrementally and a regression surfaces against the commit that caused it.

**A run IN PROGRESS closes nothing** - the .NET job takes 25 minutes or more, so expect a lag behind each push. The green, when it comes, belongs to a COMMIT and not to the branch or to anyone's memory of a run. Worker 2 separately confirmed lock-free that the whole solution BUILDS with zero warnings at its head, which is a build and explicitly not a test run. |
| 20 | The Entity Framework MODEL describes the literal version 5 DDL, so a later table-rebuild migration cannot silently undo it | **OPEN - owed by WORKER 2. This is the Architect's own prediction, arriving.** | Review 4, PROVED BY RUNNING against live model metadata. The raw `Up()` now emits correct version 5 DDL, but the runtime model, the designer's target model and the snapshot were never changed to describe it: all eight `tenant` columns have NO configured model default against a database default of `'local'`; all eight rowid primary keys are non-nullable CLR properties where the real file reports `PRAGMA table_xinfo.notnull = 0`; and the model still uses conventional primary key constraint names. **These are the SAME FOUR DIVERGENCES the first review rejected** - replacing only `Up()` hid them from the baseline output without correcting the chain's target model. The bite lands on the first later SQLite migration needing a table rebuild: scaffolding is diffed from the snapshot, so it can strip `DEFAULT 'local'`, restore named key constraints and restore the rowid difference - **and both equivalence tests stay GREEN, because they rebuild only the baseline and never
baseline-plus-a-later-model-driven-migration.**

**Architect ruling: fixing the model is NOT enough on its own.** The reason this hid is STRUCTURAL -
nothing ever exercises baseline-plus-a-later-model-driven-migration, which is precisely where
scaffolding diffs against the snapshot and the drift bites. Fix the model and leave that blind spot, and
the same class of defect is free to return the next time anyone edits the model. **Add the missing
case:** a trivial second migration, applied on top of the baseline, asserting no spurious table rebuild
and no unexpected difference - **watched failing first against the current drifted model.** If it does
not go red today, it is not testing what it claims. |
| 21 | The version-stamp rule is enforced PER MIGRATION and on `Down()`, not by one final sum | **OPEN - owed by WORKER 2** | Review 4. The test applies the whole chain once and asserts only `final user_version == 4 + migration count`, so one migration can omit its bump while another over-bumps and it stays green; and it never migrates DOWN, despite its failure text promising "a matching reset in its Down()". **It also surfaced a contradiction that needs deciding, not papering over:** `AdoptableSchemaVersion` reads the same `SchemaVersion` constant, so raising that constant to satisfy a future second migration would stop recognising the real version-5 no-history files this adoption exists to protect. **The baseline's SOURCE version must stay frozen while the chain's CURRENT version advances; the tests
treat them as one value.**

**Architect ruling: TWO CONSTANTS, not one.** They are different concepts sharing a number today, and
that coincidence ends with the second migration. One is FROZEN FOREVER and describes a historical
artefact - the shape of files the OLD code wrote, which exist on users' disks and can never be
retroactively changed - named so it says so (`LegacyBaselineSchemaVersion` or similar), pinned at 5,
commented with WHY it is frozen: raising it stops recognising the very files adoption exists to protect.
The other is CURRENT and advances with the chain. **The fix must make the two IMPOSSIBLE TO CONFUSE, not
merely correct today**, and the tests treating them as one value must be split. Conflating them means
every future migration silently breaks adoption for every existing self-host install - the exact failure
this adoption path was built to prevent. |
| 17 | Every no-SQLite allowlist entry carries a MACHINE-CHECKABLE EXPIRY, and the stale `GatewayStatsDatabase` exemption fails the guard the moment the port lands | **WRITTEN, NEVER EXECUTED - OPEN and UNOWNED** | Promoted from a note to a row on the Architect's ruling, because a note is a rule keyed to somebody REMEMBERING and it guards the exact hole this mission exists to close. The guard must FAIL when `GatewayStatsDatabase` is in the allowlist AND `GatewayStatsDbContext` exists in the scanned assembly - that context existing is a machine-checkable signal that the port HAS HAPPENED and the exemption has expired. The red then arrives when the justification stops being true, not when somebody next reads a document. Generalised: **every entry carries an expiry CONDITION whose becoming true fails the guard, and entries justified only in prose are refused** - an exemption that cannot expire is a permanent hole wearing a temporary label. An entry that genuinely cannot be given a checkable expiry is itself a finding and must be reported, not invented around. Proven the same way as the other four trips: add a stub `GatewayStatsDbContext` to the scanned assembly, watch it go red NAMING the stale exemption, remove it, watch it go green. |
| 14 | The concurrency store on Postgres | **OPEN - owed by WORKER 5** | Corrected by review 3. The SQLite arm is NOT fully closed: the lost-update red is a fake-writer red rather than a product mutation (row 8), and the parity arm is reopened by the out-of-order divergence (row 7). The Postgres arms are written, gated and NOT YET RUN. Review 3's own baseline run TIMED OUT waiting for the fleet lock and PRODUCED NO RESULT - recorded here as no result, which closes nothing. |
| 15 | `gateway-concurrency-stats.json` no longer written on the hosted path | **CLOSED for the claim** (same run as row 10: `HOSTED root contents:` empty against `SELF-HOST root contents:
gateway-concurrency-stats.json`, one variable different, so the absence is a REFUSAL rather than a test
that failed to look). **The INDEPENDENT verifier remains unowned** | **I wrote that this would be 'verified by the no-SQLite guard's sibling check'. Worker 7 has told me NO SUCH CHECK EXISTS and never did - its scan matches SQLite TYPES and says nothing whatever about a JSON file being written. I invented a verifier. Recorded here rather than quietly reassigned, because a ledger row naming a verifier that does not exist is worse than a row naming none: it reads as covered.** Worker 6 owns the wiring and the claim. The independent check - that nothing writes that file on the hosted path - is assigned to worker 8's contract suite. |

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
- Worker 4: **half and half, and it corrected me on this itself, twice.** The SQLite facts are built by
  RUNNING the existing `GatewayStatsDatabase`, so a wrong `ToTable` or `HasColumnName` throws there. The
  **Postgres** facts create the `gateway_stats` schema and its tables from the MODEL via
  `ctx.Database.GenerateCreateScript()`, because no statistics migration existed on that branch - so
  **rows 3 and 4 are CONCURRENCY proofs and NOT schema proofs, and must be rebuilt on worker 2's
  migration.**

  Its race also guards against its own non-occurrence, which is the fixture-shape rule applied to
  concurrency: if PostgreSQL never reports another session with `wait_event_type='Lock'` in the
  database, the fact **THROWS** `No other session ever blocked on a lock, so the two writers never
  actually interleaved` rather than reporting a green. A concurrency test that passes when the race did
  not happen is the commonest false green there is, and this one cannot.

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

## A green can go stale, and one nearly did

Worker 3 held an 80-test green on the pre-existing statistics tests. It noticed that green **predated
both its own meta-read port and its rebase onto worker 2's branch**, so quoting it would have been a
stale measurement presented as a current fact - the same defect as a constant standing in for something
that has since moved. It re-ran instead: `Passed! - Failed: 0, Passed: 96, Skipped: 0, Total: 96` across
`GatewayInputStatsAggregatorTests`, `GatewayStatsDatabaseTests`, `StatsPageEndpointTests` and worker 2's
three SQLite classes, **all green together on the rebased tree**.

A green belongs to the tree it was run against. Nobody asked it to check.

## A WINDOW ON THE OBSERVATION IS NOT A WINDOW ON THE CONSEQUENCE

The cross-incarnation error was offered in good faith as "bounded to one poll interval". It is
PERMANENT. One poll interval describes how long both readings are AVAILABLE - it says nothing about how
long the resulting error LASTS, and the two were conflated.

The reasoning error is worth more than the instance, because it is reusable: when bounding a defect, ask
what the bound is a bound ON. A short window in which a mistake can be MADE does not make the mistake
short-lived, and a durable ledger turns a momentary collision into a permanent number. Here one
collision left 210 against an honest 115 - overcounted by 95, forever, with the magnitude scaling with
the pre-reset watermark.

**Ruling: this is not an acceptable residual.** Statistics wrong forever for anyone whose Director
restarts after a counter reset is an ORDINARY event, not an edge. Fix it, or the row stays open and it
is declared in plain words on the release.

## A WITNESS CAN BE STRENGTHENED IN EVERY DIMENSION AND STILL WITNESS THE WRONG EVENT

The race witness was tightened three times - naming the blocked backend, requiring not-granted,
restricting the lock type, requiring the blocker in the blocking set - and each tightening was correct.
It still certified a race that never touched the row it claimed to exercise, because identity upserts
run BEFORE any watermark raise, so the second writer blocks on an IDENTITY row and satisfies every
clause HONESTLY. With its watermark work removed entirely, the fact still passed.

**The fix is to change the KIND, not the strength: CAUSE the interleave rather than OBSERVE it.** Force
the second writer to read, hold it, let the first commit, then release it - the race is then guaranteed
by construction and needs no witness at all, and it CANNOT pass when the race did not happen. Any
observational check that survives must bind to the WATERMARK RELATION AND KEY, because binding to the
backend and the lock type is exactly what an identity block satisfied.

## A RETRY HIDES A CYCLE; A CANONICAL ORDER REMOVES IT

Deadlock needs a cycle; a cycle needs two writers taking the same locks in opposite order. Sorting by a
stable key before locking removes the cycle. **A retry is not the fix** - it makes the deadlock less
visible while leaving the cycle intact, so the first thing it costs is the ability to notice. Any retry
kept for what ordering cannot cover is BOUNDED and COUNTED, with the count on the health surface.

## A CONFIGURATION SETTING IS A REQUEST, NOT A GUARANTEE

Third costume of the same law in one day. You cannot read a BOUND off a setting any more than you can
read LIVENESS off a lock file - both must be EXERCISED.

`PRAGMA busy_timeout = 5000` was set and believed. The provider executes the contested statement through
an internal command that retries on the CONNECTION's default timeout instead, and the two mechanisms
compound rather than one replacing the other: measured **35.065 seconds** for a nominal five-second
bound. Nobody had run it.

**And the invariant that would have caught all three rounds of this:** THE INNER BOUND MUST BE STRICTLY
LESS THAN THE OUTER DEADLINE. An adoption that takes 35 seconds inside a containment that gives up at 20
means the containment cannot do its job however correct either piece looks alone - and **nothing in
either file said those two numbers were related**. Assert the RELATIONSHIP, not the values, so that a
tightened deadline or a changed provider default is caught by a test rather than by a user.

## A GATE THAT RUNS ONLY ON THE FRESH CASE NEVER RUNS WHERE IT MATTERS

The version gate ran only on stores with NO history table - which is the case that cannot occur on any
machine that has ever started this software. The check existed, looked present, and never fired where it
was needed.

For a tracked store the check is small and precise: **every APPLIED MIGRATION ID must be one this build
knows.** An id this build does not have means the store was written by a NEWER build, which is the
desktop downgrade exactly. The applied list is the authority once a store is tracked; `user_version` is
not needed on that branch. The code must say WHY the gate runs on both branches, because the next reader
will see the untracked path checking and assume the tracked one is redundant.

## A STATUS FILE ANSWERS WHAT WAS; ONLY THE OPERATION ANSWERS WHAT IS

The general form of the two mistakes below, and of the classifier being right by the rule and wrong
about the world. An artefact that DESCRIBES a state is not the state, and the convincing ones are
convincing because **every field in them is true** - they are accurate records of a moment that has
passed.

Two instances this evening, one on each of us. I read the fleet lock file as live state for an
afternoon. The Architect read timestamps off CANCELLED CI runs and told Step 1 that main finishes in
eight to twelve minutes when it actually takes an hour, and has had to retract it.

## A DERIVED EXPECTED SET IS A VACUOUS TEST - assert a PROPERTY instead

I filed issue 1151 proposing that a stale hardcoded expected list be DERIVED instead. **That was wrong
and would have been worse than the stale list.** Deriving the expected set from the same source as the
actual set means the test compares the database to itself and passes whatever the collation is - a guard
supplying its own evidence. **A stale list fails loudly; a vacuous test never speaks again.**

The fix is an asserted PROPERTY - every text column in a natural key has collation C - expressed as a
rule the schema must satisfy rather than an inventory it must match. A new column then satisfies it
silently or fails it loudly, with no list for anyone to maintain. Genuine exceptions get an allowlist
entry with a MACHINE-CHECKABLE EXPIRY, per row 17.

## THE LOCK FILE IS NOT THE LOCK

The fleet test lock is an OPEN FILE HANDLE. The file beside it carries the holder's process id, worktree
and acquisition time - and it PERSISTS AFTER THE HOLDER EXITS, because the operating system releases the
handle without rewriting the file.

So reading that file tells you WHO LAST HELD the lock, not whether it is held NOW. I read it as live
state for most of an afternoon, scheduled work around it, chased a worker over it, and twice reported a
holder that had already exited.

**The only reliable read is to attempt the acquisition.** An artefact that describes a state is not the
state, and this one is convincing precisely because every field in it is true - it is a TRUE RECORD of a
moment that has passed.

## A REVIEWER'S AD-HOC PROBE IS NOT A TEST

Review 5 proved by running that adoption left five refused stores unmodified. Nothing in the SUITE
protected that property - the proof existed only in the reviewer's transcript, and the reviewer has
since been reaped. **A proof that lives in somebody's memory is a proof that ends when they do.**

Every refusal now fingerprints the WHOLE store before and after - the database file plus any
write-ahead log and shared-memory file beside it - and compares.

**A hash rather than a count, deliberately.** Counting tables and rows only ever proves what somebody
thought to count: a refusal that rewrote a value, moved the version stamp or dropped an index nobody
enumerated would pass every count and still have changed the operator's file. **A hash has no checklist
to be incomplete.**

Five states had no committed test at all and now do: a tracked store with a table dropped, the
names-only table, a VIEW wearing a table name, a foreign database carrying its OWN migration history,
and a store with an abandoned migration lock. The foreign-database fixture now actually HAS the foreign
history row it was always described as having - without it, as the reviewer pointed out, it was not
reproducing the original defect - and it asserts the foreign table, its row and its version stamp all
SURVIVE, rather than only that ours are absent.

**Still uncovered and stated rather than papered over:** the two-adopter RACE itself. The re-check now
happens under the database's own write lock so the window is closed by construction, but a deterministic
test needs real concurrency, and an assertion that passes without exercising the race is worse than an
admitted gap.

## Our own remedy keeps carrying our own defect

**A fix for the check-then-create race introduced a startup hang inside the containment built to prevent
startup hangs.**

That is the third time today our own remedy has carried our own defect. The reason-code map made a
missing enum member report as an unreachable database, through the mechanism built to guarantee named
reasons. The CI watcher read a cancelled run as an answer, in the fix for CI not running at all. And now
an unbounded provider lock sits inside the boundary whose entire contract is that statistics can never
stop the Gateway.

**It is the strongest argument that exists for why every FIX ROUND gets an independent review rather
than a re-run.** A re-run asks whether the reported defect is gone. It cannot ask what the fix brought
with it, because nobody wrote a test for a thing that did not exist yet.

Two rulings attached to it. The unbounded retry is a **PROVIDER CONSTRAINT WE DESIGN AROUND, not a
defect we can fix** - we do not control that acquisition and no amount of care in our code makes it
bounded, so the honest record is that we work around it. And it makes issue 1134 strictly worse: an
abandoned migration that holds a migration lock forever is a worse abandoned thing than one that does
not.

## A MIGRATION ID IS PART OF THE ON-DISK CONTRACT

The schema-version constant law in a different costume. Every store that already exists records the
migration IDs applied to it, so **renaming one is a breaking change to every install that ran the
earlier build** - proved by recreating the previous revision's history state over a genuine healthy
store and watching adoption reject it as not a statistics store.

The original IDs are restored, and the migrations directory says these identifiers may never be renamed
and why. Without that note a future reader tidies them, **every test passes on fresh databases**, and the
breakage lands only on machines nobody is testing on.

## NEVER LEARN WHAT YOU CHANGED FROM YOUR OWN PRIOR BELIEF

Learn it from the response of whatever ARBITRATES. Architect ruling, and it is the root cause of four
separate defects that looked like four problems.

Each writer computed its growth against its OWN PRIOR BELIEF - a private in-memory mirror - and then
appended that growth to a SHARED ledger, while the shared watermark was arbitrated by the DATABASE.
**Those two things cannot both be authoritative.** Every one of the four write-path defects was that
contradiction surfacing somewhere different: doubled delta growth, two identities minted for one
display, the same rows archived twice, and a counter reset recounted after a restart.

The fix is not four guards. Raise the watermark and RETURN the old and new values in the SAME ATOMIC
STATEMENT, then append exactly that difference. Growth becomes what THIS writer added to the shared
value rather than what it believed it was adding, **the sum of appended deltas equals final minus
initial BY CONSTRUCTION, and the stale-baseline case DISAPPEARS rather than being detected** - which is
the difference between a fix and a guard. Retention archives exactly the rows its own statement claimed;
identity minting reads back WHICH ID WON rather than assuming it minted one.

## THE AUDIT QUESTION FOR EVERY CLOSED ROW

**Does it assert the quantity that would BREAK, or a neighbouring one that would not?**

The write-path suite asserted WATERMARKS, which were always going to be right, and never DELTA TOTALS,
which were always going to be wrong. A proof that does not assert the quantity that breaks is not a
weaker proof - **it is a proof of a different claim.**

Applied to every closed row, this surfaces one that had not been split:

**Row 16 asserts that a fresh file STAMPS version 5. It does not assert that an OLDER BUILD REFUSES.**
The stamp is the mechanism; the refusal is the property that protects a user rolling a desktop build
back. Worker 2 has already said a live desktop downgrade was never exercised end to end. So the stamp
half is closed and **the refusal half is OPEN and UNOWNED** - and it is on the desktop release path.

The other closed rows survive the question because their neighbouring quantities were already split out:
row 1's rig-fidelity precondition, row 10's non-empty roster, row 11's model-versus-baseline, row 15's
independent verifier, and row 9's named non-coverage.

## A containment that catches EVERYTHING labels our bugs as their infrastructure

The class, not the incident. **A boundary that catches everything cannot distinguish "the store is
unreachable" from "we have a bug"**, so every programming error inside the containment gets a plausible
INFRASTRUCTURE label and sends the reader somewhere the fault is not.

Three instances in one day: a `StatsPageEndpoint` catch reporting a null reference as a storage fault;
the Architect's CI watcher reading a cancelled run as an answer; and a missing enum member reported as
an unreachable database.

**Ruling: the boundary must separate OUR fault from THEIRS.** Inside the containment, a failure that is
not a recognised storage or connection failure gets its OWN reason - an internal error, named as ours -
with the exception type and stack in the log, and it is NEVER reported as unreachable or not-configured.
A user sent to check their network for a bug in our switch statement is worse off than one told
"something in our code failed", because the second is at least TRUE and is actionable by them in the
only way that matters, which is telling us. Owed by worker 6, alongside the reason-code guard.

## Two names for one state is the distinct-reasons ruling stood on its head

Worker 2 and worker 6 independently reached the SAME state from opposite ends - a store whose tables
exist while its migration history records nothing applied, left by a first migration that died partway.
Worker 2 detects it inside adoption; worker 6 detected it in the startup boundary before the chain runs.

**Two codes for one condition fails the distinct-reasons rule exactly as badly as one code for two
conditions.** It is hard to see precisely because NEITHER STRING IS WRONG - an operator simply gets a
different one depending on which path noticed first.

Collapsed to one member that names the STATE (the store's schema is half-built on disk) rather than the
MECHANISM (the history records nothing). Naming a reason after its detection route ages badly the moment
a second route finds the same state - which is what happened here, between two seats, in one afternoon.
Detection stays in adoption because it sits earlier and knows more; the boundary check is kept as the
backstop for what adoption cannot see. Worker 2 owns the collapse because it is its file; worker 6 drops
its duplicate on its next rebase.

Worker 6 did NOT resolve this unilaterally, and that was right: deleting another seat's member during a
rebase is how one worker silently overwrites another's decision. It left both in with cross-referencing
notes and working codes, so nothing mis-reported while it waited.

## The defect that only existed BETWEEN two branches

Worker 6 rebased onto worker 2 and the rebase found something neither branch could have shown alone,
which is the strongest argument for merging as each branch clears review rather than at the end.

Worker 2 had independently added two reason codes, `StoreSchemaIncomplete` and
`MigrationHistoryIncomplete`. Worker 6's reason-code map was written before those existed and did not
know them. **Nothing failed to build** - a C# switch expression over an enum does not require
exhaustiveness - and nobody was told.

The failure mode is the part that matters. `CodeFor` throws on an unknown member, and on the path that
matters **that throw lands inside worker 6's own boundary catch, which reports it as UNREACHABLE**. So a
self-host user with a half-built store ON DISK would have been sent to look at their NETWORK - precisely
what the two distinct named reasons exist to prevent, arriving through the very mechanism meant to
guarantee them.

Both codes added, and the guarantee is now MECHANICAL rather than remembered: a test walks
`Enum.GetValues` and fails when a member has no code, a blank one, or one shared with another, and
requires every code to stay greppable `lower_snake_case`. It walks the ENUM rather than a list beside
it, because a list is the same forgettable rule wearing a different hat, and it collects every offender
so one run names them all.

Worker 6 also corrected a FALSE COMMENT of its own that claimed a missing member "fails to compile". It
does not. That comment was worth more than the mistake it was hiding, because a reader who believed it
would never have looked.

## Nothing on worker 6's branch has run against a REAL PostgreSQL server

Every PostgreSQL assertion on `nosqlite-stats-w6-startup` is about a connection that FAILS - the dead
endpoint at `127.0.0.1:1`. That is the right rig for proving containment and it proves nothing about the
store working against a live server. Volunteered by the worker rather than found.

## The allowlist entry that is knowingly FALSE today

`GatewayStatsDatabase` sits in the no-SQLite guard's allowlist with a written reason, and **that reason
is currently false - it really does open SQLite on hosted.** It is listed so the guard can land before
the port does. Nothing machine-checks that an allowlist reason is TRUE, which worker 7 stated plainly
rather than leaving to be discovered, and this entry is the proof that it can be false. **When workers 2
and 6 land and the hosted path stops opening that store, this entry must be REMOVED, or the guard
carries a permanent hole in the exact place the mission was about.**

## An accepted, measured divergence after a provider-driven table rebuild

A SQLite table rebuild emitted by the provider does three things the Entity Framework model cannot
express: it rewrites the rowid key to `NOT NULL` with a NAMED constraint, and it **REORDERS EVERY COLUMN
ALPHABETICALLY**. The third was named by nobody before worker 2 measured it - not either reviewer, not
me. So a post-rebuild file can NEVER be byte-identical to version 5, and "no unexpected difference" is
not achievable as written.

**Why it is inert, structurally rather than conveniently:** the version-5 shape claim only has to hold
for the BASELINE, because the baseline is what adoption STAMPS. A file that has been rebuilt already
carries a history table, so adoption can never run on it again. **Post-rebuild is post-adoption by
construction.** The equivalence that protects users is about the adoption DECISION.

**What is asserted instead:** after baseline-plus-a-rebuilding-migration, `tenant` still carries
`DEFAULT 'local'` - measured on a REAL adopted version 5 file, on exactly the path nothing previously
exercised - and all sixteen tables still have their expected columns, compared as an ORDER-INSENSITIVE
NAME SET.

**The condition this acceptance rested on is now MET, measured rather than reasoned:** a sweep proving nothing binds by
POSITION. Both directions. Reads - positional accessors, `SELECT *` followed by index-based access - and
**WRITES**, which are worse: an `INSERT ... VALUES (...)` with no column list binds by position, so after
a reorder it silently writes the right values into the WRONG COLUMNS. That corrupts rather than
misreports, and nothing throws. A bad read fails loudly on a missing column or a type mismatch; a bad
write just quietly puts the character count in the turns column. **The sweep came back CLEAN, both directions.** Every insert in the Gateway names its columns (checked
repo-wide, zero hits for the positional form); no tuple-form update; no `SELECT *` against these sixteen
tables, with the actual `SELECT *` hits NAMED elsewhere in the repository so the negative is checkable
rather than asserted; every read in the frozen pre-port aggregator uses an explicit column list, so its
ordinal accessors are select-list positions and not declaration order; and both `pragma_table_info`
readers in the old tests are order-insensitive. Unasked, the author also tightened its own adoption
column read to a table-valued pragma with a BOUND parameter read BY NAME, so that read depends on
neither the statement text nor SQLite's catalog layout - the defect cannot return through its own code.

The divergence must be recorded IN THE EQUIVALENCE TEST ITSELF, not only here - a future reader who
finds a loose assertion with no explanation will tighten it, discover it cannot pass, and either weaken
it further or spend a day rediscovering what was measured today.

## The check that can disappear without anyone noticing

**Worker 3's fixtures run the real `GatewayStatsDatabase` rather than the model, and they are currently
the ONLY place the entity mapping meets the real on-disk shape.** A wrong `ToTable` or `HasColumnName`
throws there at query time instead of passing quietly. Worker 2's baseline is now literal version 5 DDL
rather than model-generated, so the model no longer generates the schema and the two can drift.

**If a later change makes those fixtures model-built, that check vanishes SILENTLY.** Nothing goes red.
The suite stays green, because a model-built fixture and the model agree by construction. It would look
like a tidy-up. Worker 3 raised this on its way out, unasked, and asked that it be kept in front of the
Architect at merge.

## The merge-time obligation nobody may forget

**Worker 4's Postgres fixtures and worker 5's SQLite fixtures are both built from the MODEL, not from a
migration, because worker 2's migration did not exist when they were written. Both MUST be rebuilt on
that migration once it lands.** Until they are, those suites prove behaviour and prove nothing about the
schema - a fixture built from the same model the test exercises agrees by construction and passes just
as happily when both are wrong together. This is written here rather than left with the workers because
both of them will be reaped long before the merge, and the obligation must outlive them.
