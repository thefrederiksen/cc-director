# Step 2 review 1: statistics model and migrations

## Verdict

Reject this range as a shippable SQLite baseline. I found **6 findings: 3 critical, 2 high, and 1
medium**. The worst defect is that the mandatory version-5 adoption path does not exist, so the first
Entity Framework migration attempt against every existing self-host store will try to create tables
that are already present. Independently, the new SQLite baseline is not structurally equivalent to a
database produced by `GatewayStatsDatabase` at schema version 5.

Review range: `fd959cac3..0402c8595`.

The schema comparison was made two ways:

1. Direct comparison of the migration against the versioned DDL in
   `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs`.
2. A runtime probe that created one fresh database through `GatewayStatsDatabase` and another through
   `GatewayStatsDbContext.Database.Migrate()`, then compared `sqlite_master`, `PRAGMA table_info`, and
   `PRAGMA user_version`.

## Findings

### 1. Critical — the mandatory SQLite version-5 adoption path is absent

**Files and lines:**

- `src/CcDirector.Gateway/Stats/Data/Migrations/20260730160415_InitialGatewayStats.cs:11-28`
- `src/CcDirector.Gateway/Stats/Data/GatewayStatsDbContextDesignTimeFactory.cs:42-48`
- Requirement: `docs/step2-entity-contract.md:104-118`

The range adds one SQLite baseline migration whose first operation creates `agent_delta`, and it adds
only a design-time factory. It adds no runtime adopter, no version check, no history-table stamp, no
named/logged adoption step, and no adoption test. A repository-wide search of the range finds no
adoption implementation.

An existing self-host database has all sixteen tables and `PRAGMA user_version = 5`, but no
`__EFMigrationsHistory`. When `Database.Migrate()` is eventually pointed at it, Entity Framework sees
the baseline as pending and executes `CreateTable("agent_delta")`; SQLite fails because that table
already exists. This is the exact all-existing-install failure the contract says worker 2 must
prevent.

The missing test is part of this defect: there is no proof that a real version-5 file is stamped, that
its rows survive, or that a different `user_version`/non-statistics file is rejected.

### 2. Critical — the baseline drops the version-5 `tenant DEFAULT 'local'` from eight tables

**Files and lines:**

- Source of truth: `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs:549-556`
- Baseline columns:
  - `agent_delta.tenant`: `20260730160415_InitialGatewayStats.cs:23`
  - `agent_driven_delta.tenant`: `20260730160415_InitialGatewayStats.cs:39`
  - `agent_identity.tenant`: `20260730160415_InitialGatewayStats.cs:67`
  - `checkout_identity.tenant`: `20260730160415_InitialGatewayStats.cs:105`
  - `model_identity.tenant`: `20260730160415_InitialGatewayStats.cs:132`
  - `repo_identity.tenant`: `20260730160415_InitialGatewayStats.cs:146`
  - `stat_delta.tenant`: `20260730160415_InitialGatewayStats.cs:198`
  - `token_delta.tenant`: `20260730160415_InitialGatewayStats.cs:217`

Version 5 adds `tenant TEXT NOT NULL DEFAULT 'local'` to each delta and identity table. That default is
retained in the on-disk schema. The new migration creates `tenant TEXT NOT NULL` with no default on all
eight.

The runtime probe reported `dflt_value='local'` for all eight version-5 columns and `dflt_value=NULL`
for all eight baseline columns. This is both a structural and behavioural difference: omitting
`tenant` from a raw insert selects `local` on an adopted database but fails `NOT NULL` on a database
created by the baseline. Stamping an existing file as though this baseline produced it would record a
false schema state for every later migration.

No other version-5 table has a column-default mismatch.

### 3. Critical — eight rowid primary keys have different SQLite nullability metadata

**Files and lines:**

- Version-5 declarations:
  - `stat_delta.id`: `GatewayStatsDatabase.cs:214-225`
  - `token_delta.id`: `GatewayStatsDatabase.cs:455-463`
  - `agent_delta.id`: `GatewayStatsDatabase.cs:302-309`
  - `agent_driven_delta.id`: `GatewayStatsDatabase.cs:327-333`
  - `repo_identity.repo_id`: `GatewayStatsDatabase.cs:274-278`
  - `agent_identity.agent_id`: `GatewayStatsDatabase.cs:279-283`
  - `model_identity.model_id`: `GatewayStatsDatabase.cs:402-406`
  - `checkout_identity.checkout_id`: `GatewayStatsDatabase.cs:516-520`
- Baseline declarations:
  - `agent_delta.id`: `20260730160415_InitialGatewayStats.cs:17-18`
  - `agent_driven_delta.id`: `20260730160415_InitialGatewayStats.cs:34-35`
  - `agent_identity.agent_id`: `20260730160415_InitialGatewayStats.cs:64-65`
  - `checkout_identity.checkout_id`: `20260730160415_InitialGatewayStats.cs:102-103`
  - `model_identity.model_id`: `20260730160415_InitialGatewayStats.cs:129-130`
  - `repo_identity.repo_id`: `20260730160415_InitialGatewayStats.cs:143-144`
  - `stat_delta.id`: `20260730160415_InitialGatewayStats.cs:185-186`
  - `token_delta.id`: `20260730160415_InitialGatewayStats.cs:209-210`

Version 5 declares each as `INTEGER PRIMARY KEY AUTOINCREMENT`; the baseline declares each as
`INTEGER NOT NULL ... PRIMARY KEY AUTOINCREMENT`. SQLite rowid-primary-key semantics prevent a stored
null in either case, but the schemas are not structurally identical: `PRAGMA table_info.notnull` is
`0` for all eight version-5 keys and `1` for all eight baseline keys.

The three intended nullable data columns do otherwise match the real schema exactly:
`stat_delta.model_id`, `stat_delta.checkout_id`, and `token_delta.model_id`. All remaining non-key data
columns are `NOT NULL` on both sides. The divergence is specifically the eight rowid-key declarations,
and a structural adoption proof that checks nullability must detect it.

### 4. High — a fresh baseline database is left at `PRAGMA user_version = 0`, not version 5

**Files and lines:**

- Source-of-truth version stamp: `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs:118-158`
- Baseline `Up` method (contains no version stamp):
  `src/CcDirector.Gateway/Stats/Data/Migrations/20260730160415_InitialGatewayStats.cs:11-271`

`GatewayStatsDatabase` writes `PRAGMA user_version=5` in the same transaction as its schema
migrations. The Entity Framework baseline never writes `user_version`, and the runtime probe confirmed
that its fresh database remains at `0`.

That makes the two creation paths observably different and breaks downgrade/open compatibility with
the existing versioned implementation: the old implementation opens an Entity Framework-created file
as version 0, runs migrations 1 through 5 against already-created tables, and fails at the first
duplicate `ALTER TABLE` column. The file is neither a version-5 store nor safely consumable by the old
versioned path.

### 5. High — the range makes an exact-equivalence claim with no test capable of falsifying it

**Files and lines:**

- Claim: `src/CcDirector.Gateway/Stats/Data/GatewayStatsDbContext.cs:6-19`
- Migration under test: `src/CcDirector.Gateway/Stats/Data/Migrations/20260730160415_InitialGatewayStats.cs:11-271`
- Required real-file adoption test: `docs/step2-entity-contract.md:104-118`

The context says the sixteen tables are carried forward “UNCHANGED,” but the entire commit range adds
zero test files. Nothing compares a database produced by the source-of-truth implementation with one
produced by the baseline. Consequently, changing a column default, a rowid declaration, an index name,
or a key order could leave every test green; the current range already contains two such structural
changes.

A fresh-database-only test would not repair this proof gap because it would derive both its model and
its evidence from the new chain. The required guard must independently create the old side by running
`GatewayStatsDatabase`, compare the normalized structure table by table (including indexes, column
order, types, nullability, defaults, key order, and uniqueness), and be watched failing after a
deliberate schema mutation such as an index rename.

### 6. Medium — all sixteen SQLite primary-key constraints are newly named in `sqlite_master`

**Files and lines:**

- Version-5 unnamed primary keys: `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs:214-367`,
  `402-406`, `455-480`, `516-520`, and `565-627`
- Named baseline primary keys:
  `src/CcDirector.Gateway/Stats/Data/Migrations/20260730160415_InitialGatewayStats.cs:25-250`

The hand-written version-5 DDL does not name any primary-key constraint. The baseline emits
`PK_agent_delta`, `PK_agent_driven_delta`, `PK_agent_driven_highwater`, `PK_agent_identity`,
`PK_agent_session`, `PK_agents_seeded`, `PK_checkout_identity`, `PK_meta`, `PK_model_identity`,
`PK_repo_identity`, `PK_repo_session`, `PK_session_highwater`, `PK_stat_delta`, `PK_token_delta`,
`PK_token_highwater`, and `PK_wingman_session`.

SQLite exposes those names in each table's `sqlite_master.sql`; the existing database has no such
names. Key columns, order, and uniqueness are otherwise the same on all sixteen tables. This does not
change current SQLite key enforcement, but it is another reason the literal claim that the baseline is
the schema already on disk is false, and it gives later migrations model metadata that adopted files do
not actually contain.

## Controls verified

The following sharp questions did not produce additional findings:

- All sixteen application tables exist on both provider migrations with the intended column order and
  storage types, apart from the SQLite differences above.
- `hour_utc` remains a string/text column; it did not become a timestamp.
- The four index names and indexed-column orders match version 5:
  `ix_stat_delta_hour`, `ix_stat_delta_tenant_hour`, `ix_token_delta_hour`, and
  `ix_token_delta_tenant_hour`.
- Neither provider has foreign keys, navigation properties, relationships, alternate keys, or global
  query filters.
- The four identity display columns have no unique constraint or unique index.
- `repo_session` and `agent_session` have no `tenant` column.
- SQLite booleans are `INTEGER`; PostgreSQL booleans are `boolean`; count fields are SQLite `INTEGER`
  and PostgreSQL `bigint`.
- The generated PostgreSQL script creates `gateway_stats`, places all sixteen tables in it, and creates
  `gateway_stats.__EFMigrationsHistory`. It does not use the main context's `gateway` schema/history.
- Both projects build with zero warnings/errors, both migration chains contain one intended baseline,
  and `dotnet ef migrations has-pending-model-changes` reports no snapshot drift for either provider.

No implementation files were changed during this review.
