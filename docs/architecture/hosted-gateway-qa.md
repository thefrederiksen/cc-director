# Hosted Gateway - QA evidence

Dated proof records for the Hosted Gateway mission's data-layer steps.

## Step 4a - Postgres provider proof

Date: 2026-07-18

Goal for this step: give the Gateway's EF Core context (`GatewayDbContext`) a PostgreSQL provider
for the single-tenant hosted install, decoupled from multi-tenancy, while the local install stays on
SQLite. The model is provider-agnostic; this step proves it actually runs on real Postgres and that the
SQLite path is untouched.

### What changed

- `CcDirector.Gateway.csproj` - added `Npgsql.EntityFrameworkCore.PostgreSQL` version 9.0.2, pinned to
  the EF Core 9.0.2 already referenced (a 10.x Npgsql would drag EF Core to 10 and break the pin).
  Restore is clean under TreatWarningsAsErrors - no downgrade, no version conflict.
- `Data/GatewayDatabase.cs` - provider selection is now config-driven and reads `CC_GATEWAY_DB_CONNECTION`
  in three distinct cases: UNSET (the variable is absent) keeps exactly today's SQLite behavior; SET to a
  blank/whitespace value THROWS a fail-loud configuration error before any provider is built (a blank value
  is a misconfiguration, never a request for SQLite); SET to a real connection string uses Npgsql (migrations
  assembly `CcDirector.Gateway.Migrations.Postgres`, history table `__EFMigrationsHistory` in the `gateway`
  schema). There is no fallback between the two providers - a configured-but-broken Postgres throws with a
  message that names the provider and states it will NOT revert to SQLite. The SQLite-only
  `PRAGMA journal_mode=WAL` runs on the SQLite branch only. The Postgres path logs a credential-redacted
  target (host + database only, parsed with `NpgsqlConnectionStringBuilder`), never the connection string,
  and never interpolates the provider's exception message (only the exception type name) into a log or
  thrown message, so a password can never reach a log.
- `Data/GatewayDbContext.cs` - `OnModelCreating` adds a Postgres-only block guarded by
  `Database.IsNpgsql()` so SQLite is 100% unchanged: `HasDefaultSchema("gateway")`, and an explicit
  byte-ordinal collation `"C"` on the four natural-key string primary-key columns
  (`snoozes.SessionId`, `push_subscriptions.Endpoint`, `session_spend.SessionId`, `mission_notes.Key`)
  so their ordering and uniqueness match SQLite's default BINARY (memcmp) behavior. The UTC converter,
  decimal ban, GUID-key rule, and tenant scope are untouched (already provider-agnostic).
- `Data/GatewayDbContextDesignTimeFactory.cs` - one design-time factory, switched by
  `CC_GATEWAY_EF_PROVIDER` (`postgres` selects Npgsql with the Postgres migrations assembly and gateway
  history table; unset selects SQLite). Exactly one `IDesignTimeDbContextFactory<GatewayDbContext>` in
  the tree, so the tooling is never ambiguous.
- New project `src/CcDirector.Gateway.Migrations.Postgres` (net10.0, Nullable, TreatWarningsAsErrors) -
  holds only the generated Postgres migration and snapshot. It references `CcDirector.Gateway` for the
  model; `CcDirector.Gateway` does NOT reference it back (that would be a cycle). Added to the solution.
- `CcDirector.Gateway.Tests.csproj` - references the Postgres migrations project so the proof test can
  load it, plus the new gated proof test `Data/PostgresProviderProofTests.cs`.

### The migration command

The Postgres migration was scaffolded by switching the design-time provider to Postgres via
`CC_GATEWAY_EF_PROVIDER`:

```
CC_GATEWAY_EF_PROVIDER=postgres dotnet ef migrations add InitialPostgres \
  --project src/CcDirector.Gateway.Migrations.Postgres \
  --startup-project src/CcDirector.Gateway.Migrations.Postgres \
  --context GatewayDbContext \
  --output-dir Migrations
```

Note on regeneration: the Postgres migrations project is BOTH the target (`--project`) and the
`--startup-project`. It references `CcDirector.Gateway`, so `GatewayDbContext` and the single design-time
factory are discovered through that reference, and the migrations assembly is the startup project's own
output - so the migration DLL is always present and the command is reproducible from a clean checkout with
no "build the other project first" step. `CcDirector.Gateway` still deliberately does NOT reference the
migrations project (no cycle). To confirm the committed migration is in sync with the model,
`dotnet ef migrations has-pending-model-changes` with the same `--project`/`--startup-project` reports no
pending changes.

At runtime, whatever hosted startup/publish project composes the Gateway will need to reference
`CcDirector.Gateway.Migrations.Postgres` so the migration DLL ships beside the Gateway assembly and
`Database.Migrate()` can load it. That host/publish wiring - plus a publish smoke test that loads the
assembly and constructs `GatewayDatabase` against a real Postgres - is NOT done in this increment; it is
DEFERRED to the deploy increment. Today the ONLY project that references the migrations assembly is the
test project `CcDirector.Gateway.Tests` (added here so the proof test can load it); the Gateway host is
deliberately left untouched. So the runtime hosted path is proven at the data-layer level by the test
project here, but the end-to-end host packaging is still owed by the deploy increment.

The command wrote `Migrations/20260718120027_InitialPostgres.cs`, its `.Designer.cs`, and
`Migrations/GatewayDbContextModelSnapshot.cs` into the Postgres migrations project only. The SQLite
migration set and snapshot under `src/CcDirector.Gateway/Data/Migrations/` are byte-for-byte unchanged
(`git status` reports zero changes there).

### Generated Up() - the Postgres-specific shape

The generated migration puts every table under the `gateway` schema, uses `jsonb` for the owned JSON
columns, `timestamp with time zone` for every UTC `DateTime`, `uuid` for every GUID key, and
`collation: "C"` on the four natural-key primary-key columns. Excerpts:

```
migrationBuilder.EnsureSchema(
    name: "gateway");

migrationBuilder.CreateTable(
    name: "cron_jobs",
    schema: "gateway",
    columns: table => new
    {
        Id = table.Column<string>(type: "text", nullable: false),
        ...
        CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
        ...
        Action = table.Column<string>(type: "jsonb", nullable: false),
        Target = table.Column<string>(type: "jsonb", nullable: false)
    },
    ...);

migrationBuilder.CreateTable(
    name: "account_hosted_ai_spend",
    schema: "gateway",
    columns: table => new
    {
        Id = table.Column<Guid>(type: "uuid", nullable: false),
        ...
    },
    ...);

migrationBuilder.CreateTable(
    name: "push_subscriptions",
    schema: "gateway",
    columns: table => new
    {
        Endpoint = table.Column<string>(type: "text", nullable: false, collation: "C"),
        ...
    },
    constraints: table =>
    {
        table.PrimaryKey("PK_push_subscriptions", x => x.Endpoint);
    });
```

The other three `collation: "C"` columns are `snoozes.SessionId`, `session_spend.SessionId`, and
`mission_notes.Key`. Counts across the whole migration: 25 `timestamp with time zone` columns, 17
`uuid` columns, 8 `jsonb` columns (cron target/action, wingman versions, workflow_runs
criteria/proof/participants, workflow_versions steps/outcome_criteria), and exactly 4 `collation: "C"`
columns. The snapshot carries `HasDefaultSchema("gateway")`.

### Proof on real Postgres

A throwaway Postgres 16 container was started:

```
docker run -d --name cc-pg-proof -e POSTGRES_PASSWORD=proof -e POSTGRES_DB=ccpgproof \
  -p 55432:5432 postgres:16
```

The database name begins with the dedicated throwaway prefix `ccpg`; the from-nothing migrate test refuses
to `EnsureDeleted()` any database whose name does not start with that prefix, so the drop can never hit a
real database.

The gated proof tests (`PostgresProviderProofTests`, skipped unless `CC_GATEWAY_TEST_PG_CONNECTION` is
set) were run against it. Each builds a `GatewayDbContext` on Npgsql with the Postgres migrations
assembly and the gateway-schema history table, calls `Database.Migrate()`, and exercises the shapes that
could diverge between providers - a JSON-owned column (WorkflowVersion Steps + OutcomeCriteria), a
natural-key/collation column (PushSubscription endpoints, asserting byte-ordinal order and PK
uniqueness), an explicit-collation catalog check, and a UTC timestamp (SessionSpend). All writes set
`ActiveTenant` = "local".

```
CC_GATEWAY_TEST_PG_CONNECTION="Host=localhost;Port=55432;Database=ccpgproof;Username=postgres;Password=proof" \
  dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj \
  --filter "FullyQualifiedName~PostgresProviderProofTests"

Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 1 s
```

The five tests that passed:

- `Migrate_CreatesGatewaySchemaAndTables_OnRealPostgres` - drops and re-migrates from nothing (guarded
  so it only drops a database whose name carries a throwaway marker), then asserts the `gateway` schema,
  all 16 mapped tables, and the `__EFMigrationsHistory` table exist under the `gateway` schema and NONE
  of them under `public`.
- `Collation_ExplicitC_OnExactlyTheFourNaturalKeys_OnRealPostgres` - reads `pg_attribute.attcollation`
  (the column's DEFINED collation, which is the type-default pseudo-collation for a plain text column and
  the `C` collation only where the migration set `COLLATE "C"` explicitly, so it distinguishes our
  explicit collation from the database's default even if that default is already C) and asserts EXACTLY
  the four natural-key columns (`snoozes.SessionId`, `push_subscriptions.Endpoint`,
  `session_spend.SessionId`, `mission_notes.Key`) carry an explicit `C`, and no gateway column carries
  any explicit collation other than the default or `C`.
- `WorkflowVersion_JsonOwnedColumns_RoundTrip_OnRealPostgres` - Steps and OutcomeCriteria written and
  read back field-for-field through `jsonb`.
- `PushSubscription_NaturalKeyByteOrdinalCollation_OnRealPostgres` - endpoints `endpoint_a`,
  `endpoint_C`, `endpoint_B` come back ordered `endpoint_B`, `endpoint_C`, `endpoint_a` (byte order:
  'B' < 'C' < 'a'; a locale collation would have put `endpoint_a` first), and a duplicate endpoint is
  rejected with `DbUpdateException`.
- `SessionSpend_UtcTimestampRoundTrip_OnRealPostgres` - UTC timestamps come back equal with
  `Kind == Utc`.

### SQLite still green, Postgres tests skip by default

With the environment variable unset, the same test binary shows the SQLite Data tests green and the five
Postgres tests skipped - the default (SQLite) run is unaffected. The 35 passing include the existing
data-layer tests plus two additions that need no Postgres: the provider-selection tests
(`GatewayDatabaseProviderSelectionTests` - env var unset selects SQLite; env var set-but-blank fails
loud, isolated in a `DisableParallelization` collection with the env var saved and restored) and the
redaction tests (`GatewayDatabaseRedactionTests` - a password containing ';' and '=' never appears in
`RedactConnectionTarget`'s output, and an unparseable string returns the fixed literal without echoing
input).

```
dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --filter "FullyQualifiedName~Data"

  Skipped CcDirector.Gateway.Tests.Data.PostgresProviderProofTests.PushSubscription_NaturalKeyByteOrdinalCollation_OnRealPostgres
  Skipped CcDirector.Gateway.Tests.Data.PostgresProviderProofTests.WorkflowVersion_JsonOwnedColumns_RoundTrip_OnRealPostgres
  Skipped CcDirector.Gateway.Tests.Data.PostgresProviderProofTests.Collation_ExplicitC_OnExactlyTheFourNaturalKeys_OnRealPostgres
  Skipped CcDirector.Gateway.Tests.Data.PostgresProviderProofTests.Migrate_CreatesGatewaySchemaAndTables_OnRealPostgres
  Skipped CcDirector.Gateway.Tests.Data.PostgresProviderProofTests.SessionSpend_UtcTimestampRoundTrip_OnRealPostgres

Passed!  - Failed:     0, Passed:    35, Skipped:     5, Total:    40, Duration: 2 s
```

The whole solution builds clean (Debug): 0 warnings, 0 errors, TreatWarningsAsErrors on. The container
was removed after the run (`docker rm -f cc-pg-proof`).

## Step 4a - increment 2: proof on the real Supabase Postgres

Date: 2026-07-18

Increment 1 proved the provider on a throwaway Docker Postgres. Increment 2 proves the SAME provider
against the REAL hosted target - the Supabase Postgres project - through the actual Gateway startup path,
in an isolated `gateway` schema, before any Azure deploy.

### Target and constraints

The connection points at the hosted Supabase project's SESSION POOLER
(`Host=<...>.pooler.supabase.com;Port=5432;...;SSL Mode=Require`, password REDACTED - never committed or
logged), authenticating as a dedicated role scoped to the `gateway` schema ONLY: it has no access to the
website's accounts/auth/public tables, and it cannot create or drop databases. So the increment-1 harness
that drops a throwaway database (the `ccpg`-prefixed `EnsureDeleted` path) does NOT apply here. This is a
real integration run instead: it applies the migration into the existing `gateway` schema and leaves it in
place (deploy-ready), and it cleans up its own test rows so the tables are left empty.

The connection string lives ONLY in the machine-local credential file
(`%LOCALAPPDATA%/cc-director/config/credentials.env`, key `DEVTHROTTLE_GATEWAY_DB_CONNECTION`), read at the
point of use and exported into `CC_GATEWAY_DB_CONNECTION`. It is never echoed, committed, or written to a
log - the Gateway's Postgres path logs only a redacted host+database target.

### The proof (a real integration run)

`GatewayDatabaseLivePostgresProofTests` (new) drives the REAL runtime path: each fact constructs a
`GatewayDatabase` (the same class the running Gateway uses), whose constructor reads
`CC_GATEWAY_DB_CONNECTION`, selects Npgsql, and runs `Database.Migrate()` - applying the Postgres migration
set into the `gateway` schema on Supabase. It never calls `EnsureDeleted` and never drops anything.

CI SAFETY: every fact is gated behind the PRESENCE of `CC_GATEWAY_DB_CONNECTION` (skips cleanly when
unset, the same pattern as the increment-1 Postgres tests). Automated CI never sets it, so CI never
connects to the hosted project and never needs the secret; the proof runs only locally/manually with the
connection string exported.

```
CC_GATEWAY_DB_CONNECTION="<Supabase session-pooler string; password redacted>" \
  dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj \
  --filter "FullyQualifiedName~GatewayDatabaseLivePostgresProofTests"

Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 6 s
```

The four facts that passed on real Supabase:

- `Startup_AppliesGatewaySchemaAndTables_OnConfiguredPostgres` - constructing `GatewayDatabase` migrates
  into the `gateway` schema; asserts the schema, all 16 mapped tables, and `__EFMigrationsHistory` exist
  under `gateway` and NONE of the mapped tables exist under `public` (schema isolation holds on the real
  project).
- `JsonOwnedColumns_RoundTrip_OnConfiguredPostgres` - a WorkflowVersion's Steps + OutcomeCriteria written
  and read back field-for-field through `jsonb`, then the row deleted.
- `NaturalKeyByteOrdinalCollation_RoundTrip_OnConfiguredPostgres` - endpoints come back in byte order
  (`..._B`, `..._C`, `..._a`; a locale collation would have put `..._a` first), a duplicate endpoint is
  rejected, then the rows deleted.
- `UtcTimestampRoundTrip_OnConfiguredPostgres` - a session_spend row's UTC timestamps come back equal with
  `Kind == Utc`, then the row deleted.

### Schema left clean and deploy-ready

Every round-trip deletes its rows in a `finally`, so a failed assertion could not leave data behind.
Verified independently after the run by counting rows directly on Supabase (via `psql` over the pooler),
summing across ALL 16 application tables rather than only the three written:

```
app_table_rows_total=0        (sum of row counts across all 16 mapped tables)
migrations_history_rows=1     (the one applied InitialPostgres record - expected)
app_tables_in_gateway=16
```

So the `gateway` schema holds the 16 application tables, ALL empty (0 rows in total), plus the
`__EFMigrationsHistory` table carrying exactly one row: the applied `InitialPostgres` migration. That one
history row is not residue - it is the record that the migration ran, and it is precisely what makes the
schema deploy-ready: the eventual hosted deploy's `Migrate()` reads it, sees the migration already applied,
and is a no-op. This closes increment 2. The runtime host packaging that ships the migrations assembly (so
the deployed Gateway can load it) remains the deploy increment's work.
