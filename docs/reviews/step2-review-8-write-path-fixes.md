# Step 2 Review 8 — write-path fixes

Review scope: `95a72d483..97723b48d`, never `origin/main`. This is an adversarial re-review of the five findings in [`step2-review-7-write-path.md`](step2-review-7-write-path.md). All PostgreSQL probes used private rig `rev8c0d68` on port `55483`. Temporary mutations and probe facts were removed before the final clean run.

## Verdict

| Review 7 finding | Verdict | Evidence |
|---|---|---|
| 1. Concurrent writers double-count shared growth | **FIXED** for concurrent observations of one incarnation | **PROVED BY RUNNING** |
| 2. Concurrent identity mints create two rows for one exact display spelling | **FIXED** for the original exact-spelling race | **PROVED BY RUNNING** |
| 3. A Director reset is counted again after a Gateway restart | **FIXED** for an unambiguous reset | **PROVED BY RUNNING** |
| 4. Concurrent pruning archives the same rows twice | **FIXED** | **PROVED BY RUNNING** |
| 5. The race witness can certify the wrong lock | **NOT FIXED** | **PROVED BY RUNNING** |

Four of the five original findings are genuinely fixed. The range also introduces a production identity-mint deadlock and a new way for the race witness to false-green. A separate case-variant identity race remains, and the documented cross-incarnation residual is materially wider than “one poll interval” suggests.

## Original findings

### 1. FIXED — same-incarnation writers now append only database-arbitrated growth

**PROVED BY RUNNING.** The writer raises each watermark first, consumes the stored and pre-raise values returned by the same statement, and only then builds the append-only row ([`GatewayStatsWriter.cs:168`](../../src/CcDirector.Gateway/Stats/Data/GatewayStatsWriter.cs#L168), [`:333`](../../src/CcDirector.Gateway/Stats/Data/GatewayStatsWriter.cs#L333), [`:686`](../../src/CcDirector.Gateway/Stats/Data/GatewayStatsWriter.cs#L686)). The delta insert and raise remain in one transaction through commit ([`:127`](../../src/CcDirector.Gateway/Stats/Data/GatewayStatsWriter.cs#L127), [`:291`](../../src/CcDirector.Gateway/Stats/Data/GatewayStatsWriter.cs#L291)).

An independent four-writer probe seeded watermark/ledger 100, then released four writers on the same row with `(reported, believed)` values `(400,100)`, `(250,100)`, `(100,100)` and `(50,0)`. This includes three or more writers, a no-change observation, and a lower-but-provably-stale observation. The final watermark and ledger were both 400: appended growth was 300 and watermark movement was 300. The 100 and 50 observations contributed zero.

I also forced an exception after a successful raise but before the delta entity could be constructed: the identity resolver threw from the post-raise delta path. Disposal rolled the transaction back; watermark and ledger both remained 100. Thus a process/connection failure at that boundary cannot commit one half without the other.

The load-bearing mutation claim reproduced exactly. I changed only session growth to use `b.BelievedTurns`/`b.BelievedChars` instead of the returned `previous_*` values and ran all 12 PostgreSQL write-path facts. Result: **10 passed / 2 failed**, exactly the deterministic session-ledger fact and the many-writer session-ledger fact. In the many-writer fact the watermark assertion at 200 passed before the ledger assertion reported 1418; in the deterministic fact watermark movement was 5 while ledger growth was 10. Token, agent-driven and every watermark assertion stayed green.

### 2. FIXED — exact-spelling identity mints read back the winning id

**PROVED BY RUNNING.** Identity resolution now performs an upsert against the exact `(tenant, display)` unique index and returns the surviving id ([`GatewayStatsWriter.cs:392`](../../src/CcDirector.Gateway/Stats/Data/GatewayStatsWriter.cs#L392), [`:406`](../../src/CcDirector.Gateway/Stats/Data/GatewayStatsWriter.cs#L406), [`:727`](../../src/CcDirector.Gateway/Stats/Data/GatewayStatsWriter.cs#L727)). The shipped 12-writer exact-spelling race passed: PostgreSQL contained one row and every commit result reported the same id.

This fixes the exact race demonstrated by Review 7. It does not solve case-variant concurrent mints; that separate remaining gap is recorded below.

### 3. FIXED — an unambiguous reset survives a Gateway restart

**PROVED BY RUNNING.** The clean reset/restart fact passed: the store observed 10 turns, adopted a reset to 3, the aggregator was destroyed and recreated, and growth to 5 produced the honest total 15. The stored row and rebuilt mirror therefore agree after the reset.

The baseline-evidence mutation also reproduced the claimed six red facts. Removing `OR believed >= stored` caused three failures in the 19 write-path facts (the PostgreSQL reset, dropped-count, and reset-across-Gateway-restart facts) plus the three established aggregator reset facts for human input, agent-driven input and token spend. Restoring the clause returned them to green.

The reset rule is not correct for concurrent observations from different incarnations. That is acknowledged by the change, but its stated bound is too narrow; see “Cross-incarnation residual” below.

### 4. FIXED — concurrent pruners archive only rows their own delete removed

**PROVED BY RUNNING.** Pruning now materializes `DELETE ... RETURNING` and builds archive rows only from that result ([`GatewayStatsWriter.cs:452`](../../src/CcDirector.Gateway/Stats/Data/GatewayStatsWriter.cs#L452), [`:461`](../../src/CcDirector.Gateway/Stats/Data/GatewayStatsWriter.cs#L461), [`:491`](../../src/CcDirector.Gateway/Stats/Data/GatewayStatsWriter.cs#L491), [`:780`](../../src/CcDirector.Gateway/Stats/Data/GatewayStatsWriter.cs#L780)).

I seeded one expired token row worth 100 and ran two pruners, each adding a current row worth 1. Writer A held its transaction after deleting and archiving the expired row; writer B reached the same delete and was observed blocked by A. After A committed, B deleted no expired row. Final total was 102 and the archive contribution was exactly 100. The Review 7 double archive did not reproduce.

### 5. NOT FIXED — the witness still certifies an unrelated row lock

**PROVED BY RUNNING.** The query is stronger: it names writer B, requires `NOT granted`, restricts the lock type, and requires writer A in `pg_blocking_pids` ([`GatewayStatsWritePathPostgresTests.cs:758`](../../src/CcDirector.Gateway.Tests/Stats/GatewayStatsWritePathPostgresTests.cs#L758), [`:773`](../../src/CcDirector.Gateway.Tests/Stats/GatewayStatsWritePathPostgresTests.cs#L773)). It still does not connect that lock to the contested high-water row.

Every interleave batch queues the same identities ([`GatewayStatsWritePathPostgresTests.cs:160`](../../src/CcDirector.Gateway.Tests/Stats/GatewayStatsWritePathPostgresTests.cs#L160), [`:708`](../../src/CcDirector.Gateway.Tests/Stats/GatewayStatsWritePathPostgresTests.cs#L708), [`:717`](../../src/CcDirector.Gateway.Tests/Stats/GatewayStatsWritePathPostgresTests.cs#L717)), and identity upserts execute before any high-water raise ([`GatewayStatsWriter.cs:148`](../../src/CcDirector.Gateway/Stats/Data/GatewayStatsWriter.cs#L148), [`:168`](../../src/CcDirector.Gateway/Stats/Data/GatewayStatsWriter.cs#L168)). Writer B therefore blocks first on writer A's identity row and exposes a non-granted `transactionid` lock with exactly the two expected PIDs.

I changed the session fact's `racesIt` callback to a no-op, so writer B performed **no watermark operation at all**. The fact still passed. The named backend, `NOT granted`, lock-type and blocker clauses all held, but for the repository identity upsert. Writer A alone moved the watermark and ledger consistently, so the invariant also passed. The test can still certify a race that never reached the row it claims to exercise.

## New and remaining defects

### NEW — identity upserts can deadlock when batches mint in opposite order

**PROVED BY RUNNING.** `ResolveIdentities` takes row locks in the insertion order of `batch.NewIdentities` ([`GatewayStatsWriter.cs:406`](../../src/CcDirector.Gateway/Stats/Data/GatewayStatsWriter.cs#L406)); the aggregator preserves observation order when it builds that list ([`GatewayInputStatsAggregator.cs:612`](../../src/CcDirector.Gateway/Stats/GatewayInputStatsAggregator.cs#L612), [`:622`](../../src/CcDirector.Gateway/Stats/GatewayInputStatsAggregator.cs#L622)). There is no canonical sort and no retry.

I ran writer A with new repositories `owner/one`, then `owner/two`, while writer B used `owner/two`, then `owner/one`. An `AFTER INSERT` trigger slept for one second only to make the natural lock window deterministic after each writer held its first row. PostgreSQL detected the cycle and aborted one real `GatewayStatsWriter.Commit` with SQLSTATE **40P01 (`deadlock_detected`)**. The unique-index/upsert fix introduced these conflicting row locks; a dual-container fold with novel identities in different roster order can now fail one request.

### REMAINING — case-variant concurrent mints still split one case-insensitive identity

**PROVED BY RUNNING.** Two simultaneous private mirrors minted `Owner/Repo` and `owner/repo`. `StringComparer.OrdinalIgnoreCase` says these are the same identity, but the exact-spelling unique index does not conflict. PostgreSQL stored two rows and the commits returned two distinct ids. This is not the identical-spelling race from Review 7, but it still violates the documented case-insensitive identity invariant under the two-container topology.

### RESIDUAL — the cross-incarnation error is permanent and its magnitude is not poll-bounded

**PROVED BY RUNNING.** I seeded 100 banked turns, applied a new-incarnation reset observation of 5 against belief 100, then delivered a delayed old-incarnation observation of 110 carrying belief 100. The final watermark was 110 and the append-only ledger was **210**. Honest activity was `100 + 10 + 5 = 115`, so one collision permanently overcounted by 95.

The affected partition is one session, as stated. “One poll interval” describes at best how long both readings are normally available; it does not bound the durable error, whose magnitude scales with the pre-reset watermark, and an in-flight writer delayed on database work can land after more than one nominal polling interval. The bad delta remains for all time unless repaired. An incarnation stamp is still required for a complete solution.

## Schema and regression audit

**PROVED BY RUNNING / INFERRED BY READING.** The model maps every `previous_*` column and all four exact identity indexes ([`GatewayStatsDbContext.cs:202`](../../src/CcDirector.Gateway/Stats/Data/GatewayStatsDbContext.cs#L202), [`:255`](../../src/CcDirector.Gateway/Stats/Data/GatewayStatsDbContext.cs#L255), [`:269`](../../src/CcDirector.Gateway/Stats/Data/GatewayStatsDbContext.cs#L269), [`:283`](../../src/CcDirector.Gateway/Stats/Data/GatewayStatsDbContext.cs#L283)). SQLite schema version 6 adds them transactionally and preserves version-5 rows ([`GatewayStatsDatabase.cs:662`](../../src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs#L662), [`:673`](../../src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs#L673)). All **15/15** `GatewayStatsDatabaseTests` passed, including migration from a populated older store, and all **60/60** `GatewayInputStatsAggregatorTests` passed.

A repository-wide reference audit found no statistics reader treating `previous_*` as a metric; only the writer SQL and model/entities use them. The read path continues to consume the current counter columns.

There is no PostgreSQL statistics migration in this branch. The contract explicitly assigns that migration to Worker 2 and requires it to generate version 6 ([`step2-entity-contract.md:18`](../step2-entity-contract.md#L18)); these write-path proofs create the schema from `GenerateCreateScript`. That is not a defect in this Worker 4 range, but this commit cannot be deployed alone over a pre-version-6 PostgreSQL statistics schema.

## Final verification

- Clean source: **19/19** `GatewayStatsWritePath*` facts passed, including all 12 PostgreSQL facts.
- Database migration suite: **15/15** passed.
- Aggregator suite: **60/60** passed.
- All temporary mutations and probe facts were removed. The only pre-existing worktree item was the untracked Review 7 document; this review is the only new artifact.
