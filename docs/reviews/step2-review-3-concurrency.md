# Step 2 review 3: concurrency store

Review target: commit `21d5f1846` only. I compared that commit with the `GatewaySessionConcurrencyStats`
implementation and with **The concurrency store** in `docs/step2-entity-contract.md`. I did not compare with
`origin/main`.

Verdict: **not ready**. Four findings remain. The highest-risk one is that the committed deterministic
lost-update proof does not interleave the real store at the read boundary it claims to test. The required
product-mutation run remains open until the automatic Gateway-suite lock admits it; the earlier queued runs
timed out or were killed before acquiring that lock and therefore produced **no test result**.

## Findings

### 1. HIGH — the advertised lost-update proof does not race the product read path

**Location:** `src/CcDirector.Gateway.Tests/GatewaySessionConcurrencyLostUpdateTests.cs:49`, `:66`,
`:148`, `:158`, `:160`, `:185`

**Evidence:** PROVED by reading the interleaving; executable product-mutation arm still OPEN.

`ReadModifyWriteContainer.Prepare` opens and tracks both rows before either commit, so its sequential
`a.Commit(); b.Commit();` deterministically loses the eight to the seven. `UpsertContainer.Prepare`, however,
only stores a roster. Every database read and write made by the real store happens later, wholly inside
`Commit -> Observe`, so the same `a.Commit(); b.Commit();` sequence does not make a read-then-save mutation
read stale state: A finishes before B starts its read. The test therefore proves that the test-only fake is
wrong, not that the product test detects replacement of the product upsert.

The threaded hammer is not a substitute for the claimed deterministic proof. It asserts only `live_max` and
`max_live`; it does not assert `working_max`, its timestamp, `max_working`, or any of the three distinct-count
maxima. It also runs only on the model-built SQLite fixture.

I built an isolated product mutation that replaces the peak upsert in
`GatewaySessionConcurrencyStore.Observe` with an EF-tracked query plus `SaveChanges`. The baseline and mutation
test executions are still owed: the automatic per-user Gateway-suite lock was held by another live test host,
and each attempt ended before a test started. Those attempts are not pass or failure evidence and do not close
this finding.

### 2. HIGH — an out-of-order hour changes the rendered Snapshot relative to the original

**Location:** `src/CcDirector.Gateway/Stats/GatewaySessionConcurrencyStore.cs:332`, `:342`, `:350`;
`src/CcDirector.Gateway.Tests/GatewaySessionConcurrencyParityTests.cs:23`

**Evidence:** PROVED by an executable SQLite probe.

On every hour-key change the port rehydrates that hour's old members from
`concurrency_hour_member`. The original implementation simply clears the current sets when the key changes;
it does not rehydrate an earlier hour. This changes visible distinct counts when observations arrive H, H+1,
then H again.

Probe:

1. observe `session-a` in `2026-07-30T12`;
2. observe another roster in `2026-07-30T13`;
3. observe `session-b` in `2026-07-30T12`;
4. compare Snapshot for hour 12.

Result: original JSON store `sessions=1`; database port `sessions=2`. The parity test itself acknowledges at
lines 23-27 that this case diverges and excludes it. That exclusion contradicts the contract's unqualified
Snapshot parity requirement. This also means proof-ledger row 7 cannot call the concurrency parity arm closed,
and row 6 cannot describe the store's out-of-order boundary as closed.

### 3. HIGH — a failed observation poisons the in-memory member shadow, so retry commits counts without members

**Location:** `src/CcDirector.Gateway/Stats/GatewaySessionConcurrencyStore.cs:166`, `:174`, `:204`,
`:226`

**Evidence:** PROVED by an executable SQLite fault-injection probe.

`HashSet.Add` mutates the three current-hour sets before the transaction starts. If any statement then fails,
the database transaction rolls back but those set additions do not. The comment at lines 226-228 says the
shadow advances only after commit so the next observation retries the same write, but that is false for the
sets. On retry, `HashSet.Add` returns false, `newMembers` is empty, and the member rows are never retried.

I installed a SQLite trigger that aborts `concurrency_hour_member` inserts, observed one session, removed the
trigger, and repeated the identical observation through the same store instance. The first transaction failed
as intended. The retry left `distinct_sessions/machines/repos = 1/1/1` in `concurrency_hour` while
`concurrency_hour_member` contained **zero rows**. A restart at that point cannot resume any of the three sets
from the durable store.

### 4. MEDIUM — the SQLite fixture is generated from the EF model, so it cannot prove the migration/schema

**Location:** `src/CcDirector.Gateway.Tests/StatsConcurrencyTestDb.cs:17`, `:33`, `:34`

**Evidence:** PROVED by reading the fixture route.

The fixture calls `Database.EnsureCreated()`. The entity mapping creates the schema that the same mapping and
raw statements then exercise, so a migration that omits a table/column, chooses a different name or collation,
or otherwise drifts from the model is invisible here. This is a model/store behavior fixture, not a schema or
migration-chain proof. The file comments acknowledge that worker 2 owns the eventual migration, but the
current tests must not be reported as proving that migration until the fixture is rebuilt through it.

## Sharp-question audit (no finding where the implementation passed)

| Area | Result | Evidence |
|---|---|---|
| `concurrency_peak.live_max` | Pass | `GREATEST/MAX(excluded.live_max, existing.live_max)` at store line 481. |
| `live_max_at_utc` | Pass | CASE changes it only when excluded live max is greater, line 480. |
| `concurrency_peak.working_max` | Pass | `GREATEST/MAX` at line 483. |
| `working_max_at_utc` | Pass | Independent CASE against working max, line 482. |
| Five `concurrency_hour` maxima | Pass | All five use `GREATEST/MAX` independently at lines 490-494. |
| PostgreSQL execution | Pass for store SQL, not a migration proof | Private PostgreSQL 16 rig produced peak `8@12:02`, working `6@12:03`, hour maxima `8/6`; the lower competing write did not move either value or timestamp. The private container and volume were removed afterward. Schema was model-built for this probe. |
| Runtime current values | Pass | `LiveCurrent` and `WorkingCurrent` exist only in `TenantShadow`; entities contain no current column. A fresh store Snapshot returns zero current values. |
| Dedup comparers/raw strings | Pass | Sessions remain `Ordinal`; machines/repos remain `OrdinalIgnoreCase`; raw strings are inserted and rehydrated into those sets. No database distinct/citext/case fold was introduced. |
| 90-day retention | Pass | Both hour and member tables use the same tenant, cutoff key, and inclusive/exclusive boundary at lines 400-419. |
| Weekly cutoff and ordinal order | Pass | Same `nowUtc.AddDays(-7)`, parsed hour-start `>=` comparison, and `string.CompareOrdinal` sort as the original. |
| Unseen tenant | Pass | No rows plus no shadow yields zero series and an empty hourly list. |

## Probes and limits

- Lock-free review probe output:
  - cross-container raw union: stored distinct `1/1/1`, raw rows `2/2/2`;
  - failed transaction retry: stored distinct `1/1/1`, raw member rows `0`;
  - out-of-order parity: original `1/1/1`, port `2/1/1`.
- The first line is **not counted as a finding** because the mission proof ledger explicitly records
  cross-container distinct under-counting as an accepted limitation of the mandated in-memory-set design.
- Baseline and mutation Gateway test runs attempted before this report did not start: they waited on the
  automatic suite lock until the caller timeout killed them. They produced no result. The obligation remains
  open and is owed by this reviewer; this report must be amended with the actual baseline/mutation result
  before the review is called complete.
- No full Gateway suite was run. CI, not this review, owns the full-solution result for the merge commit.
