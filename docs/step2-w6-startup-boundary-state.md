# Step 2, worker 6: the startup boundary - current state

Branch `nosqlite-stats-w6-startup`, worktree `D:\ReposFred\dt-nosqlite-w6`, cut from worker 2's branch. Rows
10 and 15 stay OPEN. This file records exactly what is built, what is NOT proven, and what the next seat
picks up.

**Nothing in this file is a proof. Read the "what is not proven" section before quoting anything from it.**
Every test named here is written and unrun; the only standing fact about this branch is that it compiles, and
a compile is evidence about types and about nothing else.

History: the first worker 6 built the boundary and was parked when the mission cut to three concurrent
workers; its one queued run produced no output against the fleet-wide test lock and was stopped, which is NO
RESULT and closed nothing. The seat was re-seated on 2026-07-30 to run the proofs. Since re-seating, this
document has gained the mid-chain answer, the `IncompleteSchema` reason and the abandon-not-cancel
limitation - all still unrun.

---

## What is built

### The failure-domain boundary

`src/CcDirector.Gateway/Stats/Data/GatewayStatsStore.cs`. Owns the statistics context's provider selection,
its connection and its migration chain, and contains every failure in all three. It never throws for a
configuration or a database problem - it reports one, with a named reason, and the Gateway starts.

It is a boundary and not a fallback, and the distinction is written into the type: there is no substitute
store, no alternative path and no invented data. `Factory` is NULLABLE so a consumer cannot reach a context
without having decided what to do when there is not one, and `CreateContext()` on an unavailable store
throws with the named reason rather than handing back a context over something else.

The containment is on the CLOCK as well as on the exception (`OpenDeadline`, twenty seconds). "Non-fatal" is
not the same as "harmless": a hosted Gateway has a platform startup deadline sitting in front of the port
bind, so a statistics database that accepts a connection and then never answers would take the site down
just as completely as one that threw, and would do it without a single exception in the log. On the timeout
the attempt is ABANDONED rather than cancelled - a migration in flight must not be torn out from under
itself - and its provider is released by a continuation once it finally settles.

The main `GatewayDatabase` is untouched and keeps its fatal-on-failure startup behaviour, which is correct:
it carries the roster, and a Gateway that came up without it would serve wrong answers rather than none.

### The connection choice

`src/CcDirector.Gateway/Stats/Data/StatsConnectionSelection.cs`, pure and argument-driven so the decision can
be tested without a process-wide side effect.

- `CC_GATEWAY_STATS_DB_CONNECTION` set - wins outright, nothing is derived.
- Set but BLANK - NOT CONFIGURED. A real operator error, never read as unset.
- Unset, and `CC_GATEWAY_DB_CONNECTION` names PostgreSQL - DERIVED, with application name `gateway-stats`
  and its own pool (minimum 0, maximum 8).
- Unset, nothing to derive from, hosted - NOT CONFIGURED. Never a SQLite file, under any circumstance.
- Unset, nothing to derive from, self-host - the local statistics file, unchanged.

Derived rather than reused because Npgsql keys its connection POOLS by the connection string: an identical
string would collapse both contexts into ONE pool and delete the separation the design rests on, while
looking perfectly separated in the code. The database name is never derived - the builder starts FROM the
Gateway's own string and writes only the pooling keys, so it is carried unaltered by construction rather
than by a rule somebody has to keep following.

Note for whoever reads `docs/step2-nosqlite-stats-plan.md` section 3: its original text said "hosted and
unset means UNAVAILABLE". That was superseded by the Architect's derivation ruling and the Manager has
marked it superseded in place. NOT CONFIGURED still exists and still matters; it no longer covers
"hosted and unset".

### The three named reasons

`StatsStoreUnavailableReason` gained `NotConfigured`, `Unreachable` and `IncompleteSchema` beside worker 2's
four adoption reasons. One enum for the whole statistics availability surface, and a stable machine-readable
code per reason (`not_configured`, `unreachable`, `incomplete_schema`, ...) written out by hand rather than
derived from the enum member name, so renaming a member in C# cannot silently change a string an operator
greps for.

The rule the three of them encode, written down so a fourth is added on the same grounds rather than on
taste: **a named reason exists to separate causes that are FIXED IN DIFFERENT PLACES.** NOT CONFIGURED is
fixed by editing a setting, UNREACHABLE by fixing a database or a network, and INCOMPLETE SCHEMA on the
store's own disk with both of those already healthy. Collapsing any pair costs an incident spent looking
where the fault is not.

`IncompleteSchema` is the Manager's ruling of 2026-07-30, and it was made because the first version of this
branch got it wrong in a way that mattered: a half-built store was contained as UNREACHABLE, whose sentence
says "this is a database or network problem rather than a missing setting" - true of a refused connection and
actively misleading here, where the database is up, the network is fine, the settings are right, and the
fault is sitting on disk. A reason that sends the first responder to check three healthy things is worse than
no reason at all. It is named for the STATE and not for its cause, because the state is what the person
fixing it acts on, and because no code running afterwards can tell whether the process died of power loss, an
eviction mid-deploy or an operator stopping the service - claiming one would be inventing detail.

### The mid-chain question, answered: what containment can and cannot do

The Manager asked whether the containment can cover a `Migrate` that throws MID-CHAIN, part-applied, because
ledger row 18 was held out of Step 2 conditional on exactly that answer. The answer has two halves and only
the first one is comfortable.

**It covers the THROW.** `Migrate()` is called inside `OpenAndMigrate`, which runs inside the `Task` the
constructor awaits and unwraps with `GetAwaiter().GetResult()`, inside the `try` whose catch is the boundary.
A throw from migration three of five arrives there exactly like a refused connection, and the Gateway starts
with statistics unavailable and a named reason. There is no other place in the product that calls `Migrate`
on the statistics context - every call site was checked, not assumed.

**It CANNOT prevent the part-applied STATE, and that is the half that decides the row.** That state is made
by a process that DIES mid-migration, and a try-catch catches nothing once the process is gone. No boundary
placed anywhere inside this program can stop a machine losing power between two statements. What containment
buys is that the NEXT startup over that state is SURVIVABLE: the Gateway starts, serves its roster, and names
what is wrong. That is why row 18 stays out of Step 2 rather than blocking it - containment makes the state
survivable, it does not make it impossible, and nothing here should be read as claiming otherwise.

It is also not self-healing. Every subsequent startup takes the same path and reports the same thing, so a
half-built store is a permanent statistics outage until a human acts. Repair is deliberately not attempted:
it means deciding what to do about tables holding somebody's numbers, and a startup path that quietly
reshapes a store is how numbers disappear without anybody knowing which build did it.

The state is now DIAGNOSED before it is walked into, rather than caught afterwards and mis-named - see the
three named reasons above. The diagnosis is not a second boundary: if it ever fails to spot the state, the
chain throws and the catch still contains it. It only decides which reason is reported.

### LIMITATION: at twenty seconds the attempt is ABANDONED, not cancelled

Named here because it is deliberate and was undocumented, and undocumented is the part that is not fine.

When the open exceeds `OpenDeadline` the Gateway stops WAITING for the attempt. It does not stop the
attempt. The task keeps running against the database - a migration in flight must not be torn out from under
itself, which is how you manufacture the half-built schema described above - and its provider is released by
a continuation once it finally settles.

**The consequence, spelled out.** A hung migration is still running against the database after the Gateway
has declared statistics unreachable and begun serving. If that Gateway is then restarted, the restart's own
migration attempt can be running ALONGSIDE the first one that never died. On PostgreSQL the migration lock
serialises them, so the second waits rather than interleaving; the concern is not corruption so much as a
second process holding a connection and a lock nobody knows is held, and an operator reading "unreachable"
who has no way to see that a migration from the previous boot is still in progress. It is not measured and
it is not tested. The Manager has this as a separate finding and will decide whether it becomes its own
issue.

### The Step 1 shape, depended on and not rebuilt

`src/CcDirector.Gateway/Stats/StatsFailureState.cs` - `IStatsFailureState` carrying exactly the four things
Step 1's surface exposes: failure count, drop count, last error, last successful write. There is deliberately
NO endpoint wiring on this branch. If Step 1 lands a different spelling, adapting is a rename.

A drop and a failure are counted separately on purpose: refusing to attempt a write is the CORRECT behaviour
when the store is down, and it must be visible as its own number rather than looking either like a failure
storm or like nothing happening at all.

### The wiring

`GatewayHost` constructs the boundary on its own, deliberately outside the main database's construction and
outside anything that gates startup, and registers it. `SessionConcurrency` is now NULLABLE and is NOT
constructed on a hosted Gateway, so `gateway-concurrency-stats.json` is never written there. Both existing
consumers were already null-tolerant (`concurrency?.Observe`, `concurrency?.Snapshot`), so an absent recorder
is an absent series - never a zero and never an exception on the roster path.

**The seam for worker 5.** `GatewayStatsStore.Factory` is the pooled `IDbContextFactory<GatewayStatsDbContext>`
that `GatewaySessionConcurrencyStore` takes. At merge the Architect changes the property type and constructs
`new GatewaySessionConcurrencyStore(statsStore.Factory)` when the store is available. Worker 5's branch was
deliberately NOT merged here - it carries its own copy of `GatewayStatsDbContext.cs` that conflicts with
worker 2's, and that resolution is the Architect's, done once by the person holding all three branches.

---

## WHAT IS NOT PROVEN - rows 10 and 15 are OPEN

**No test in this branch has been RUN.** One narrow run was queued against the fleet test lock, produced no
output, and was stopped at stand-down. That is NO RESULT: it is not a pass and it is not a failure, and it
closes nothing.

What DOES stand: the Gateway project and the test project both COMPILE (`dotnet build`, succeeded). A
compile is evidence about types, and about nothing else here.

So all three of the following are written and unexecuted:

| Row | File | What it would prove | State |
|---|---|---|---|
| 10 | `GatewayStartsWithStatisticsUnreachableTests.HostedGateway_StartsAndServesARoster_WithTheStatisticsDatabaseUnreachable` | The Gateway starts and `GET /sessions` answers 200 with a real roster body while the statistics store reports UNREACHABLE | **OPEN - written, never run** |
| 10 (failing direction) | `GatewayStatsStoreContainmentTests.TheSameFault_IsFatal_WhenItIsNotContained` | The SAME connection throws when nothing contains it, so the containment arm is not passing against a fault that never happened | **OPEN - written, never run. The Gateway has NOT been watched refusing to start.** |
| 10 (the ruling) | `GatewayStatsStoreContainmentTests.NotConfiguredAndUnreachable_AreDifferentNamedReasons` | The two reasons are produced side by side and DIFFER - enum, code and sentence | **OPEN - written, never run** |
| 15 | `GatewayStartsWithStatisticsUnreachableTests.ConcurrencyStatisticsFile_IsNeverWrittenOnTheHostedPath_AndIsWrittenOnSelfHost` | Nothing writes `gateway-concurrency-stats.json` on the hosted path, with the self-host control that DOES write it | **OPEN - written, never run** |
| 18 | `GatewayStatsStoreMidChainContainmentTests.TheHalfBuiltStore_IsFatal_WhenItIsNotContained` | The half-built store really does kill an uncontained migration, naming the table | **OPEN - written, never run** |
| 18 | `GatewayStatsStoreMidChainContainmentTests.HalfBuiltStore_IsContained_AndReportsIncompleteSchema` | The same store through the boundary does not throw and reports INCOMPLETE SCHEMA, pointing at the disk and explicitly not at the network | **OPEN - written, never run** |
| 18 | `GatewayStatsStoreMidChainContainmentTests.ContainedOpen_ChangesNothingOnDisk` | A contained open is not a quiet repair - seeded row, table count, version stamp and empty history all unchanged | **OPEN - written, never run** |
| 18 | `GatewayStatsStoreMidChainContainmentTests.TheThreeReasons_AreAllDifferentFromEachOther` | All three reasons produced side by side and different PAIRWISE - enum, code and sentence | **OPEN - written, never run** |
| 18 | `GatewayStatsStoreMidChainContainmentTests.HealthyStores_AreNotReportedAsIncomplete` | The diagnosis's OTHER failure direction - a fresh store and a fully migrated one are not condemned | **OPEN - written, never run** |

### How these fixtures were built to be able to fail

Recorded so the next seat can check the shape rather than re-deriving it, and so a green - when there is one
- is worth something:

- The half-built store is built by RUNNING THE REAL OLD CODE - a genuine `GatewayStatsDatabase` creates the
  sixteen tables - and its empty history table comes from Entity Framework's OWN create script, so it is the
  table an interrupted migration would actually have left rather than a hand-written guess at one. It then
  asserts its own premises before using itself: sixteen or more tables present, a history table present, and
  zero rows in it. Each of those is a way the fixture could quietly stop being half built, leaving tests that
  pass for reasons unconnected to what they claim.
- The half-built containment arm has its own uncontained twin, `TheHalfBuiltStore_IsFatal_WhenItIsNotContained`,
  which watches the SAME file kill an ordinary migration and names the table (`agent_delta` - the baseline
  creates the sixteen alphabetically and dies on the first, never reaching the fifteen behind it).
- The no-repair test seeds a row FIRST. Without it, "nothing was lost" is a statement about an empty file and
  would hold just as well against a startup path that dropped and recreated every table.
- The half-built diagnosis has a test for its OTHER failure direction - a guard that reports a half-built
  schema can be wrong by missing one or by condemning a healthy store, and the second would take the
  statistics surface down on every self-host Gateway. A fresh store and a fully migrated reopened store are
  both asserted available.
- The unreachable arm asserts the store **attempted** the connection (reason UNREACHABLE, failure count one,
  last error non-null). A Gateway that skipped statistics entirely would report NOT CONFIGURED and these
  would fail. "It started" is not the claim; "it started having tried and failed" is.
- The two-reasons test produces both states in ONE test and asserts they are **unequal**. Asserting only
  that each equals its own expected value would also pass against a build that had collapsed them, which is
  precisely the defect the ruling exists to prevent.
- The no-file-on-hosted test carries its own **control**: the identical run with the hosted flag off DOES
  write the file. Without it, the absence could be a fixture in which nobody would have written it anyway.
- The never-a-file-on-hosted selection tests each carry a self-host control for the same reason.
- The derivation tests use several DIFFERENT database names, because a fixture whose database happened to be
  named the thing a substitution would produce could not show a substitution at all.
- The pool-separation test compares the derived string to the **normalised** Gateway string, not only the raw
  one: a difference in whitespace or key order alone would still be the same pool key to Npgsql, so the raw
  comparison could pass on a difference that separates nothing.

### The run, pre-flighted before it was asked for

A contended fleet-wide lock means roughly one narrow run, so the four ways this one could have died for
reasons unconnected to what it measures were checked by reading first. Recorded because a run that fails on
its own fixture costs the slot and closes nothing, and because the next seat should not re-derive it:

- **Cross-test contamination by environment variable.** The row 10 and row 15 fixtures set `CC_DIRECTOR_ROOT`,
  `CC_GATEWAY_HOSTED` and the statistics override, which are process-wide. `TestParallelization.cs` disables
  parallelisation assembly-wide, so no other class runs beside them. The two containment classes take an
  explicit `StatsConnectionChoice` and never read the environment at all, so they are immune either way.
- **The storage root redirect.** `CcStorage.Root()` reads `CC_DIRECTOR_ROOT` on every call and caches
  nothing, so the row 15 control really does write into the test's own temporary root. Had it cached, the
  control would have written elsewhere and the test would have reported CONTROL FAILED for a reason that has
  nothing to do with the hosted path.
- **The roster response shape.** `/sessions` without `envelope` returns `Results.Json(all)` over a
  `List<SessionDto>`, so a JSON array is the right assertion, and the enrolment helper the fixture uses is
  the same one the tenancy suite depends on rather than a fixture written for this test.
- **Whether the row 15 control can write the file at all with an empty roster.** It can, and only just:
  `Observe` sets `changed` when it creates the hour bucket, which happens on the first call of a new hour
  even when the roster is empty. Had the write been gated on a session actually existing, the control would
  have failed and taken the row with it.

**A scope limit inside row 10 that the fixture cannot remove, so it is stated rather than glossed.** The
roster body proven is an EMPTY array: the test enrols a device but no Director pushes a session snapshot, so
the claim earned is that the roster ROUTE answered in roster shape while statistics were unreachable - which
does exclude an error object, an error page and an empty body, all of which would have carried a 200. It is
NOT a claim that sessions were enumerated with the statistics store down. Enumerating them needs a pushed
snapshot over the tunnel, which is a different rig.

### What is not covered at all, by anything here

- **No run against a real PostgreSQL server.** Every PostgreSQL assertion in this branch is about a
  connection that FAILS. That the derived connection actually opens, migrates into `gateway_stats` and lands
  its history in `gateway_stats.__EFMigrationsHistory` is worker 1's rig and is not proven here.
- **That the two pools are separate in the database.** The test asserts the two connection STRINGS differ,
  which is the mechanism by which Npgsql separates pools. It does not observe two pools, and it does not
  observe `application_name` in `pg_stat_activity`.
- **The failure surface itself.** Step 1 owns `/stats/data` and the 503 body. Nothing on this branch serves
  the availability state to any client.
- **Whether the twenty-second `OpenDeadline` is the right number.** It is reasoned, not measured.
- **CI.** Draft pull request 2319 covers `nosqlite-stats`, not this worker branch. Nothing has built this
  branch except the local compile above.
