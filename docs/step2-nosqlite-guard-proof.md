# The no-SQLite-on-hosted guard: what it is, and the record of watching it trip

Step 2, worker 7. Branch `nosqlite-stats-w7-guard`. This file records what was WATCHED, not what was
intended. The guard is `NoSqliteOnHostedArchitectureTests` (rules DT-SQL-1 to DT-SQL-4) plus
`HostedSqliteGuard`.

---

## 1. Where the guard lives, and why there

**`src/CcDirector.Gateway.Tests/Architecture/NoSqliteOnHostedArchitectureTests.cs`** - a metadata scan
(Mono.Cecil) over the compiled assemblies that ARE the hosted Gateway service: `CcDirector.Gateway.dll`
and `CcDirector.Gateway.Host.dll`. Every type that touches SQLite must be named on an allowlist that
carries the reason it cannot reach a hosted Gateway.

Three shapes were available and they are not equivalent. The property that decides between them is
BYPASSABILITY - a guard that only binds the call site we already know about is worth very little,
because the whole point is catching the call site nobody has written yet.

| Shape | What it would catch | How it is walked around |
|---|---|---|
| Startup assertion in the Gateway host | Only what runs during startup | Any store opened lazily, on a request, or from a background sweep - which is most of them |
| Runtime assertion over the composed host in a test | Only paths a test actually drives | The store nobody wrote a test for. Passes GREEN for the exact reason it should be red |
| **Static scan of the compiled assemblies, allowlist polarity** | **Every site, executed or not** | Reflection, or code outside the scanned assemblies (section 5) |

The first two share one fatal property: **they are satisfied by nothing having happened.** That is the
specific false green this task was warned about, and it is not a hypothetical - a new SQLite store will
sit behind a lazy initializer or an endpoint no test calls, so the guard would report green on the day
the store shipped. A metadata scan reads what the assembly CAN do. It does not care whether any path ran.

There is also no supported process-wide interception point for `new SqliteConnection(...).Open()`.
`Microsoft.Data.Sqlite` exposes no global hook, so a purely runtime guard could only ever bind sites that
remember to call it - which is the "only the call site we know about" weakness by construction.

**The polarity is the other half of the decision.** A denylist enumerates the call sites already known;
an allowlist catches every OTHER one. A type nobody has written yet is caught because it is not on the
list. This matches the shape already in this repository: `TenantGateArchitectureTests.GlobalUnscopedTables`.

`HostedSqliteGuard` (`src/CcDirector.Gateway/Data/HostedSqliteGuard.cs`) is the RUNTIME half - the
refusal a hosted-reachable site calls before opening a connection. It exists so a future allowlist entry
has a real mechanism to point at rather than a promise. Its notion of "hosted" is a compile-time alias of
`GatewayDatabase.PostgresConnectionEnvVar`, so the two are one string and cannot drift apart.

**Hosted is identified exactly as the Gateway already identifies it:** `CC_GATEWAY_DB_CONNECTION` is SET.
Set-but-blank counts as hosted, failing closed - a misconfiguration must never be the thing that hands a
hosted Gateway a SQLite file.

---

## 2. What is on the allowlist today, and why it shrinks

| Type | Why it cannot hand a hosted Gateway a SQLite file |
|---|---|
| `GatewayDatabase` | BRANCH-GATED on the hosted marker itself - every SQLite statement sits behind the same `CC_GATEWAY_DB_CONNECTION` test that means hosted |
| `GatewayDbContextDesignTimeFactory` | DESIGN-TIME ONLY - built by `dotnet ef`, never by the Gateway process |
| `GatewayStatsDatabase` | **The subject of the remediation, not an exemption.** Ungated today; this IS the outage. Named so the guard passes against the CURRENT tree while workers 2-5 port it |
| `GatewayInputStatsAggregator` | Uses the connection the store above already opened; opens none of its own. Goes away with it |

DT-SQL-2 makes the last two self-clearing: the moment the port removes SQLite from them, DT-SQL-2 goes
RED and says to delete the entries. The guard tightens on its own to the two structurally-safe entries.
It cannot be left permissive by inattention.

---

## 3. The proof: watched tripping, four times

The guard was committed BEFORE any fault was injected, so undoing a fault could not take the guard with it.

### Fault 0 - detector validation (does the scan see anything at all?)

The allowlist was emptied. If the scan were broken, DT-SQL-1 would pass identically to a clean tree - a
green that proves nothing. It went RED listing **15 entries covering exactly the 4 allowlisted top-level
types** and nothing else. Two entries are worth reading:

```
CcDirector.Gateway.dll: CcDirector.Gateway.Data.GatewayDatabase/<>c__DisplayClass9_1
    touches Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite
CcDirector.Gateway.dll: CcDirector.Gateway.Stats.GatewayInputStatsAggregator/<>c__DisplayClass54_0
    touches Microsoft.Data.Sqlite.SqliteDataReader
```

Those are compiler-generated closures. The scan sees through the compiler's transformation and rolls them
up to the owning type, which no source-line reading would do.

### Fault 1 - the innocent-looking new store (the real threat)

A new file, `GatewayFeedbackStore.cs`: a small table, `new SqliteConnection`, `CREATE TABLE IF NOT
EXISTS`. Nothing about it looks like an outage. **Nothing constructed it and no test called it.** RED:

```
DT-SQL-1: the hosted Gateway must keep NO SQLite database - its file lives on a network share, where a
deploy running two containers corrupts it (the 2026-07-30 outage, 32 minutes of HTTP 500 to every
client). These types touch SQLite and are not named on SqliteTouchingTypes:
  CcDirector.Gateway.dll: CcDirector.Gateway.Stats.GatewayFeedbackStore touches Microsoft.Data.Sqlite.SqliteConnection
Put the data in PostgreSQL (the hosted Gateway already has one), or - if this really cannot reach a
hosted Gateway - add the type to SqliteTouchingTypes with the reason it cannot, and call
HostedSqliteGuard.EnsureNotHosted before opening anything. Self-host keeps SQLite and is unaffected.
```

That the store was never executed is the point: this is the case a startup or composed-host assertion
would have passed.

### Fault 2 - the same store, written to dodge a source grep

`GatewayNoteStore.cs`, using `using Db = Microsoft.Data.Sqlite;` and `new Db.SqliteConnection(...)`. It
contains neither `using Microsoft.Data.Sqlite;` nor `new SqliteConnection` as text; a grep for both
returned nothing. The scan reads IL, so it went RED anyway:

```
  CcDirector.Gateway.dll: CcDirector.Gateway.Stats.GatewayNoteStore touches Microsoft.Data.Sqlite.SqliteConnection
```

### Faults 3 and 4 - both failure directions of DT-SQL-2

A guard has two failure directions and both were watched.

- A ghost name (`AStoreThatWasRenamedAway`) on the allowlist: RED - *"no such type exists ... A stale
  allowance is a permission nobody is checking, and the next type to take the name inherits it."*
- A real type that does NOT touch SQLite (`GatewayHostedMode`), simulating the port landing: RED - *"no
  longer touch it - which is GOOD NEWS. Delete their entries ... and the guard tightens around it."*

### Back to green

Every fault was removed and the four rules pass on a clean tree: `Failed: 0, Passed: 4`.

---

## 3a. DT-SQL-5 - WRITTEN, BUILDS, NEVER RUN. NOT PROVEN.

Stated first and plainly so this section cannot be skim-read as finished.

DT-SQL-5 binds every allowlist entry to a condition the scan evaluates instead of to prose, in one of two
directions - TRANSITIONAL entries fail when the world that ends them arrives, STRUCTURAL entries fail when
the property they rest on stops holding. All four current entries carry one (section 4, limitation 4 is
what it answers). The record type has no constructor without a condition, so a prose-only entry cannot be
written.

**It has never been executed.** The build is clean; that is all that is known. Every attempt to run it was
aborted by the fleet-wide test lock. In particular the trip proof that the other five rules got - inject a
stub statistics `DbContext`, watch DT-SQL-5 go red naming the stale `GatewayStatsDatabase` exemption,
remove it, watch it go green - **has not been done**.

Until that pair is watched, DT-SQL-5 is decoration by this document's own standard, and the ledger row for
it stays OPEN. A guard that has never been seen tripping proves nothing, and that applies to the rule that
polices the other rules exactly as it applies to them.

## 4. What the guard does NOT cover

Stated plainly, because a guard whose limits are unstated gets trusted past them.

1. **Reflection.** `Type.GetType("Microsoft.Data.Sqlite.SqliteConnection")` and `Activator.CreateInstance`
   name the type in a string, not in metadata. Nothing here sees it. This is the one real hole.
2. **Assemblies outside the two scanned.** `CcDirector.Core` and `CcDirector.Engine` are in the hosted
   Gateway's reference closure and DO contain SQLite (agent-history readers, quick actions,
   communications). They are deliberately NOT scanned: that code is Director-side and would drown the
   allowlist in legitimate entries. **A hosted Gateway code path calling into Core's SQLite would not be
   caught.** Narrowing that is a separate piece of work with its own allowlist.
3. **Method-level granularity.** The allowlist names TYPES. Adding a new SQLite method to
   `GatewayStatsDatabase` does not trip it. The two entries where that matters are the ones scheduled for
   deletion.
4. **Whether an allowlisted site is genuinely unreachable on hosted.** DT-SQL-1 checks the entry EXISTS
   and carries a reason; no machine checks the reason is true. `GatewayStatsDatabase` is proof it can be
   false - it is on the list today and it really does open SQLite on hosted.
5. **A runtime guard call being reached.** DT-SQL-4 proves `EnsureNotHosted` refuses when called. Nothing
   proves a caller's guard call DOMINATES its connection open - `if (false) Guard(); Open();` would pass.
   IL dominator analysis was out of scope.
6. **Nothing outside the .NET assemblies.** A migration script, a container entrypoint, or a sidecar that
   opened the file is invisible here.
7. **`HostedSqliteGuard` has no production caller on this branch.** It is a tested, working mechanism
   ready for the wiring worker; it is not yet load-bearing. The total guarantee today comes from DT-SQL-1.

---

## 5. Scope kept

- `GatewayDatabase` was NOT changed. It already branches correctly.
- The statistics store was NOT touched - workers 2, 3, 4 and 5 are porting it in parallel.
- The guard passes against the CURRENT tree, and is written against the hosted path in general rather
  than against any one class, so it passes against the ported tree too.
- Self-host keeps SQLite. Nothing here forbids SQLite existing or running on a desktop install; DT-SQL-4
  drives the self-host arm explicitly and asserts the guard is inert.
