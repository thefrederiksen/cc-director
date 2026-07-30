# Step 2, worker 6: the startup boundary - state at stand-down

Branch `nosqlite-stats-w6-startup`, worktree `D:\ReposFred\dt-nosqlite-w6`, cut from worker 2's branch.
Parked on the Manager's instruction when the mission cut to three concurrent workers. Rows 10 and 15 stay
OPEN. This file records exactly what is built, what is NOT proven, and what the next seat picks up.

**Nothing in this file is a proof. Read the "what is not proven" section before quoting anything from it.**

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

### The two named reasons

`StatsStoreUnavailableReason` gained `NotConfigured` and `Unreachable` beside worker 2's four adoption
reasons. One enum for the whole statistics availability surface, and a stable machine-readable code per
reason (`not_configured`, `unreachable`, ...) written out by hand rather than derived from the enum member
name, so renaming a member in C# cannot silently change a string an operator greps for.

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

### How these fixtures were built to be able to fail

Recorded so the next seat can check the shape rather than re-deriving it, and so a green - when there is one
- is worth something:

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
