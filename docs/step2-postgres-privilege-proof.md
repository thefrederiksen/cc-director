# Step 2, worker 1: the hosted role CAN create its own statistics schema - proved, and watched failing

The record of the local PostgreSQL proof rig and what it establishes. Branch
`nosqlite-stats-w1-pgrig`. This closes the half of the Architect's verdict 3 ruling 3 that
`docs/step2-nosqlite-stats-plan.md` section 1 records as "still to prove, and it is not proven yet".

---

## What was open

The hosted role's grants were measured read-only against the live database and are recorded in the
plan: `gateway_app` is not superuser, has no CREATEDB, no CREATEROLE and no BYPASSRLS, belongs to no
other role, holds CREATE on the database, can create in and use the existing `gateway` schema, and
can use but not create in `public`.

What that measurement does NOT answer is whether a role holding only those grants can actually
**create a new schema, create tables in it, and run an Entity Framework migration chain there with
its own history table inside it**. That question cannot be asked of the hosted database: staging
shares production's database, so a failed creation experiment lands on the live database the fleet
depends on. It is answered here, against a local container whose role mirrors the measured grants.

**Answer: yes.** A role with no privilege beyond CREATE on the database creates `gateway_stats`, owns
it, creates tables in it, and runs a two-migration Entity Framework chain whose history table is
`gateway_stats.__EFMigrationsHistory`. Take that single privilege away and it cannot, with
`42501: permission denied for database`.

---

## The rig

`scripts/pg-stats-proof-rig.ps1`. A throwaway `postgres:16` container, a restricted login role, and
two connection strings.

```
powershell -NoProfile -File scripts\pg-stats-proof-rig.ps1 -Instance w1 -Port 55433 -Verb up
```

Verbs: `up`, `down`, `status`, `print-env`, `revoke-database-create`, `grant-database-create`,
`reset-stats-schema`.

### One instance per caller, enforced by the parameter contract

`-Instance` and `-Port` are mandatory and there is no shared default. Every container, database and
role name derives from `-Instance`, so two agents running this script land on two servers rather than
one. That is not tidiness. This script revokes a privilege, grants it back, and tears containers
down; any of those landing inside another worker's running test produces a result that is
confidently wrong - a revoke during their green run reads as a real finding, and a revoke during
their deliberate-red run makes a test that does NOT detect look like one that does. A wrong proof is
worse than no proof.

Two further guards back that up: the container carries an instance label and the script refuses to
touch a container of its own name that does not carry it, and it refuses a host port another
container already publishes, naming the squatter.

### Two databases per instance

The existing Postgres proof suite calls `EnsureDeleted()` on whatever `CC_GATEWAY_TEST_PG_CONNECTION`
points at. The suites run in parallel, so one shared database would let a from-nothing migrate drop
the database out from under a parallel run. `ccpgproof_<instance>` is the droppable one;
`ccpgstats_<instance>` holds the restricted role's work and nothing drops it.

### The mirror, and why it is revoked as well as granted

```
GRANT CONNECT, CREATE ON DATABASE ccpgstats_w1 TO gateway_app_w1;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
REVOKE CREATE ON SCHEMA public FROM gateway_app_w1;
GRANT USAGE ON SCHEMA public TO gateway_app_w1;
CREATE SCHEMA IF NOT EXISTS gateway AUTHORIZATION postgres;
GRANT USAGE, CREATE ON SCHEMA gateway TO gateway_app_w1;
ALTER ROLE gateway_app_w1 NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS INHERIT;
```

`CREATE` on `public` is revoked from `PUBLIC` as well as from the role, because a role inherits
PUBLIC's grants implicitly. Leaving it would hand the local role a privilege the hosted role does not
have, and every proof run against it would be green and worthless.

What `-Verb up` reports back, which is the same question set that was asked of the live hosted role:

```
rolsuper=false rolcreatedb=false rolcreaterole=false rolbypassrls=false rolinherit=true
memberships=0
is_database_owner=false
database_CREATE=true
public_CREATE=false
public_USAGE=true
gateway_CREATE=true
gateway_stats_exists=false
```

`is_database_owner=false` earns its place: an owner can create a schema by virtue of ownership even
with no explicit grant, so a rig that accidentally made the role the owner would prove the creation
works for a reason the hosted role cannot rely on.

---

## The tests

`src/CcDirector.Gateway.Tests/Data/GatewayStatsSchemaPrivilegeProofTests.cs`, with the proof model and
its generated migration chain in `src/CcDirector.Gateway.Tests/Data/StatsSchemaProof/`.

Gated on BOTH `CC_GATEWAY_TEST_PG_CONNECTION` (superuser, used only to revoke and restore the grant)
and `CC_GATEWAY_TEST_PG_STATS_CONNECTION` (the restricted role, the subject of every proof). With
either unset the four tests report SKIPPED and nothing reaches a server.

| Test | What it establishes |
|---|---|
| `RestrictedRole_MirrorsTheMeasuredHostedGrants` | The local role holds exactly the measured hosted grants and no others, read back out of the catalog. Everything else is only worth reading if this passes. |
| `RestrictedRole_CreatesStatsSchemaAndTable_FromNothing` | Raw SQL: creates `gateway_stats`, owns it, creates a table in it, round-trips a row - and is REFUSED a table in `public` in the same session. |
| `RestrictedRole_AppliesMigrationChain_WithHistoryTableInsideStatsSchema` | Entity Framework: two migrations applied from nothing, history table inside `gateway_stats` holding both rows, every object in `gateway_stats` and none in `public`, then a write and a read through the context. |
| `RestrictedRole_WithoutDatabaseCreate_CannotCreateStatsSchema_AndCanAgainOnceRestored` | The failing direction, made permanent: with the grant revoked, both the raw `CREATE SCHEMA` and the migrate fail with SQLSTATE 42501 naming the database; the grant is restored in a `finally` and the identical migrate then succeeds. |

### The trap this rig is built around

`migrationBuilder.EnsureSchema` emits `CREATE SCHEMA IF NOT EXISTS`, which is a **silent no-op** when
the schema is already there. A migrate run against an existing `gateway_stats` would therefore pass
identically whether the privilege was present or absent - a guard supplying its own evidence. Every
test here drops the schema and asserts it is gone before creating it, so the creation that follows is
a real one.

The same trap is why the `gateway` schema existing on the hosted database proves nothing about the
create privilege, which is the point the plan records at length.

### Why the chain is two migrations and not one

After the first migration, Entity Framework has to read its history table back OUT of `gateway_stats`
to work out what to apply next. A history table written to the wrong schema shows up exactly there
and nowhere earlier. The second migration also ALTERs a table already inside the schema, so the chain
covers more than fresh creates.

---

## Watched failing on purpose

A test that has never been seen red is not evidence. The migration-chain test was run with the one
privilege under test revoked from outside, and then with it restored.

**Red - `-Verb revoke-database-create`, then the migration-chain test alone:**

```
[pg-stats-proof-rig/w1] REVOKE CREATE ON DATABASE ccpgstats_w1 FROM gateway_app_w1
[pg-stats-proof-rig/w1] has_database_privilege(CREATE) is now: f

  Failed CcDirector.Gateway.Tests.Data.GatewayStatsSchemaPrivilegeProofTests.RestrictedRole_AppliesMigrationChain_WithHistoryTableInsideStatsSchema [1 s]
  Error Message:
   Npgsql.PostgresException : 42501: permission denied for database ccpgstats_w1
  Stack Trace:
     ...
   at Npgsql.EntityFrameworkCore.PostgreSQL.Migrations.Internal.NpgsqlHistoryRepository.Microsoft.EntityFrameworkCore.Migrations.IHistoryRepository.CreateIfNotExists()
   at Microsoft.EntityFrameworkCore.Migrations.Internal.Migrator.Migrate(String targetMigration)
   ...
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 1 s
```

The error names the privilege class and the database, and the frame that failed is Entity Framework
creating its history table - which is the operation the hosted deploy will perform.

**Green - `-Verb grant-database-create`, then the whole class:**

```
[pg-stats-proof-rig/w1] GRANT CREATE ON DATABASE ccpgstats_w1 TO gateway_app_w1
[pg-stats-proof-rig/w1] has_database_privilege(CREATE) is now: t

Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 3 s
```

**The gate, with both variables unset - the four skip and the ordinary run is untouched:**

```
  Skipped ...GatewayStatsSchemaPrivilegeProofTests.RestrictedRole_MirrorsTheMeasuredHostedGrants [1 ms]
  Skipped ...GatewayStatsSchemaPrivilegeProofTests.RestrictedRole_CreatesStatsSchemaAndTable_FromNothing [1 ms]
  Skipped ...GatewayStatsSchemaPrivilegeProofTests.RestrictedRole_AppliesMigrationChain_WithHistoryTableInsideStatsSchema [1 ms]
  Skipped ...GatewayStatsSchemaPrivilegeProofTests.RestrictedRole_WithoutDatabaseCreate_CannotCreateStatsSchema_AndCanAgainOnceRestored [1 ms]

Passed!  - Failed:     0, Passed:    86, Skipped:    20, Total:   106, Duration: 42 s
```

---

## What this does NOT prove

- **It is not a statement about the hosted database's current state.** It says a role holding the
  measured grants can do this. If the hosted role's grants change, the measurement is stale and the
  question reopens - the mirror test would keep passing locally while saying nothing about Supabase.
- **It says nothing about the sixteen real statistics tables**, their column types, their upserts or
  their read projections. The proof model is three deliberately small tables. Workers 2 to 5 own that
  ground.
- **It says nothing about concurrent writers.** The lost-update risk on the high-water tables is
  worker 4's interleaved-writer proof, which uses this rig but is a different assertion.
- **Postgres 16 in a container is not Supabase.** Supabase runs its own build with its own extensions
  and a pooler in front. Nothing here exercises the pooler, and the privilege model is the part
  claimed to transfer, not the deployment.
