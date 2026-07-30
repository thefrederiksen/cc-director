# Step 2 entity contract: `GatewayStatsDbContext`

Published by the Step 2 Manager BEFORE the model is built, so the model, the read projections and the
write path can be built in parallel against one agreed shape. Worker 2 implements this; workers 3, 4
and 5 build against it. **If a worker needs this shape changed, it asks the Manager - it does not
change it locally.**

The source of truth for what these tables ARE is
`src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs` at SQLite schema version 5. Every table and
column name below is carried forward UNCHANGED. That is deliberate: the self-host SQLite store already
exists on disk with these exact names, and a rename would strand it.

## Conventions

- Context: `GatewayStatsDbContext`, namespace `CcDirector.Gateway.Stats.Data`.
- Entities: namespace `CcDirector.Gateway.Stats.Data.Entities`, one file each.
- **Table and column names stay snake_case and identical to schema version 5.** Configure them
  explicitly with `ToTable` / `HasColumnName`; never rely on a naming convention.
- Postgres schema: `gateway_stats`. History table: `gateway_stats.__EFMigrationsHistory`.
- SQLite: default schema, no history schema. Its migration chain lives in `CcDirector.Gateway`; the
  Postgres chain lives in `CcDirector.Gateway.Migrations.Postgres`.
- `tenant` is a `string` column named `tenant` (NOT `tenant_id` - that is `GatewayDbContext`'s
  convention and this store does not share it).
- `hour_utc` stays a **string** in the format `yyyy-MM-ddTHH`. It does not become a timestamp. Every
  read projection groups and ranges on it as text, and the `ARCHIVE` marker is a legal value.
- Counts (`turns`, `chars`, every token column) are `long` -> `bigint` on Postgres.
- Surrogate ids (`repo_id`, `agent_id`, `model_id`, `checkout_id`) are `long`, generated on add.
- `is_voice` and `wingman` are `bool`. SQLite already stores them as INTEGER 0/1, which is exactly what
  Entity Framework emits for a bool on SQLite, so the stored shape is unchanged; Postgres gets a real
  `boolean`.
- Nullable exactly where version 5 has it nullable: `stat_delta.model_id`, `stat_delta.checkout_id`,
  `token_delta.model_id`. Nowhere else.
- **No navigation properties and no foreign keys.** Version 5 has none between these tables, the
  identity map is held in memory, and adding them would change delete and insert ordering.

## The sixteen tables

### Delta tables - append only

| Table | Key | Columns |
|---|---|---|
| `stat_delta` | `id` generated | `hour_utc` string, `session_id` string, `modality` string, `surface` string, `is_voice` bool, `repo_id` long, `wingman` bool, `turns` long, `chars` long, `model_id` long?, `checkout_id` long?, `tenant` string |
| `token_delta` | `id` generated | `hour_utc` string, `model_id` long?, `input_tokens` long, `output_tokens` long, `cache_read_tokens` long, `cache_creation_tokens` long, `tenant` string |
| `agent_delta` | `id` generated | `agent_id` long, `is_voice` bool, `turns` long, `chars` long, `tenant` string |
| `agent_driven_delta` | `id` generated | `agent_id` long, `turns` long, `chars` long, `tenant` string |

Indexes, names preserved: `ix_stat_delta_hour` on `(hour_utc)`, `ix_stat_delta_tenant_hour` on
`(tenant, hour_utc)`, `ix_token_delta_hour` on `(hour_utc)`, `ix_token_delta_tenant_hour` on
`(tenant, hour_utc)`.

### Identity tables - surrogate id to FIRST-SEEN display spelling

| Table | Key | Columns |
|---|---|---|
| `repo_identity` | `repo_id` generated | `repo_display` string, `tenant` string |
| `agent_identity` | `agent_id` generated | `agent_display` string, `tenant` string |
| `model_identity` | `model_id` generated | `model_display` string, `tenant` string |
| `checkout_identity` | `checkout_id` generated | `checkout_display` string, `tenant` string |

**No unique constraint on the display column, on either provider.** Version 5 says why at length:
identity here is case-INSENSITIVE and a database can only enforce it under some collation, which would
be the wrong question asked authoritatively. The in-memory `StringComparer.OrdinalIgnoreCase` map is
what guarantees one id per distinct-ignoring-case spelling. Do not "tidy this up".

### High-water tables - the read-modify-write paths, and the whole reason for the upsert ruling

| Table | Composite key | Columns |
|---|---|---|
| `session_highwater` | `(tenant, session_id, modality, surface)` | `turns` long, `chars` long |
| `token_highwater` | `(tenant, session_id)` | `input_tokens`, `output_tokens`, `cache_read_tokens`, `cache_creation_tokens`, all long |
| `agent_driven_highwater` | `(tenant, session_id)` | `turns` long, `chars` long |

**Every write to these three is an explicit `ON CONFLICT DO UPDATE`, never a change-tracked
read-then-save.** This is worker 4's core task and proof row 2 exists to prove it.

### Membership tables - all-time distinct sets, never pruned

| Table | Composite key |
|---|---|
| `wingman_session` | `(tenant, session_id)` |
| `agents_seeded` | `(tenant, session_id)` |
| `repo_session` | `(repo_id, session_id)` - **no tenant column** |
| `agent_session` | `(agent_id, session_id)` - **no tenant column** |

`repo_session` and `agent_session` genuinely have no tenant column at version 5. They are partitioned
INDIRECTLY: `repo_id` and `agent_id` are surrogates minted per tenant, so the pair is already
tenant-unique. Carry the shape forward unchanged and let the contract suite assert the indirect
partitioning. Adding a tenant column here is a behaviour change and is out of Step 2's scope.

Writes to all four are insert-if-absent. Use `ON CONFLICT DO NOTHING`, not a read-then-insert.

### Scalar table

| Table | Composite key | Columns |
|---|---|---|
| `meta` | `(tenant, name)` | `value` string |

`agents_since_utc` is per tenant. `models_since_utc` is a schema fact that rides the local tenant's row
and is read tenant-agnostically. `meta` writes are insert-if-absent for the since-stamps (they are
written once and never moved), so `ON CONFLICT DO NOTHING` - matching version 5's `INSERT OR IGNORE`.

---

## The self-host adoption problem - worker 2 must solve this, it is not optional

An existing self-host `gateway-stats.db` is at `PRAGMA user_version = 5` and has **no**
`__EFMigrationsHistory` table. Point an Entity Framework SQLite migration chain at it and the baseline
migration will try to create sixteen tables that already exist, and the open will fail. Every self-host
user who has ever run the statistics page is in that state.

The answer is **adoption, not retirement and not a fallback**: on opening a SQLite statistics store,
if the file exists, reports `user_version = 5`, and has no `__EFMigrationsHistory`, create the history
table and stamp the baseline migration as applied - then let the chain proceed normally from there. The
rows are already in the right shape; the only thing missing is the bookkeeping that says so.

Write it as an explicit, named, logged adoption step with its own test that starts from a real version
5 file. A file at any OTHER `user_version`, or a file that is not a statistics store at all, is NOT
adopted - it fails loud, exactly as version 5 does today for a newer file.
