# Step 2 Review 4 — baseline, adoption, and version stamp

Range reviewed: `2ceaa5d70..e43922319`.

Verdict: **changes requested. Six findings.** The literal baseline itself is equivalent to the old version-5 database, but the migration target model still describes the rejected generated shape, the new tracked-store guard has unsafe success paths, and the stamp test does not enforce what its name and failure text claim.

## Probe record

- Ran `dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --filter "FullyQualifiedName~GatewayStatsSqlite"`: **17/17 passed**.
- Independently built one database by constructing `GatewayStatsDatabase` and another by running `GatewayStatsDbContext.Database.Migrate()`. A direct, unnormalised `sqlite_master` comparison found only Entity Framework bookkeeping on the baseline side (`__EFMigrationsHistory`, its automatic index, and `__EFMigrationsLock`) plus trailing line breaks on the four named index statements. After whitespace-only comparison, all 16 application tables, four named indexes, automatic indexes, and stored SQL matched. `application_id`, `auto_vacuum`, `encoding`, `page_size`, and `user_version` also matched; both stores reported `user_version=5`.
- Read and exercised the comparison's normaliser. It does not hide quoting or casing. It changes only characters for which `char.IsWhiteSpace` is true. No baseline-equivalence finding resulted from the independent database diff.
- Compared the live EF model metadata with `PRAGMA table_xinfo` from the old-code database. This exposed the target-model mismatch in Finding 1.
- Constructed five adversarial databases and ran `GatewayStatsSqliteAdoption.Adopt` and, where relevant, `Migrate()` and an EF query. These produced Findings 2 and 3.
- Ran concurrent adoption naturally against 100 independently built version-5 files and forced the inspection race with a command interceptor. Natural runs produced five losing adopters; the forced run produced one `Adopted` result and one `NotAdoptable/StoreUnreadable` result. This produced Finding 4.
- Independently migrated the current chain up and down. The current migration writes `user_version=5`, and migration to target `0` resets it to `0`. There is no current `Down()` defect; Finding 5 is about the test's inability to enforce that behavior for subsequent migrations.

## Findings

### 1. HIGH — The migration target model still describes the four rejected generated-schema divergences

**Files and lines:**

- `src/CcDirector.Gateway/Stats/Data/Migrations/20260730160415_InitialGatewayStats.cs:68`
- `src/CcDirector.Gateway/Stats/Data/GatewayStatsDbContext.cs:109`
- `src/CcDirector.Gateway/Stats/Data/GatewayStatsDbContext.cs:125`
- `src/CcDirector.Gateway/Stats/Data/Migrations/GatewayStatsDbContextModelSnapshot.cs:338`
- `src/CcDirector.Gateway/Stats/Data/Migrations/20260730160415_InitialGatewayStats.Designer.cs:341`

**Status: PROVED BY RUNNING and confirmed by reading.**

The raw `Up()` now produces the correct version-5 DDL, but neither the runtime model, the migration designer's target model, nor the snapshot was changed to describe that DDL. The live metadata probe found all eight `tenant` columns whose database default is `'local'` have no configured model default. It also found all eight rowid primary keys are non-nullable CLR properties while the actual version-5 `PRAGMA table_xinfo.notnull` value is `0`. The model continues to use EF's conventional primary-key constraint names.

Those are not new differences: they are the same missing defaults, `NOT NULL` rowid metadata, and named primary keys that this range says made the generated baseline unacceptable. Replacing only `Up()` hides them from the baseline output without correcting the chain's target model. The previous generated migration in the range's base revision is direct evidence of what this unchanged model produces.

This matters on the first later SQLite migration that requires a table rebuild. Scaffolding is diffed from `GatewayStatsDbContextModelSnapshot`, and rebuild DDL is produced from that model. It can therefore remove `DEFAULT 'local'`, restore named key constraints, and restore the rowid `NOT NULL` difference while both new equivalence tests remain green: those tests rebuild only the baseline, not baseline plus a later model-driven migration. The statement that the structural diff “guards the model against the DDL” is unsupported; the test never compares model metadata to the old database or to the literal baseline.

### 2. HIGH — `InspectTrackedStore` certifies invalid stores in both directions

**File and lines:** `src/CcDirector.Gateway/Stats/Data/GatewayStatsSqliteAdoption.cs:181`, `:186`, and `:194`.

**Status: PROVED BY RUNNING.**

The new method reads all applied migrations and all missing table names, but its two success branches do not validate the state they describe:

1. If the baseline ID is present, line 186 returns `AlreadyTracked` before considering `missingTableCount`. I built a healthy baseline store, dropped `stat_delta`, and called adoption. It returned `AlreadyTracked/None`, `IsUsable=true`, and zero pending migrations. The next EF query failed with `SQLite Error 1: 'no such table: stat_delta'`.
2. If all expected statistics tables are absent, line 194 returns `FreshStore` without requiring `applied.Count == 0` and without requiring the database to contain no foreign user tables. I built a different EF database containing `somebody_elses_table` and a valid history row named `20990101000000_Foreign`. Adoption returned `FreshStore/None` with detail claiming the history “records no migrations.” `Migrate()` then added all 16 statistics tables and the baseline row to that foreign database, leaving both migration IDs in its history.

The second case is more than a diagnostic error: the code modifies a database it was required to refuse as “not this store.” The first case sends a known-broken tracked store past the same containment boundary this change was added to protect.

### 3. MEDIUM — Untracked eligibility validates names/counts, not the schema or SQLite object namespace

**File and lines:** `src/CcDirector.Gateway/Stats/Data/GatewayStatsSqliteAdoption.cs:117`, `:121`, `:130`, `:145`, and `:153`.

**Status: PROVED BY RUNNING.**

The version-5 adoption check proves only that 16 table names exist. It does not validate columns, keys, indexes, defaults, or stored DDL before permanently stamping the baseline.

I built a real old-code version-5 store, dropped `stat_delta`, and recreated a table of the same name with only `id`. Adoption returned `Adopted/None` and `IsUsable=true` and inserted the baseline history row. The first EF read then failed with `SQLite Error 1: 'no such column: s.chars'`. This is a readable but structurally corrupt file, yet it is changed and certified rather than returned as `StoreUnreadable` or `NotAStatisticsStore`.

The “empty” branch has the inverse namespace hole. `CountUserTables` counts only `type='table'`. A database containing only `CREATE VIEW stat_delta AS SELECT 1 AS id` and `user_version=5` was returned as `FreshStore/None`; the instructed `Migrate()` then escaped the adoption boundary with `SQLite Error 1: 'view stat_delta already exists'`.

The row-read test cannot close this gap: it exercises one known-good old-code `stat_delta` fixture after the eligibility decision. It does not make the production eligibility decision structural, and it covers none of the other 15 model mappings.

### 4. MEDIUM — Adoption has a check-then-create race and misreports a healthy winner's store as unreadable

**File and lines:** `src/CcDirector.Gateway/Stats/Data/GatewayStatsSqliteAdoption.cs:123`, `:153`, and `:237`.

**Status: PROVED BY RUNNING.**

`history.Exists()` and `Stamp()` are separated by the remainder of inspection. `Stamp()` starts a transaction only after the decision and runs an unconditional history-table create script. It does not acquire EF's migration lock, retry, or re-inspect after a create collision.

Two adopters that both observe no history race on `CREATE TABLE __EFMigrationsHistory`. Across natural runs this produced losing results intermittently. With both `history.Exists()` calls synchronised after returning false, one adopter returned `Adopted/None`; the other returned `NotAdoptable/StoreUnreadable` with `SQLite Error 1: 'table "__EFMigrationsHistory" already exists'`.

The database is healthy and adopted at that point. Classifying the loser as an unreadable-store failure can leave one Gateway process with statistics disabled until restart. The one transaction protects the two writes of a single adopter, but it does not make the preceding inspect-and-adopt decision atomic across adopters.

### 5. MEDIUM — The version-stamp test checks one final sum, not each migration or any `Down()`

**File and lines:** `src/CcDirector.Gateway.Tests/Data/GatewayStatsSqliteVersionStampTests.cs:81`, `:98`, `:101`, `:105`, and `:120`.

**Status: INFERRED BY READING; current `Up()` and `Down()` were independently run and are correct.**

`EveryMigrationInTheSqliteChainMovesTheSchemaVersionStamp` applies the entire chain once and checks only:

`final user_version == 4 + migration count`.

It does not migrate to each intermediate target, so one migration can omit its bump while another over-bumps and the test stays green. It never migrates down, despite its failure text claiming to enforce “a matching reset in its Down().” A constant final stamp in the last migration can satisfy the arithmetic just as easily as one correct bump per migration.

There is also an unavoidable next-migration contradiction between the two facts. With a second correctly stamped migration, the first fact requires final version `6`, while `TheStampAFreshChainBuiltStoreCarriesIsTheOneTheShippedCodeWrites` still requires `GatewayStatsDatabase.SchemaVersion`, currently `5`. Raising that legacy constant is not a harmless test update: `GatewayStatsSqliteAdoption.AdoptableSchemaVersion` reads the same constant, so doing so would stop recognizing the real version-5 no-history files this adoption exists to protect. The baseline's source version must stay frozen while the chain's current version advances; the tests currently treat them as the same value.

### 6. LOW — The release evidence's state table contradicts the changed guard

**File and lines:** `docs/evidence/step2-self-host-adoption.md:175` and `:178`.

**Status: INFERRED BY READING.**

The release-facing outcome table still says any “History table already present” store is `AlreadyTracked`. The code added in this range explicitly rejects a history table without the baseline when application tables are present, and later prose in the same document explains that behavior. The table therefore directs the release seat to expect the pre-fix behavior and makes a correct `MigrationHistoryIncomplete` result look like a regression.

## Confirmed non-findings

- The application schema created by the literal baseline matches a database produced by running the old code. The independent `sqlite_master` and PRAGMA comparison did not reveal an excluded application object or a quoting/casing difference.
- The current baseline stamps `user_version=5`, and its current `Down()` resets the value to `0`.
- A genuine old-code version-5 store is adopted successfully; the baseline ID is inserted; subsequent `Migrate()` has no pending migration; and the targeted suite passes.
- An empty EF history table beside the genuine 16 tables is refused as `MigrationHistoryIncomplete`. An empty history table beside an otherwise empty database is allowed and the current baseline applies successfully.

## Known limitations deliberately not counted

The review does not count the four limitations the brief excluded from findings: no whole-suite/CI run, detection without repair of an interrupted migration, no live desktop downgrade exercise, and the PostgreSQL proof having used a superuser.
