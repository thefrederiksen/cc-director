# These migration identifiers may NEVER be renamed

A migration id is not a filename. **It is persisted data**, and it is part of the on-disk contract with every
statistics store that already exists.

When a store is migrated - or adopted - the id is written into its `__EFMigrationsHistory` table and stays
there for the life of that file. Renaming the migration does not rename the row. The next start reads a
history recording an id the chain no longer contains, concludes the store is not one of ours, and refuses it.
On PostgreSQL, where there is no adoption guard at all, it is worse: the renamed baseline simply looks
PENDING, and Entity Framework runs its full initial `Up()` against a schema that already exists.

**This is not hypothetical.** These two baselines were renamed once, by regenerating them to correct model
metadata. A review recreated the history state the previous revision had written over a genuine, healthy
version 5 store, and adoption rejected it - a healthy store condemned, which in this design is silent: the
Gateway serves fine, statistics are off, and the named reason is a lie. The ids were restored.

## The rule

- **Never rename a migration, and never regenerate one in place to change its id.** If model metadata is
  wrong, correct the model and regenerate the migration's `.Designer.cs` and the snapshot, then put the
  ORIGINAL id back on the file and the `[Migration("...")]` attribute.
- The current ids are `20260730160415_InitialGatewayStats` (SQLite, here) and
  `20260730161529_InitialGatewayStats` (PostgreSQL, in `CcDirector.Gateway.Migrations.Postgres/StatsMigrations`).
- A future migration gets a NEW id and must also bump `PRAGMA user_version` in its own `Up()`, with a
  matching reset in its `Down()`. That rule is enforced mechanically by
  `GatewayStatsSqliteVersionStampTests`, which checks every migration in the chain individually.

## Why a reader will be tempted anyway

Every test passes on a fresh database, because a fresh database has no history row to disagree with. The
breakage lands only on machines that ran an earlier build - which is to say, only on real users' machines and
never on ours. That asymmetry is the whole reason this file exists.

## The baseline's `Up()` is raw SQL on purpose

It is the literal schema version 5 data definition language, copied from `sqlite_master` in a database built
by RUNNING the shipped `GatewayStatsDatabase`. It carries scars that look like mistakes - added columns
hanging off a closing line, quoted table names, bare unnamed keys - because that is what the files being
adopted actually look like. Do not tidy it. `GatewayStatsSqliteBaselineEquivalenceTests` compares the two
databases structurally and will say so if it drifts.
