# Step 2 Review 9 - adoption fixes round 2

Range reviewed: `5c622855f..36f3361d4`.

Verdict: **changes requested. Seven of the nine prior findings are genuinely fixed.** Five of the six
original findings are fixed; original Finding 3 remains partially fixed. Of the previous round's three new
findings, A and C are fixed and B is only partially fixed. This range also introduces one HIGH defect in the
replacement lock mechanism, and the requested later-migration probe exposes one pre-existing HIGH downgrade
defect.

| Prior finding | Verdict | Evidence basis |
|---|---|---|
| Original 1. Target model retained rejected divergences | **FIXED** | PROVED BY RUNNING and confirmed by reading |
| Original 2. Invalid tracked/foreign stores certified usable | **FIXED** | PROVED BY RUNNING |
| Original 3. Eligibility checked names, not schema/object type | **PARTIALLY FIXED** | PROVED BY RUNNING |
| Original 4. Check-then-create adoption race | **FIXED** | PROVED BY RUNNING with two real processes |
| Original 5. Version test checked only the final sum and no `Down()` | **FIXED** | PROVED BY RUNNING and confirmed by reading |
| Original 6. Release evidence contradicted the guard | **FIXED** | INFERRED BY READING |
| New A. Regenerated baseline IDs condemned previously tracked stores | **FIXED** | PROVED BY RUNNING for SQLite; confirmed by reading for PostgreSQL |
| New B. EF migration lock could wait forever in adoption | **PARTIALLY FIXED** | PROVED BY RUNNING; replacement has a different containment defect |
| New C. Refusal tests did not prove the store was unmodified | **FIXED** | PROVED BY RUNNING and confirmed by reading |

## Probe record

- Ran `dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --filter
  "FullyQualifiedName~GatewayStatsSqlite"`: **29/29 passed**.
- Raced the public `Adopt` entry point from two separately launched `dotnet` processes against the same real
  version-5 file, ten times. Every run returned exactly one `Adopted` and one `AlreadyTracked`; every database
  contained exactly one history row. Repeated the same ten-process-pair probe with both processes released at
  the `StampUnderLock` boundary; the same invariant held in all ten runs.
- Held a real SQLite write transaction in one process and ran adoption in another using the production-default
  30-second Microsoft.Data.Sqlite command timeout. The path whose detail promises a 5,000 ms bound returned
  `StoreLockedByAnotherProcess` only after **35.065 seconds**. With a seven-second command timeout it returned
  after **12.126 seconds**, again proving that the PRAGMA is not the controlling bound.
- Forced the history-table create to succeed and the history-row insert to throw inside `StampUnderLock`. The
  exception left zero history tables, no current transaction, and a connection that could immediately begin a
  second write transaction; retrying adoption on that same context returned `Adopted`. The transaction cleanup
  itself holds.
- On the same context, successful adoption left no current transaction and `Database.Migrate()` succeeded. The
  connection's `PRAGMA busy_timeout`, however, was 5,000 after adoption and remained 5,000 after migration.
- Probed the additional schema shapes requested: an extra index was accepted, an extra table beside the sixteen
  was refused as `NotAStatisticsStore`, a compatibly redeclared numeric column was accepted, reordered columns
  were accepted, and all accepted stores migrated on the same context.
- Replaced the expected non-unique `ix_stat_delta_tenant_hour` with a UNIQUE index carrying the same name.
  Adoption returned `Adopted`, but inserting two otherwise valid rows for the same tenant/hour failed with
  SQLite error 19 (`UNIQUE constraint failed`).
- Built a tracked store with the restored baseline plus a future history row, `user_version=6`, an extra table,
  an extra column and a future unique index. Adoption returned `AlreadyTracked/None`; `Migrate()` returned
  successfully without changing it; a write valid for the current model then failed on the future constraint.
- Recreated the history row persisted before the accidental rename,
  `20260730160415_InitialGatewayStats`, over a real version-5 store. Current adoption returned
  `AlreadyTracked/None`, and migration succeeded. The PostgreSQL filename and `[Migration]` attribute are also
  restored to `20260730161529_InitialGatewayStats`.
- Audited all six before/after fingerprint callers. Each closes its mutating/setup connection and scopes the
  adoption context to disposal before the closing fingerprint. The targeted run exercised all six successfully.

## Prior findings

### 1. FIXED - model defaults and the model/baseline regression remain aligned

**Status: PROVED BY RUNNING and confirmed by reading.**

The target model still carries the eight tenant defaults, the SQLite and PostgreSQL snapshots/designers agree,
and the baseline-equivalence and provider-rebuild tests passed in the 29-test targeted run. This range does not
reintroduce the original target-model drift.

### 2. FIXED - rejected tracked and foreign stores remain rejected and unmodified

**Status: PROVED BY RUNNING.**

The dropped tracked table, foreign history, interrupted history, wrong-column table and view collision tests now
fingerprint the complete database before and after refusal. They passed. Production inspection still refuses the
invalid tracked and foreign directions before handing them to the migration chain.

### 3. PARTIALLY FIXED - structural checking is deeper, but an index name is still treated as an index definition

**Files:**

- `src/CcDirector.Gateway/Stats/Data/GatewayStatsSqliteAdoption.cs:429-467`
- `src/CcDirector.Gateway/Stats/Data/GatewayStatsSqliteAdoption.cs:543-597`
- `src/CcDirector.Gateway/Stats/Data/GatewayStatsSqliteAdoption.cs:635-644`

**Status: PROVED BY RUNNING.**

The range materially improves the check: it now verifies primary-key membership/order, nullability, configured
defaults, and the presence of model index names. The malformed exact-column-name table from the previous review
is correctly refused.

It still does not verify what a named index does. `ExpectedTable` retains only `IndexNames`, and
`ReadIndexNames` returns only names. A version-5 store in which `ix_stat_delta_tenant_hour` was changed from the
baseline's non-unique `(tenant, hour_utc)` index to a UNIQUE index of the same name returned `Adopted/None` and
was stamped. A two-row insert valid under the baseline then failed with SQLite error 19. The guard therefore
still certifies a store that cannot accept valid current writes.

The requested inverse probes did not uncover false refusals: a harmless extra index, a compatibly declared
numeric type, and a different column order were accepted. An extra table on an untracked version-5 file was
refused as foreign, consistently with the documented contract.

### 4. FIXED - `BEGIN IMMEDIATE` genuinely serialises two live adopters

**Status: PROVED BY RUNNING with two real processes.**

Ten races through the public entry point and ten races released directly at the write-lock boundary all produced
one `Adopted`, one `AlreadyTracked`, no generic unreadable result, and one baseline row. The re-check and stamp
are in one SQLite write transaction and the losing process reads the committed history after acquiring the lock.

The timeout used while acquiring that transaction is defective, reported under New Finding D; that does not
reopen the live-adopter serialization mechanism itself.

### 5. FIXED - the version test now checks each `Up()` and each `Down()`

**File:** `src/CcDirector.Gateway.Tests/Data/GatewayStatsSqliteVersionStampTests.cs:116-178`.

**Status: PROVED BY RUNNING and confirmed by reading.**

The new test walks the migration list one target at a time, asserts the exact stamp after each `Up()`, then walks
back one target at a time and asserts every `Down()`, including zero after reverting the baseline. The targeted
run executed it successfully. Compensating end-state errors and empty `Down()` methods can no longer hide behind
the final sum.

### 6. FIXED - release evidence now describes the actual guard

**Status: INFERRED BY READING.**

The evidence now states the deliberate extra-column tolerance, the migration-lock refusal and the remaining EF
provider constraint. It no longer claims column order cannot be expressed. Its blanket statement that any other
`user_version` is refused is false for tracked stores, but that is caused by the separately reported downgrade
defect rather than the original outcome-table mismatch.

### A. FIXED - both persisted migration IDs are restored

**Status: PROVED BY RUNNING for SQLite; confirmed by reading for PostgreSQL.**

SQLite again uses `20260730160415_InitialGatewayStats` in both the filename and attribute; PostgreSQL again uses
`20260730161529_InitialGatewayStats`. A real version-5 SQLite store carrying the earlier persisted ID returned
`AlreadyTracked/None` and migrated successfully. The added README explains why these IDs are durable data.

### B. PARTIALLY FIXED - the unbounded EF lock is gone, but the replacement does not enforce its stated bound

**Status: PROVED BY RUNNING.**

Adoption no longer creates or acquires `__EFMigrationsLock`, so the abandoned-row forever-wait from the previous
round is removed. The SQLite transaction also rolls back cleanly on a stamp failure. The replacement's timeout
mechanism is not the five-second bound it claims to be, however. New Finding D is a containment failure in the
same high-value path, so this prior finding cannot be called fully fixed.

### C. FIXED - refusal mutation protection is now committed and exercised

**Status: PROVED BY RUNNING and confirmed by reading.**

The committed suite now fingerprints the whole main/WAL/SHM store around the five refusal shapes that previously
lacked the unmodified half. All six fingerprint pairs scope the context correctly, and all passed. The helper's
`ClearAllPools` plus disposed contexts also avoids hashing through a connection owned by the test itself.

## New defects

### D. HIGH - the introduced `busy_timeout` is not a five-second bound and loses the named reason at startup

**Files:**

- `src/CcDirector.Gateway/Stats/Data/GatewayStatsSqliteAdoption.cs:224-263`
- cross-branch caller: `nosqlite-stats-w6-startup`,
  `src/CcDirector.Gateway/Stats/Data/GatewayStatsStore.cs:88,176-207`
- cross-branch connection selection: `nosqlite-stats-w6-startup`,
  `src/CcDirector.Gateway/Stats/Data/StatsConnectionSelection.cs:165-171`

**Status: PROVED BY RUNNING; startup consequence INFERRED from the measured timing and caller code. Introduced
by this fix range.**

`StampUnderLock` executes `PRAGMA busy_timeout = 5000` and then calls
`connection.BeginTransaction(deferred: false)`. In Microsoft.Data.Sqlite 9.0.2, `BeginTransaction` executes
`BEGIN IMMEDIATE` through an internal `SqliteCommand`; that command retries BUSY/LOCKED according to
`SqliteConnection.DefaultTimeout`, not according to this PRAGMA. The provider's default is 30 seconds. The
self-host connection worker 6 constructs specifies only `DataSource`, so production uses that default.

The live two-process probe took **35.065 seconds** to return the named busy result. A seven-second configured
timeout took **12.126 seconds**. The native five-second busy handler and the provider's managed retry loop
compound rather than one replacing the other. Eventually the busy failure is a `SqliteException`, so the catch
type itself is not the hole; the named result arrives too late.

Worker 6 stops waiting after 20 seconds and reports `Unreachable`, explicitly describing a database or network
problem. It therefore abandons this adoption attempt roughly fifteen seconds before adoption returns
`StoreLockedByAnotherProcess`. The exact operator-misdirection failure requested for review remains: a local
writer lock is surfaced through the generic database/network bucket because the supposed inner bound is longer
than the outer containment deadline.

The setting also leaks beyond its intended scope. On the same context, `PRAGMA busy_timeout` was 5,000 after
adoption and remained 5,000 after `Migrate()`. There was no active transaction and migration succeeded, but the
connection-global setting was not restored.

### E. HIGH - a tracked store from a later migration is certified current and can fail outside containment

**Files:**

- `src/CcDirector.Gateway/Stats/Data/GatewayStatsSqliteAdoption.cs:155-157`
- `src/CcDirector.Gateway/Stats/Data/GatewayStatsSqliteAdoption.cs:356-383`

**Status: PROVED BY RUNNING. Pre-existing, newly exposed by the requested later-migration probe rather than
introduced by this range.**

The version gate runs only on stores without a history table. Once history exists, `InspectTrackedStore` asks
only whether the current baseline ID appears anywhere in the applied list and whether the current model's
required shape remains present. It never reads `user_version`, never rejects applied migration IDs unknown to
this build, and tolerates additional objects/indexes.

A store carrying the restored baseline plus `20990101000000_FutureStats`, `user_version=6`, an extra table, an
extra column and a future unique index returned `AlreadyTracked/None`. `Database.Migrate()` then succeeded as a
no-op because this older chain had nothing pending. A write valid for the current model failed afterward with
SQLite error 19 on the future constraint. Adoption had certified the downgrade usable, so the failure occurred
outside its named containment boundary.

This contradicts the reason type's promise that a newer file is refused and the evidence table's claim that any
non-5 version is `IncompatibleSchemaVersion`. More importantly, it removes the downgrade safety that the new
per-migration `user_version` test exists to preserve.

## Confirmed non-findings

- Busy lock contention eventually surfaces from Microsoft.Data.Sqlite as `SqliteException`; it does not fall
  through the adoption boundary as `StoreUnreadable`. The defect is that it arrives after the outer startup
  deadline and is reported there as `Unreachable`.
- A thrown stamp rolls back the history-table create, clears the transaction and permits an immediate retry on
  the same connection.
- Successful adoption leaves no active transaction that disturbs `Migrate()` on the same context.
- Extra indexes, compatible declared types and column reordering do not falsely condemn the real version-5
  shape; the harmful index case is an expected-name index with different uniqueness/columns, not a harmless
  additional index.
- The restored SQLite migration ID accepts the exact history state written before the accidental rename.
