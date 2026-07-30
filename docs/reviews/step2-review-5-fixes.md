# Step 2 Review 5 - fixes re-review

Range reviewed: `e43922319..c44b59988`.

Verdict: **changes requested. Four of the six original findings are fixed.** Findings 1, 2, 4, and 6 are fixed; Finding 3 is only partially fixed; Finding 5 is not fixed. The round also introduces two material defects: it changes both baseline migration IDs, causing a healthy store tracked by the previous revision to be rejected (and causing the PostgreSQL baseline to appear pending), and it uses Entity Framework's unbounded SQLite migration lock in a path whose contract promises containment.

| Original finding | Verdict | Evidence basis |
|---|---|---|
| 1. Target model retained rejected divergences | **FIXED** | PROVED BY RUNNING and confirmed by reading |
| 2. Invalid tracked/foreign stores certified usable | **FIXED** | PROVED BY RUNNING |
| 3. Eligibility checked names, not schema/object type | **PARTIALLY FIXED** | PROVED BY RUNNING |
| 4. Check-then-create adoption race | **FIXED** | PROVED BY RUNNING |
| 5. Version test checked only the final sum and no `Down()` | **NOT FIXED** | PROVED BY RUNNING and confirmed by reading |
| 6. Release evidence contradicted the guard | **FIXED** | INFERRED BY READING |

## Probe record

- Ran `dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --filter "FullyQualifiedName~GatewayStatsSqlite"`: **18/18 passed**.
- Ran the new later-rebuild test in an isolated worktree after restoring both `GatewayStatsDbContext` and its SQLite snapshot to `e43922319`. The test failed at its load-bearing assertion: expected tenant default `'local'`, actual `null`. This removes the easiest false green, where the test might have passed against the old model.
- Built and exercised the five refused states requested by the review: a tracked store with a dropped table, a foreign database with its own history row, a version-5 store with wrong columns, a version-5 store with a view in the statistics-table namespace, and an interrupted first migration. Every current refusal returned the intended named reason. After all connections were closed, the database-file SHA-256 was identical before and after each call. The foreign table row and its history row were also still present.
- Recreated `stat_delta` with the exact expected column-name set but no primary key, no `NOT NULL`, no tenant default, and no indexes. Adoption returned `Adopted/None` and inserted the baseline row.
- Forced two adopters' initial `history.Exists()` calls to meet at a barrier. The results were one `Adopted/None` and one `AlreadyTracked/None`, with one baseline history row.
- Added two deliberately malformed migrations in an isolated worktree: the first did not move `user_version`; the second jumped directly to the final arithmetic value; both `Down()` methods were empty. `EveryMigrationInTheSqliteChainMovesTheSchemaVersionStamp` still passed.
- Created the exact history state written by `e43922319`, using migration ID `20260730160415_InitialGatewayStats`, over a genuine version-5 store. Current adoption returned `NotAdoptable/NotAStatisticsStore` even though the store was healthy and the database file remained unchanged.
- Created a genuine version-5 store with EF Core's `__EFMigrationsLock` schema and a persisted lock row but no history table. `Adopt` did not complete within 2.5 seconds. Inspection of the exact EF Core 9.0.2 provider used here confirmed that the synchronous lock acquisition retries forever and has no timeout or cancellation path.
- Re-ran the positional-binding sweep across the production statistics code and the frozen hand-rolled reader. No `SELECT *` targets any of the sixteen tables, every insert names its columns, and every ordinal read follows an explicit select list. The one rebuild copy also supplies explicit destination and source column lists.

## Original findings

### 1. FIXED - the tenant default is now in the target model and the regression test detects the old model

**Files:**

- `src/CcDirector.Gateway/Stats/Data/GatewayStatsDbContext.cs:43-62` and `:143-238`
- `src/CcDirector.Gateway/Stats/Data/Migrations/GatewayStatsDbContextModelSnapshot.cs`
- `src/CcDirector.Gateway.Migrations.Postgres/StatsMigrations/GatewayStatsDbContextModelSnapshot.cs`
- `src/CcDirector.Gateway.Tests/Data/GatewayStatsSqliteBaselineEquivalenceTests.cs:318-430`

**Status: PROVED BY RUNNING and confirmed by reading.**

All eight delta/identity tenant properties now carry `HasDefaultValue("local")`. Both snapshots and both migration designers contain eight matching default annotations; the PostgreSQL baseline contains eight `defaultValue: "local"` definitions; and the literal SQLite baseline still contains the eight `DEFAULT 'local'` clauses. The current rebuild test passes without a pending-model warning, so the runtime model and SQLite snapshot agree.

Most importantly, the test is not compatible with the defect it claims to catch. With the runtime model and snapshot restored to `e43922319`, it drove the same provider rebuild and failed with `Expected: "'local'"; Actual: null`. The fix therefore changes the mechanism, not just the observed baseline output.

The accepted post-rebuild differences are operationally inert under the code currently in this tree. A later rebuild followed by the next startup returned `AlreadyTracked`; the adoption method does run on every startup, but the baseline-stamping branch does not run because the history table remains. The independent read/write sweep found no declaration-order binding.

Two comments overstate the case without reopening the original defect:

- The statement that column order "cannot be expressed" in the model is false. EF Core 9.0.2 exposes `HasColumnOrder(int?)`. The code may reasonably choose not to pin an inert order, but inability is not the reason.
- The new test rebuilds only `stat_delta`. Its loop proves that the other fifteen tables were not incidentally damaged by that one operation; it does not drive a rebuild of all sixteen tables. It still fails against the old model and therefore covers the original mechanism.

### 2. FIXED - both invalid-store directions are refused and unmodified

**File:** `src/CcDirector.Gateway/Stats/Data/GatewayStatsSqliteAdoption.cs:248-305`.

**Status: PROVED BY RUNNING.**

The tracked-store branch now checks the current shape before returning `AlreadyTracked`. A genuine adopted store with `stat_delta` dropped returned `NotAdoptable/StoreSchemaIncomplete`; its database-file hash was unchanged.

The foreign-history case is also fixed in production code. A database containing `somebody_elses_table`, one retained payload row, and its own `20990101000000_Foreign` migration row returned `NotAdoptable/NotAStatisticsStore`. Its file hash, foreign row, and history row were all unchanged. Refusal and non-mutation are independently proved.

The committed tests do not preserve this full proof; that is reported separately below.

### 3. PARTIALLY FIXED - wrong column names and views are caught, but schema validation is still a column-name set comparison

**File:** `src/CcDirector.Gateway/Stats/Data/GatewayStatsSqliteAdoption.cs:308-414`.

**Status: PROVED BY RUNNING.**

The two requested reproductions now behave correctly:

- A real version-5 store whose `stat_delta` was recreated with only `id` returned `NotAdoptable/StoreSchemaIncomplete`, remained byte-identical, and gained no history table.
- A real version-5 store whose `stat_delta` was replaced by a view returned `NotAdoptable/StoreSchemaIncomplete`, remained byte-identical, retained the view, and gained no history table.

That fixes the two symptoms, not the full mechanism described by the original finding. `ExpectedSchema` maps each table only to a `HashSet<string>` of column names, and `DescribeMismatch` compares only missing/extra names and table-versus-view type. It does not inspect declared types, nullability, primary keys, unique constraints, defaults, foreign keys, or indexes.

The residual hole is executable: I replaced `stat_delta` with `CREATE TABLE ... AS SELECT`, retaining the exact column-name set while removing all constraints and indexes. The resulting table had zero primary-key columns, nullable `tenant`, no tenant default, and zero indexes. Adoption returned `Adopted/None` and permanently stamped the baseline. The release evidence's repeated claim that the guard verifies the "right shape" is therefore still too strong.

### 4. FIXED - the two-adopter create race is serialized and reclassified correctly

**File:** `src/CcDirector.Gateway/Stats/Data/GatewayStatsSqliteAdoption.cs:184-229`.

**Status: PROVED BY RUNNING.**

With both initial history checks forced to return from the same barrier, one adopter acquired the migration lock, created/stamped history, and returned `Adopted`. The second waited, re-read history under the lock, and returned `AlreadyTracked`. There was one history row and neither caller reported `StoreUnreadable`.

The chosen lock introduces a separate crash-recovery defect, reported under New Finding B. That does not leave the original two-live-adopter race in place.

### 5. NOT FIXED - the version test is unchanged and still accepts compensating errors and empty `Down()` methods

**Files:**

- `src/CcDirector.Gateway.Tests/Data/GatewayStatsSqliteVersionStampTests.cs:61-128`
- `src/CcDirector.Gateway/Stats/Data/GatewayStatsSqliteAdoption.cs:48-66`
- `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs:35-50`

**Status: PROVED BY RUNNING and confirmed by reading.**

The adoption check now has a frozen literal `LegacyBaselineSchemaVersion = 5`, so raising some other value will not directly stop recognition of no-history version-5 files. That is useful, but it does not implement the requested current-chain version or fix the test.

`EveryMigrationInTheSqliteChainMovesTheSchemaVersionStamp` is unchanged: it migrates the whole chain once and checks only `4 + migrationCount == final user_version`. In an isolated worktree, a first added migration omitted its bump, a second jumped from 5 to 7, and both `Down()` methods were empty. The test passed. It therefore enforces neither one bump per migration nor any reset on downgrade.

The supposedly current value also remains coupled to the legacy hand-rolled writer: `TheStampAFreshChainBuiltStoreCarriesIsTheOneTheShippedCodeWrites` still compares the chain against `GatewayStatsDatabase.SchemaVersion`, while that class still uses the same value to decide and stamp the old raw-SQL schema. There is no independently governed current-chain version that can advance without either editing this legacy value or editing away the test's asserted equality.

The present baseline's actual `Up()` stamp of 5 and `Down()` reset to 0 are correct. The finding is that the promised mechanical enforcement for subsequent migrations does not exist.

### 6. FIXED - the release outcome table now matches the guard

**File:** `docs/evidence/step2-self-host-adoption.md:174-196`.

**Status: INFERRED BY READING.**

The table now distinguishes a baseline-bearing tracked store, an empty history, a foreign history, missing tables/columns, views/foreign objects, and incompatible versions. It no longer equates history-table presence with `AlreadyTracked`.

Its use of "right shape" should be narrowed until Finding 3 is completed.

## New findings introduced or exposed by the fix round

### A. HIGH - regenerating the baselines changed both migration IDs and condemns a healthy previously tracked store

**Files:**

- `src/CcDirector.Gateway/Stats/Data/Migrations/20260730181222_InitialGatewayStats.Designer.cs:13`
- `src/CcDirector.Gateway.Migrations.Postgres/StatsMigrations/20260730181312_InitialGatewayStats.Designer.cs:14`
- `src/CcDirector.Gateway/Stats/Data/GatewayStatsSqliteAdoption.cs:248-300`

**Status: PROVED BY RUNNING for SQLite; INFERRED BY READING for PostgreSQL.**

At `e43922319`, the SQLite baseline ID was `20260730160415_InitialGatewayStats` and the PostgreSQL baseline ID was `20260730161529_InitialGatewayStats`. This range renames them to `20260730181222_InitialGatewayStats` and `20260730181312_InitialGatewayStats` while changing model metadata.

Migration IDs are persisted data. A genuine version-5 SQLite store carrying the old SQLite baseline row is exactly a store the earlier revision had successfully adopted. Current `BaselineMigrationOf` names the new first migration; `InspectTrackedStore` sees one applied migration but not that new ID and returns `NotAdoptable/NotAStatisticsStore`. The probe reproduced that result and confirmed the old history row and file remained unchanged. This is the inverse guard failure requested by the manager: a healthy store is condemned.

For PostgreSQL there is no adoption guard. EF therefore sees the renamed baseline as pending and attempts its full initial `Up()` against an already-created schema. Unless it is established that no database was ever migrated by either earlier ID, the migration IDs must remain stable while their model metadata is corrected.

### B. HIGH - a crash while holding the new SQLite migration lock leaves future adoption waiting forever

**File:** `src/CcDirector.Gateway/Stats/Data/GatewayStatsSqliteAdoption.cs:184-223` and `:333-356`.

**Status: PROVED BY RUNNING and confirmed by inspecting the referenced EF Core provider.**

`StampUnderLock` calls synchronous `IHistoryRepository.AcquireDatabaseLock()`. EF Core 9.0.2 implements this by creating `__EFMigrationsLock`, inserting row ID 1, and retrying `INSERT OR IGNORE` forever with sleeps until that row disappears. The timestamp is not used for expiry, and this synchronous API has neither timeout nor cancellation. Disposal deletes the row; a process crash does not dispose it.

The adoption inspector deliberately excludes `__EFMigrationsLock` from its object set. Given a genuine version-5 store with a persisted lock row and no history table - the state a crash after lock acquisition and before stamping can leave - eligibility succeeds and `AcquireDatabaseLock()` waits forever. The probe did not complete in 2.5 seconds, while ordinary adoption in the same harness completed in well under a second; the lock row remained and history did not exist.

This bypasses the boundary catch because nothing throws. It contradicts the stated containment contract more severely than the old losing-racer classification: the caller hangs instead of receiving a named unavailable result.

### C. MEDIUM - committed refusal tests still do not prove the required "unmodified" half

**File:** `src/CcDirector.Gateway.Tests/Data/GatewayStatsSqliteAdoptionTests.cs:250-392`.

**Status: INFERRED BY READING; the production behavior was independently PROVED BY RUNNING.**

There are no committed regression tests for the dropped tracked table, wrong-column store, view collision, or two-adopter race. The interrupted-migration test asserts only the result and reason; it does not assert that the empty history and application tables remain unchanged.

The new foreign-database assertion is also weaker than its comment and commit title:

- The fixture has no foreign migration history row, so it does not reproduce the original tracked foreign database.
- The assertion checks only that `stat_delta` and `__EFMigrationsHistory` are absent. It never asserts that `somebody_elses_table`, its rows, or `user_version` survive. Code that deleted or rewrote the foreign database could still make the test green.
- The incompatible-version test checks only that history was not added; it does not prove the existing schema and stamp were preserved.

The ad-hoc probes show the current code leaves all five requested refusal states byte-identical, but the suite does not protect that behavior. Given that mutation was the harmful half of the original defect, this is a material regression-test gap rather than a documentation preference.

## Claims the code does not support

- "Right shape" currently means only expected table object type plus an exact column-name set.
- `ExpectedSchema` is read from the EF model, but the SQLite baseline is hand-written SQL. The comment that deriving the expectation from the model means it "cannot drift from the schema the baseline migration creates" is not generally true; Finding 1 was an instance of precisely that drift.
- The foreign-database test does not prove the file was left exactly as found.
- The version test does not govern each migration or any `Down()` method.
- Adoption does run against a rebuilt tracked file on the next startup; what is true is that it does not stamp the baseline again while history remains.
- EF Core can express column order with `HasColumnOrder`; the remaining order divergence is accepted by choice, not forced by an absent model capability.

## Confirmed non-findings

- A genuine version-5 store is still accepted rather than falsely condemned, and a provider-rebuilt tracked store returns `AlreadyTracked` on the next startup.
- The current exact refusal cases are non-mutating after connections close.
- The current baseline model/default fix is real and the new regression test fails against the old drifted model.
- No positional read or write against the sixteen tables makes the measured post-rebuild column reorder operationally significant in this tree.
