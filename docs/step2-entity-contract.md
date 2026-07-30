# Step 2 entity contract: `GatewayStatsDbContext`

Published by the Step 2 Manager BEFORE the model is built, so the model, the read projections and the
write path can be built in parallel against one agreed shape. Worker 2 implements this; workers 3, 4
and 5 build against it. **If a worker needs this shape changed, it asks the Manager - it does not
change it locally.**

The source of truth for what these tables ARE is
`src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs`, whose version 5 shape every table and column
name below is carried forward from UNCHANGED. That is deliberate: the self-host SQLite store already
exists on disk with these exact names, and a rename would strand it.

**AMENDED BY THE STEP 2 ARCHITECT'S RULING ON THE WRITE PATH: the shape is now SQLite schema version
6.** Version 6 is purely ADDITIVE - no table is rebuilt, no row is rewritten, and every version 5 name
is untouched. It adds `previous_*` columns to the three high-water tables and a unique index on each
identity table's exact `(tenant, display)` pair, both so that a statement can tell a writer what IT
changed instead of the writer inferring it from its own mirror. The two sections below say what each is
for and why it is not optional. Worker 2's PostgreSQL migration must generate version 6, not version 5;
the model in `GatewayStatsDbContext` already carries it.

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

**The database never decides whether two DIFFERENT spellings are one identity.** That question is
case-INSENSITIVE and a database can only answer it under some collation, which would be the wrong
question asked authoritatively. The in-memory `StringComparer.OrdinalIgnoreCase` map is what guarantees
one id per distinct-ignoring-case spelling. Do not "tidy this up".

**Each identity table DOES carry a unique index on the exact `(tenant, display)` pair** (schema version
6): `ux_repo_identity_tenant_display`, `ux_agent_identity_tenant_display`,
`ux_model_identity_tenant_display`, `ux_checkout_identity_tenant_display`. That is a strictly weaker
question - a byte-for-byte duplicate is a duplicate under every comparer, the case-insensitive one
included - and no collation is being trusted to decide anything. Both providers compare these columns
byte-ordinally already (SQLite BINARY; the `"C"` collation the context pins on PostgreSQL).

It exists so a mint can be an upsert that RETURNS the winning id rather than an insert that assumes it
minted one. Without a conflict target, two hosted containers minting `owner/repo` for one tenant at the
same moment each keep their OWN id and that tenant's turns split silently across two rows - a wrong
number that looks exactly like a right one. Spellings differing only by case still mint separate ids;
that stays the mirror's business.

### High-water tables - the read-modify-write paths, and the whole reason for the upsert ruling

| Table | Composite key | Columns |
|---|---|---|
| `session_highwater` | `(tenant, session_id, modality, surface)` | `turns`, `chars`, `previous_turns`, `previous_chars`, all long |
| `token_highwater` | `(tenant, session_id)` | `input_tokens`, `output_tokens`, `cache_read_tokens`, `cache_creation_tokens` and a `previous_` column for each, all long |
| `agent_driven_highwater` | `(tenant, session_id)` | `turns`, `chars`, `previous_turns`, `previous_chars`, all long |

**Every write to these three is an explicit `ON CONFLICT DO UPDATE`, never a change-tracked
read-then-save.** This is worker 4's core task and proof row 2 exists to prove it.

**And every one of them RETURNS what it changed.** The `previous_*` columns (schema version 6) hold
what the row held immediately before the last raise; the raise statement writes them itself and returns
them beside the new values, so the writer appends exactly the difference IT made rather than the
difference it believed it was making. Nothing reads them as a statistic.

That is not decoration, it is the correctness core. Before it, each writer computed a session's growth
against its own in-memory mirror and appended that growth to the SHARED delta ledger, while the
watermark was arbitrated by the database. Two authorities on one number: two containers measuring from
two stale baselines append MORE in total than the watermark ever moves, and every all-time total - which
is the SUM of that ledger - inflates on every interleave. The watermark assertion passes throughout,
which is why the original proof suite was green.

**The rule, and it governs the whole write path:** *never learn what you changed from your own prior
belief - learn it from the response of whatever arbitrates.* It applies four times: the high-water
raises, the retention sweep (`DELETE ... RETURNING`, archive exactly the rows the statement removed),
the identity mints (upsert, read back the winning id), and the `agents_seeded` claim (insert-if-absent
`RETURNING`, back-fill only if this writer marked it).

The raise also carries the RESET rule, which cannot live in the fold any more. A reported count below
the stored watermark is either a Director restart counting from zero or a stale read another writer has
overtaken, and those look identical from outside. The writer therefore sends the baseline it BELIEVED
the store held as evidence, and the statement rules: belief current means a real reset (adopt the
reported count, report all of it as new activity); belief already overtaken means a stale read (keep the
floor, report nothing). The belief is an input to the arbiter, never the authority.

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

`agents_seeded` additionally carries `RETURNING session_id`, so the writer learns whether ITS insert
created the row. The first-fold back-fill (issue #1633) attributes a session's standing count to its
agent, and two writers first-folding one session both find an unmarked mirror; without the claim, both
attribute it and the agent's numbers multiply by however many containers are running.

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
