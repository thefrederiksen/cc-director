# Step 2, worker 5: the concurrency store off the shared file and into the statistics database

What was built, what was proved, what was NOT proved, and the one limitation that was carried forward
deliberately. Branch `nosqlite-stats-w5-concurrency`.

## Why this was in scope

`gateway-concurrency-stats.json` is not a tidy-up item. It was rewritten IN FULL, atomically, on every
`/sessions` read on which anything changed - the hottest path in the system - by both of the containers a
slot swap runs. It was 53 KB and was being written through the same window in which a database on that same
Azure Files share was corrupted and the hosted Gateway answered HTTP 500 to every client for 32 minutes. It
is corruptible by exactly that two-writer race. It simply fails later, and with a parse error instead of a
malformed-image error.

## What was built

Three tables, exactly as the entity contract specifies, in `CcDirector.Gateway.Stats.Data`:

| Table | Key | Holds |
|---|---|---|
| `concurrency_peak` | `tenant` | the two all-time peaks and the instant each was set |
| `concurrency_hour` | `(tenant, hour_utc)` | per-hour maxima and the three distinct counts |
| `concurrency_hour_member` | `(tenant, hour_utc, kind, member_id)` | the raw members of each hour's sets |

`GatewaySessionConcurrencyStore` is the port. Its public surface is the same two methods as the JSON store
(`Observe`, `Snapshot`) so the wave 3 wiring is a substitution.

### The four properties that had to survive, and where each lives

1. **The two CURRENT values stay runtime-only.** They are in the in-memory shadow and in no table. The JSON
   file had no field for them either; persisting them would let a restarted container report a "right now"
   number inherited from a dead process.
2. **Every maximum is an upsert.** Nine columns - four on `concurrency_peak` (two maxima and their two
   timestamps) and five on `concurrency_hour` - are written with an explicit
   `ON CONFLICT ... DO UPDATE SET x = GREATEST(excluded.x, table.x)`, never a change-tracked read-then-save.
   Each timestamp moves through a `CASE` so it advances only on the write where its own maximum advanced.
   (The mission brief said eleven columns; it is nine. The contract was right and the brief miscounted. The
   Manager has confirmed the correction.)
3. **The dedup sets stay in memory with their comparers** - `Ordinal` for session ids,
   `OrdinalIgnoreCase` for machines and repositories. `concurrency_hour_member` stores raw strings and is
   only how the sets survive a restart. Its key is ordinal, so it may legally hold two spellings of one
   machine; that is harmless because they are rehydrated into the set that collapses them, and the
   reasoning is written into `ConcurrencyHourMemberEntity` so nobody reaches for a case-insensitive column
   or a `citext`.
4. **Retention is 90 days, pruned on write, and member rows prune with their hour.**

### One thing worker 2 and the contract should know: the member table is CURRENT-HOUR-ONLY

Found by review 3, and it is a behaviour difference rather than a test gap. Observe hour H, then H+1, then
H again: the file store reports ONE distinct session for H, because it clears its three dedup lists whenever
the observed hour DIFFERS from its single current-hour key - and "differs" fires when the hour moves
backwards as much as forwards. A store that keeps every hour's members and unions them reports two. Two is
the better answer and the wrong one for a port; if we want the union we ask for it as a change on its own
merits, where the owner can see a number move and be told why.

So `concurrency_hour_member` now holds the CURRENT hour and nothing else: when a tenant's hour changes, in
either direction, its rows for every other hour are discarded. That is exactly what the three lists in the
JSON file were. The contract describes the table as pruning with its hour at 90 days; that prune still runs
and is not redundant - it is what eventually clears the last hour of a tenant that stopped being observed -
but the table's steady-state size is one hour per tenant, not ninety days of them. Flagged rather than
assumed, because it is a lifetime the contract does not currently spell out.

The only dialect difference in the store is `GREATEST` (PostgreSQL) versus `MAX` (SQLite). A third provider
fails loud rather than being guessed at. Table names come from the mapped model, so a rename cannot leave a
statement pointing at a name that no longer exists.

## What was proved

**Output parity with the JSON store, on the rendered page.** One fixture is driven through both
implementations observation for observation, and what is compared is the JSON body `/stats/data` serves for
its `concurrency` property, serialized with the same web defaults minimal APIs use. That is a different and
stronger claim than "the same numbers are stored": a difference in a null timestamp, a `DateTime` Kind, the
order of the hourly list, or an hour bucket one store creates and the other does not, shows up here and in
no row-by-row comparison. The fixture covers two tenants, hour and day rolls, the seven-day weekly window,
the ninety-day retention boundary, empty rosters, exited sessions, sessions with no machine or repository,
and one machine under two spellings - before and after restarting both stores.

**The parity comparison can fail.** The two stores are deliberately driven apart by one extra observation
and the comparison must notice. Without that, every parity green would also be consistent with a renderer
returning a constant.

**The lost update.** Two containers, one store, one hour bucket and one peak: both hold a picture that says
five, one observes eight and the other seven, and their writes interleave. The interleaving and the
assertion live in one place and are run against both implementations. Against the upserts the eight
survives and its timestamp is the instant that actually set it. Against a deliberately change-tracked
read-modify-write writer the SAME assertion fails - watched directly, `Expected: 8, Actual: 7` at the peak
assertion - and that failure is now asserted in the committed suite, so the proof is not a claim about a red
somebody saw once on their own machine. If the naive writer were ever "fixed", that test turns red and says
so.

**The failure direction.** A write that fails puts back the members the fold had already added to the dedup
sets, so their rows are not lost for the rest of the hour; and a store pointed at a statistics file that
predates its tables throws and names `concurrency_peak` rather than quietly charting zeroes.

## What was NOT proved - read this before treating the suite as a schema proof

**The fixtures are MODEL-BUILT (`EnsureCreated`), not migration-built.** A fixture built from the model
cannot detect the model drifting from a hand-written schema, because the model and the fixture agree by
construction: the test passes exactly as happily when both are wrong together. This matters on this step
because worker 2's SQLite baseline is the literal schema version 5 DDL rather than generated from the model,
so the two can drift, and the drift would surface as a query error on a self-host user's machine rather than
as a migration error on ours.

For these three tables that is currently harmless, and it was MEASURED rather than assumed:
`ConcurrencyTablesAreAdditiveTests` runs the real `GatewayStatsDatabase`, reads `sqlite_master` off the file
it produces, and shows that none of `concurrency_peak`, `concurrency_hour` or `concurrency_hour_member`
exists at version 5 in any spelling - the concurrency record was a JSON file and was never a table. So there
is no on-disk shape for the model to drift from, and the migration that creates these tables will be their
first authority.

**When worker 2's migration lands, these fixtures must be rebuilt on it.** Until then this suite is not a
schema proof and this section must keep saying so. No migration was generated from this slice on purpose: a
migration describes the whole model, and one scaffolded from three tables would be discarded the moment the
other sixteen arrive.

## Known limitation, carried forward deliberately

**Two containers folding the same hour keep SEPARATE in-memory dedup sets**, and each rehydrates only when
the hour rolls or it first touches the tenant. So within one hour each can write a distinct count below the
true union of what both containers saw, and the stored count is the larger of the two rather than the count
of the union.

This is the JSON store's own behaviour, and it is strictly better than the whole-file last-writer-wins
clobber it replaces. It is recorded here rather than fixed because the obvious fix - counting rows in
`concurrency_hour_member` - would hand the decision "are these two machine names the same machine" to the
database's collation, which is not equivalent to the `OrdinalIgnoreCase` comparer that decides it today.
The Manager ruled: carry it forward unchanged and write it down, because an under-count nobody has recorded
reads as a bug to the next person who notices it, and they will "fix" it by counting rows.

## A PostgreSQL result that is real but narrow, and must not be read as more

`docs/step2-w5-pg-statement-probe.sql` was run against the rig with `psql`, using `PREPARE` with the same
parameter types Npgsql infers. Actual output, not a reading of it:

- first write where live peaked and working did not: `live_max 5 | 2026-07-11 20:00:00+00 | working_max 0 |
  (null)` - the `CASE`-with-`NULL` types correctly and no instant is invented;
- the race with the LOWER value landing last: `live_max 8 | 2026-07-11 20:02:00+00` - `GREATEST` holds eight
  and the stamp is the write that SET it, not the later one;
- a write advancing only working: `8 | 20:02 | 9 | 21:00` - the two timestamps move independently;
- all five per-hour columns as maxima: `8 | 2 | 9 | 2 | 1`;
- `ON CONFLICT DO NOTHING` inserted 0 on a repeat, and the ordinal key held both `SOREN_NORTH` and
  `Soren_North` as two rows;
- the hour keys sorted chronologically and the prune text range deleted exactly the two stale hours.

**What it is not.** It is NOT a run of the store: no C# path, no Npgsql parameter inference, no
model-to-table mapping, no store logic, and it ran as the superuser in a throwaway schema, so it says
nothing about the restricted role either. It removes the "does this SQL run on PostgreSQL at all" risk and
removes nothing else.

## Outstanding - all of it still owed, none of it closed

1. **The PostgreSQL arms of both proofs.** Written and gated on `CC_GATEWAY_TEST_PG_STATS_CONNECTION`,
   pointed at the per-caller rig (`scripts/pg-stats-proof-rig.ps1`, instance `w5`, port 55435), whose login
   role holds exactly the hosted role's measured grants. **NOT RUN.** Three attempts produced no result: one
   died at a ten-minute tool timeout (killed, exit 143), two never reached a test while the fleet suite lock
   was held elsewhere. A killed run and a queued run are the absence of an answer, not a verdict, and the
   earlier per-class SQLite greens are not a substitute for them.
2. **The product-code mutation for the lost update.** `docs/step2-w5-mutate-product-to-read-modify-write.py`
   replaces the store's own upserts with a change-tracked read-then-save. It is written and NOT RUN. Note
   which test judges it: the permanent red in the suite uses a fake writer, which proves the ASSERTION can
   fail but not that the SHIPPED upserts are what makes it pass. The product mutation must be judged by the
   THREADED four-container test, not the deterministic race - this store decides whether to write from its
   in-memory shadow rather than from a database read, so a read-then-save would re-read at write time, see
   the other container's eight, and correctly decline to write seven. The lost update for a read-then-save
   lives between ITS read and ITS write, and only genuine concurrency opens that window.
3. **The SQLite migration for these three tables, and rebuilding the fixtures on it.** Ownership was settled
   late and it is MINE, not worker 2's: worker 2 carries none of these three tables. What is left is to
   rebase onto `origin/nosqlite-stats-w2-model` (literal version 5 DDL baseline, `e0c401b50`), add the three
   `DbSet`s to that context through the single `ConcurrencyStatsModel.Configure` call, generate the SECOND
   migration in the SQLite chain, and bump `PRAGMA user_version` from 5 to 6 in its `Up()` with a matching
   reset in `Down()` - worker 2's row-13 test derives the expected stamp from the migration count, so a
   second migration without the bump turns it red and says so.

   A rebase was attempted and ABORTED rather than left half-finished. It produced exactly one conflict, an
   add/add on `GatewayStatsDbContext.cs`, and the resolution is mechanical: keep worker 2's file whole, add
   the three concurrency `DbSet`s after `Meta`, and call `ConcurrencyStatsModel.Configure(modelBuilder)`
   inside `OnModelCreating` BEFORE the `if (Database.IsNpgsql())` block - before it, so the three tables'
   text columns also get the `"C"` collation that block pins, which is what makes the retention text range
   compare identically on both providers. Nothing was lost by aborting; every commit is on the remote.

None of this depends on a signal that does not exist: no claim here rests on continuous integration, which
has never run on this worker branch.

## Merge notes for worker 2

- The three `DbSet`s sit behind one `ConcurrencyStatsModel.Configure(modelBuilder)` call, so folding this
  slice into the sixteen-table context is three lines and one call.
- The store names its tenant explicitly in every predicate and does not rely on a global query filter.
- `GatewayStatsDbContext` here declares the `gateway_stats` default schema on Npgsql only.
