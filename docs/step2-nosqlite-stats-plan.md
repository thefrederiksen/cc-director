# Step 2: remove SQLite from the hosted Gateway - the privilege answer and the build plan

Working document for the Step 2 Manager. Branch `nosqlite-stats`, worktree `D:\ReposFred\dt-nosqlite`.
The mission brief is `docs/MISSION-hosted-gateway-remediation-2026-07-30.md`; the deploy-mechanics
rulings are the Architect's verdict 3. This file records what was MEASURED and what is PLANNED. It
grants nothing.

---

## 1. The privilege answer: YES, the hosted role can create a schema. Measured, not assumed.

Verdict 3 ruling 3 required this answered before any entity was written, and required it answered
WITHOUT attempting a creation against the hosted database (staging shares production's database, so a
failed experiment lands on the live database).

**Method.** Read-only interrogation of the live hosted role's grants, from a throwaway
`postgres:16-alpine` container, using the connection string read out of the hosted app settings
(`az webapp config appsettings list -g rg-devthrottle-hosted-gateway -n devthrottle-gw`). Every
statement was a `SELECT` against the catalog. Nothing was created, altered or dropped.

**What the live database says.**

| Question | Answer |
|---|---|
| Connected role | `gateway_app` (session and current user both) |
| Role attributes | not superuser, no CREATEDB, no CREATEROLE, no BYPASSRLS, inherits |
| Role memberships | none - `gateway_app` belongs to no other role |
| `has_database_privilege(current_user, current_database(), 'CREATE')` | **true** - this is the create-a-schema privilege |
| `search_path` | `gateway, "$user", public` |
| Existing `gateway` schema | owner `postgres`, and `gateway_app` **can** CREATE in it and USE it |
| `public` schema | `gateway_app` can USE it but **cannot CREATE in it** |
| A `gateway_stats` or `stats` schema | does not exist yet |
| `__EFMigrationsHistory` | lives in schema `gateway`, **owned by `gateway_app`** |
| Newest applied gateway migration | `20260729173140_SkillPlacementState` |
| Tables in `gateway` schema | 37 |

**The conclusion, and the trap in it.** The role has `CREATE` on the database, so
`CREATE SCHEMA gateway_stats` is permitted and the context can own its own
`gateway_stats.__EFMigrationsHistory` there - the existing history table in `gateway` is already owned
by `gateway_app`, which is direct evidence the role can create and own a migrations history table.

The trap that had to be avoided: the `gateway` schema EXISTING is **not** evidence the role can create
one. `migrationBuilder.EnsureSchema` emits `CREATE SCHEMA IF NOT EXISTS`, which is a silent no-op when
a superuser created the schema at provisioning time. The database privilege is the thing that answers
the question, and that is what was read.

**A correction to the stated fallback.** Verdict 3 names the fallback as "the same physical database,
default schema, table names prefixed". That fallback is **not available**: `gateway_app` has no CREATE
on `public`. Had the schema privilege been missing, the real fallback would have been prefixed tables
inside the existing `gateway` schema. This is recorded because the written fallback would have failed
on deploy day, which is precisely the discovery ruling 3 was designed to force early.

**Still to prove, and it is not proven yet.** That a role holding ONLY `CREATE` on the database (no
superuser, no ownership of an existing schema) can in fact create the schema, create tables in it, and
run an Entity Framework migration chain there. That is proven against a LOCAL Postgres container with
an equivalently-restricted role - never against the hosted database. It is the first task of worker 1.

---

## 2. What is being moved

Sixteen tables, all currently in `gateway-stats.db` at SQLite schema version 5
(`src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs`), plus one JSON file.

| Cluster | Tables |
|---|---|
| Delta (append-only) | `stat_delta`, `token_delta`, `agent_delta`, `agent_driven_delta` |
| Identity (surrogate id to first-seen display spelling) | `repo_identity`, `agent_identity`, `model_identity`, `checkout_identity` |
| High-water (read-modify-write - the lost-update risk) | `session_highwater`, `token_highwater`, `agent_driven_highwater` |
| Membership (all-time distinct sets) | `wingman_session`, `repo_session`, `agent_session`, `agents_seeded` |
| Scalar | `meta` |

Plus `gateway-concurrency-stats.json` (`GatewaySessionConcurrencyStats`), which the brief's ruling 13
puts in this step: it is written from the hottest path in the system and carries a per-hour maximum,
which is a lost-update path of exactly the same shape as the high-water tables.

### A finding to carry, not to silently fix

`repo_session` and `agent_session` are the only two tables with **no tenant column**. Schema version 5
added the tenant to every other table - as a plain column on the delta and identity tables, and into
the PRIMARY KEY on the high-water, membership and meta tables - but those two were in neither list.

They are not un-partitioned in effect: `repo_id` and `agent_id` are surrogates minted per tenant (the
identity tables carry the tenant), so `(repo_id, session_id)` is partitioned INDIRECTLY through the
surrogate. That is a real invariant and it holds, but it holds by construction elsewhere rather than by
a column here. Step 2 carries the shape forward unchanged - changing it is a behaviour change outside
this step - and the contract suite asserts the indirect partitioning explicitly rather than skipping
those two tables on proof row 4. Named here so it is a recorded decision and not an omission.

---

## 3. The design, as settled

Not reopened. Recorded so every worker builds the same thing.

1. **A separate `GatewayStatsDbContext`.** Its own schema (`gateway_stats`), its own migration history
   table (`gateway_stats.__EFMigrationsHistory`), its own connection pool. Never folded into
   `GatewayDbContext`. Same physical Supabase server.
2. **One implementation, two providers.** Entity Framework carries the model, the migrations and every
   read projection, and it serves SQLite AND Postgres from the same code. The raw `SqliteConnection`
   in `GatewayStatsDatabase` goes away. This is what makes a provider-parametrised contract suite mean
   anything: it is one implementation run twice, not two implementations compared.
3. **Explicit `ON CONFLICT DO UPDATE` for every high-water and per-hour-maximum write.** Change-tracked
   read-modify-write is a lost-update generator under concurrent Postgres that single-writer SQLite
   never exposed. Both providers support the syntax, so the statements are shared where the dialects
   agree and provider-specific where they do not.
4. **The statistics migration and its connection failures are NON-FATAL to Gateway startup.** The
   Gateway boots, serves the roster and the tunnels, and the statistics surface reports itself
   unavailable with a named reason. This is a failure-domain boundary, not a fallback: there is no
   substitute store, no alternative path, and no invented data. The main `GatewayDatabase` keeps its
   current fatal-on-failure startup behaviour, which is correct and is not touched.
5. **The two migration chains never share a transaction or a startup gate.** The main one gates the
   deploy as it does today. The statistics one does not.
6. **Self-host keeps SQLite for statistics and that is correct.** The mission is no SQLite on the
   HOSTED Gateway.

### The one design decision this document makes: how the statistics store is selected

A new environment variable, **`CC_GATEWAY_STATS_DB_CONNECTION`**, mirroring
`CC_GATEWAY_DB_CONNECTION`. Set means Postgres for statistics; unset means the local SQLite file.

Why a second variable rather than reusing the first: Npgsql pools are keyed by the connection string,
so pointing both contexts at the identical string would put them in ONE pool and quietly delete the
pool separation that ruling 9 asked for. A distinct string - same server, its own application name and
its own pool size - is what makes the separate pool real rather than nominal. It also makes the
ruling-1 proof possible at all: pointing the statistics connection at a dead endpoint while the Gateway
serves a roster is a one-variable change.

The rule that goes with it, so it cannot decay into a fallback: **when
`CC_GATEWAY_DB_CONNECTION` is set (the Gateway is hosted) and `CC_GATEWAY_STATS_DB_CONNECTION` is not,
the statistics store is UNAVAILABLE with a named reason. It never opens a SQLite file.** That is the
no-SQLite guard doing its job on a misconfiguration, and the provisioning workflow sets the variable.

---

## 4. Worker assignments

Every piece is reviewed by a fresh Codex session before it is committed, told to be adversarial and not
to trust this Manager's report, and told to write its review to a file and reply with one single line.

**Wave 1 - the two things nothing else can start without.**

| Worker | Task | Proves |
|---|---|---|
| W1 | The local Postgres rig. A throwaway `postgres:16` container with a role holding ONLY `CREATE` on the database, and a proof that it can create `gateway_stats`, create tables in it, and run an Entity Framework migration chain with its own history table there. Extends the existing `CC_GATEWAY_TEST_PG_CONNECTION` gate used by `PostgresProviderProofTests`. | Verdict 3 ruling 3, the half that cannot be read off the hosted database |
| W2 | The model: `GatewayStatsDbContext`, sixteen entities, keys and indexes matching schema version 5 exactly, SQLite migration in `CcDirector.Gateway` and Postgres migration into `gateway_stats`. | Proof row 1, the schema half |

**Wave 2 - three independent ports against W2's entity contract, which is published before W2 finishes.**

| Worker | Task | Proves |
|---|---|---|
| W3 | The twelve read projections, ported to provider-neutral Entity Framework queries | Proof rows 1 and 6 |
| W4 | The write path: the fold batch commit, with explicit `ON CONFLICT DO UPDATE` on all three high-water tables and the four membership sets | Proof rows 2 and 3 |
| W5 | `gateway-concurrency-stats.json` onto the statistics context, per-hour maximum written as an upsert | Ruling 13, proof row 2 |

**Wave 3.**

| Worker | Task | Proves |
|---|---|---|
| W6 | Startup wiring, provider selection, the non-fatal boundary, the unavailable-with-reason state on `/stats/data`, and the provisioning workflow change | Verdict 3 rulings 1 and 2 |
| W7 | The no-SQLite guard on the hosted path, and the test that makes it trip | Proof row 8 |

**Wave 4.**

| Worker | Task | Proves |
|---|---|---|
| W8 | The provider-parametrised contract suite: all sixteen tables read and write, interleaved writers, idempotency on replay, tenant partitioning, boundaries, output parity on `/stats/data` bodies, and the suite watched going red against a deliberately broken implementation | Proof rows 1-7 |

### The one collision risk with Step 1, named up front

Step 1 (a different Manager, a different worktree) is building the statistics failure surface: the
bounded queue at the push ingress, the per-observer failure, drop, last-error and last-successful-write
counters, and the 503 on the statistics surface. Step 2's worker 6 needs that same surface for the
non-fatal startup boundary.

Step 2 does not wait on Step 1 and does not depend on it. Worker 6 builds its own boundary against the
same shape, and the two are reconciled when the Architect lands them. Recorded here so the overlap is a
known merge, not a surprise.
