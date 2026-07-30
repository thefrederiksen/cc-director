# Self-host adoption of an existing statistics store: the evidence

**Who this is for.** A release seat cutting the authorised desktop build, who will not have the people who
wrote this available to ask. Everything needed to decide whether the self-host statistics store is safe is
here, including what is NOT proven.

**What changes for a self-host user.** The Gateway statistics store stops being a hand-rolled SQLite schema
and becomes an Entity Framework model with a migration chain, so that one implementation can serve SQLite on
the desktop and PostgreSQL on the hosted Gateway. The file on disk does not move, is not rebuilt, and loses
nothing. Self-host keeps SQLite, and that is correct - the mission is no SQLite on the HOSTED Gateway.

**Proof ledger rows this document carries:** 11, 12, 13 and 16.

---

## Item 5 first: the SQLite baseline is structurally the same database as a real version 5 file

**This item is numbered five and printed first, deliberately.** Adoption is only correct if this holds. The
mechanism in items one to four is what the Gateway DOES; this is the precondition that makes doing it honest,
and a reader who meets the mechanism first will have already accepted the claim before seeing what backs it.

### Why it is the precondition and not a detail

Adoption works by stamping the baseline migration as applied against a file the migration did not create.
That stamp is a CLAIM: it tells Entity Framework that the file on disk is what that baseline would have
produced. If the two shapes differ at all, the file has not been adopted - the framework has been told a lie
about it, and every later migration in the chain is then applied to a database that is not the one the chain
believes it is operating on.

That failure does not surface at open time, where the adoption code is. It surfaces later, on a real user's
machine, as a missing index or an absent constraint, with a stack trace pointing nowhere near adoption.

And no test starting from a fresh database can ever see it, because both sides of that comparison come from
the new chain and agree by construction. That is a guard supplying its own evidence. Only a comparison
against a database built by the OLD code can see it.

### What was found when the comparison was actually run

An independent review probed a generated baseline against a real file and found **four** structural
divergences at once:

| # | Divergence | Why it matters |
|---|---|---|
| 1 | `tenant` lost its `DEFAULT 'local'` on all eight delta and identity tables | Behavioural, not cosmetic - an insert that omits the tenant behaves differently |
| 2 | Rowid keys emitted as `INTEGER NOT NULL ... PRIMARY KEY AUTOINCREMENT` instead of the bare `INTEGER PRIMARY KEY AUTOINCREMENT` | `PRAGMA table_info.notnull` reads 1 against the real file's 0 |
| 3 | `PRAGMA user_version` left at 0 instead of 5 | See item 16 below - this one is a crash on a rolled-back desktop build |
| 4 | All sixteen primary key constraints NAMED (`PK_agent_delta` and so on) where version 5 names none | SQLite stores those names in `sqlite_master` |

Four found by one probe is the argument against fixing them one at a time. Nudging a migration builder until
it matches makes it match **the shapes somebody happened to check**, and the next divergence is the one
nobody listed.

### The fix: the baseline IS the version 5 data definition language

`Stats/Data/Migrations/20260730160415_InitialGatewayStats.cs` `Up()` is raw SQL, copied verbatim from the
`sql` column of `sqlite_master` in a database built by RUNNING the shipped `GatewayStatsDatabase`. Equivalence
is therefore true BY CONSTRUCTION rather than by careful matching.

The text carries scars that look like mistakes and are not. They are commented in the file, and must not be
tidied:

- Eight tables end with their added columns hanging off the closing line
  (`, model_id INTEGER, checkout_id INTEGER, tenant TEXT NOT NULL DEFAULT 'local')`). That is what SQLite's
  `ALTER TABLE ADD COLUMN` does to the stored statement, and version 5 reached those columns by `ALTER`.
- Six tables have QUOTED names - the ones version 5 rebuilt to put the tenant into their primary key.
  `ALTER TABLE ... RENAME TO` rewrites the stored name in quotes.
- Rowid keys are a bare `INTEGER PRIMARY KEY AUTOINCREMENT`, and no key carries a constraint name.

The model snapshot is unchanged. Entity Framework compares the model to the snapshot, not to the database, so
`has-pending-model-changes` stays green; the structural diff is what guards the model against the data
definition language.

### The comparison, and that it detects

`GatewayStatsSqliteBaselineEquivalenceTests` builds database A by running `GatewayStatsDatabase` and database
B by running the baseline against an empty file, then compares them two ways:

- **Stored text** - the `CREATE` statement SQLite itself kept for every table and index, normalised for
  WHITESPACE ONLY. Quoting, casing and bracketing are deliberately NOT normalised: each would be a
  difference the test then could not see, and seeing differences is the entire job. Formatting is made equal
  by writing the literal data definition language, not by teaching the comparison to look away.
- **Structure, from the PRAGMAs** - per table, every column in declaration ORDER with type, NULLABILITY,
  default and primary key POSITION; and every index by NAME with its UNIQUENESS, origin and column order.
  Automatically-created indexes are included, because their names encode how the key was declared.

Both compare object by object, so a failure NAMES the object that diverged.

**Two exclusions, both Entity Framework's own bookkeeping and nothing else:** `__EFMigrationsHistory`
(creating it is precisely what adoption ADDS) and `__EFMigrationsLock` (the advisory row that stops two
processes migrating at once). Neither is part of the statistics schema and neither exists in a hand-rolled
store.

The lock table was NOT anticipated - it was what running the comparison actually reported, and it would
otherwise have read as a divergence. That is the argument for running the comparison rather than reasoning
about it. Nothing else may be added to that list: every other difference is a defect in the baseline, and the
fix is to make the baseline reproduce version 5, not to widen the exclusions until the test goes quiet.

**Result with the literal data definition language in place: no divergence.** All sixteen tables, the four
indexes, every automatically-created index, column order, types, nullability, defaults, primary key column
order, and `PRAGMA user_version` all match a database built by running `GatewayStatsDatabase`.

**Scope: SQLITE ONLY.** Postgres starts empty on the hosted Gateway. There is no existing file for it to be
equivalent to, so the problem cannot arise there and this is not made symmetric.

---

## Item 16: a fresh file stamps `PRAGMA user_version = 5`, so an older build refuses instead of crashing

Divergence 3 above is its own ledger row because its failure mode is a crash on a user's machine, by a route
nobody was watching.

The shipped hand-rolled code already fails correctly on a file whose version exceeds its build: it refuses,
loudly, naming the problem. That refusal is the safety net for a **desktop rollback**, which is a real event
now that desktop releases are authorised.

A file left at `user_version = 0` defeats it. The old build reads 0, concludes the store predates every
migration, and runs its version 1 through 5 steps against tables that already exist - dying on a duplicate
`ALTER TABLE`. Stamping 5 converts that crash back into the clean refusal the original author designed.

**The rule that goes with it: any future migration in this chain must BUMP the stamp.**

---

## Item 13: that rule is enforced mechanically, not remembered

A rule somebody has to remember is the shape this mission has rejected more than once.
`GatewayStatsSqliteVersionStampTests` applies the chain to a fresh file, reads `PRAGMA user_version` back, and
asserts it equals **4 plus the number of migrations in the chain**, read off the assembly.

**The 4 is not an off-by-one.** It encodes that the baseline COLLAPSES schema versions 1 through 5 into a
single migration - a store on disk is only ever at version 5, and versions 1 to 4 are history no live file is
sitting in. So one migration corresponds to five schema versions: four already collapsed, plus one per
migration. That sentence is in the test beside the constant, because without it the next reader will
eventually decide the 4 is a bug and helpfully correct it, and the correction will look reasonable and be
silent.

The expected value is DERIVED FROM THE CHAIN, never held as a constant - a constant is the same forgettable
rule wearing a different hat. The failure message says what is wrong in words ("a migration was added to the
chain without moving the version stamp") rather than leaving a bare assertion failure to be puzzled over in
eighteen months.

---

## Item 1: the state a self-host store is actually in

Every self-host user who has ever opened the statistics page has a `gateway-stats.db` written by the
hand-rolled path: **sixteen tables, `PRAGMA user_version` 5, and NO `__EFMigrationsHistory` table**, because
that path never used Entity Framework.

Point a migration chain at that file and the baseline tries to CREATE sixteen tables that already exist. It
fails on the first one.

## Item 2: what adoption does

Adoption, not retirement and not a fallback. The rows in that file are already the right shape - the model
was ported from that exact schema, table name for table name and column name for column name, precisely so
this would be true. The only thing missing is the bookkeeping that says so.

`GatewayStatsSqliteAdoption.Adopt` creates the history table and stamps the baseline as applied; the chain
then proceeds normally. **No row is read, written or moved.** The two statements come from Entity Framework's
own history repository rather than being hand-written, so they cannot drift from what the framework itself
would produce, and they run in ONE transaction - a crash between them would leave a history table with no
baseline row, which is worse than the starting state, because the chain would then decide the baseline is
pending and try the sixteen creates again.

The step is explicit, named and logged. A store silently reshaping itself on startup is how numbers disappear
without anybody knowing which build did it.

## Item 3: what is refused, and that a refusal does not take the Gateway down

Adoption is a claim that the file already matches the baseline, so BOTH halves of that claim are checked: the
version stamp, AND that every table the model expects is really present. The expected set is read off the
model rather than written out as a list, so it cannot drift from the schema the baseline creates.

| Store state | Outcome |
|---|---|
| No file, or an empty file | `FreshStore` - the chain creates the schema |
| History table already present | `AlreadyTracked` - the steady state on every later startup |
| Version 5, all sixteen tables | `Adopted` |
| Any other `user_version` | `NotAdoptable`, reason `IncompatibleSchemaVersion`, file untouched |
| Tables present but not this store's | `NotAdoptable`, reason `NotAStatisticsStore`, file untouched |
| Unreadable, locked or corrupt | `NotAdoptable`, reason `StoreUnreadable` |

**FAIL LOUD IS NOT FAIL FATAL.** Nothing about a user's FILE throws. A refusal comes back as a result
carrying a NAMED reason, so the Gateway still starts and still serves its roster with the statistics surface
off and the operator told which case it is. This mission exists because a statistics fault took the primary
read path down for 32 minutes on the hosted Gateway; a version check that bricks a working desktop Gateway
would be that same incident on the other surface.

That is not a fallback and must not decay into one: there is no substitute store, no alternative path and no
invented data. The statistics surface is simply off, loudly, with the reason named.

A caller handing the step a context that is not on SQLite is a PROGRAMMING error rather than a user state,
and that does throw. The split is deliberate and is pinned by a test.

The main `GatewayDatabase` keeps its fatal-on-failure startup behaviour, which is correct for the database
that carries the roster, and is untouched.

## Item 4: the fixture route, and the failure watched first

**Fixture route: every version 5 fixture is built by RUNNING the shipped `GatewayStatsDatabase`** - never by
hand-writing what a version 5 file is believed to look like, and never by generating one from the new model's
understanding of the old schema. A fixture synthesised from the new code's own understanding passes just as
happily when the fixture and the code are wrong together, and would prove nothing about the installs this
work exists to protect. Stated explicitly so nobody reads a green suite as a schema proof it is not.

**The failure is watched first.** `Migrate_Version5Store_WithoutAdoption_FailsOnTablesThatAlreadyExist` runs
the chain against a real version 5 file with NO adoption and pins the error. Observed:

```
SQLite Error 1: 'table "stat_delta" already exists'
```

It dies on `stat_delta` because the baseline creates the tables in the order version 5's own steps introduced
them, and never reaches the fifteen behind it. Without this test the adoption tests could pass for a reason
having nothing to do with adoption; it is what proves the step is load-bearing, and it turns red the day
somebody deletes it.

`Adopt_RealVersion5Store_KeepsEveryRowItAlreadyHeld` reads rows WRITTEN BY THE OLD CODE back THROUGH THE NEW
ENTITIES, column by column. That is the port's central claim, and it is currently the only place the entity
mapping meets the real on-disk shape - a wrong `ToTable` or `HasColumnName` throws there rather than passing
quietly.

**Both fixtures are guarded against being unable to fail**, because the author of a fixture is the last
person able to see the gap in it:

- The seeded rows make EVERY column distinguishable from every other. An earlier version set `is_voice` and
  `wingman` both to 0 and `model_id` and `checkout_id` both to null - so a mapping that crossed either pair
  would have read back correct and every assertion would have passed with the defect in the model. The two
  booleans now differ, the two nullable surrogates carry different non-null numbers, the counts differ, and a
  second row keeps the null case covered.
- The structural comparison refuses to report equivalence unless the hand-rolled fixture is shown to hold the
  sixteen tables and four named indexes first. Two empty dictionaries compare equal, so a fixture that failed
  to build - or an exclusion list that swallowed the schema - would otherwise report equivalence between two
  databases it never looked at. That is the most comfortable false green available here.

---

## What is NOT proven here

- **The sixteen-table contract suite** (ledger rows 2 to 8) is worker 8's, not this document's. Nothing here
  claims read or write correctness across the store - only that the schema matches and the file is adopted
  without loss.
- **Startup wiring.** This work provides the adoption step and its result type. Consuming that result,
  surfacing the named reason, and keeping the failure non-fatal at the boundary are worker 6's (ledger row
  10). The named reasons defined here will need mapping onto the Step 1 failure surface's vocabulary.
- **Postgres has no adoption path and needs none** - it starts empty on hosted. The structural diff is
  SQLite only for the same reason.
- **A live downgrade has not been exercised end to end.** The stamp is proven to be written and to equal what
  the shipped code writes; that an older BUILD then produces its refusal rests on that build's existing,
  already-shipped version check rather than on a test run here.
- **An INTERRUPTED first migration is not recovered, and is not contained by the adoption step.** The step
  treats the presence of `__EFMigrationsHistory` as meaning the store is tracked, and returns `AlreadyTracked`
  without inspecting which migrations that table records. If a first `Migrate()` ever died partway - after
  Entity Framework created the history table but before the baseline was recorded - the store would come back
  with an empty history beside tables that already exist, this step would report it usable, and the chain
  would then fail on `table "stat_delta" already exists` OUTSIDE the step's containment.

  Adoption itself cannot produce that state: it creates the history table and stamps the baseline in ONE
  transaction, so the pair is all-or-nothing. The state is reachable only through an interrupted Entity
  Framework migration, which is the ordinary interrupted-migration failure for any database on this layer
  rather than anything specific to adoption, and recovering from it would mean partial-migration repair -
  well outside this work. It is named here rather than left silent because "the store has a history table"
  and "the store is at the baseline" are not the same claim, and this step currently only checks the first.

- **THE WHOLE SOLUTION HAS NEVER BEEN BUILT OR TESTED FOR THIS WORK, and no automated check covers it.**
  `.github/workflows/ci.yml` fires only on a push to `main` and on pull requests whose base is `main`. This
  work sits on a worker branch merging into the mission branch `nosqlite-stats`, which matches neither, and
  `gh run list` returns zero runs for either branch. The evidence above comes from targeted local runs of the
  statistics classes only. Do not read "the tests pass" as "the suite is green" - that signal does not exist
  yet for this mission, and it will not until a pull request against `main` makes the trigger fire.
