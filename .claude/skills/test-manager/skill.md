---
name: test-manager
description: Owns this repo's test suites - the enforced two-minute budget, which suites are parked and why, how to diagnose a slow suite by measurement, and what coverage is not running. Triggers on "/test-manager", "tests are slow", "park a suite", "unpark", "test coverage", "flaky test".
---

# Test Manager

The owner of this repository's test suites. Three duties, in priority order:

1. **The gate stays fast.** A gate nobody runs protects nothing.
2. **We know what is NOT running.** Parked coverage is acceptable; parked coverage nobody remembers is not.
3. **Nothing is diagnosed by guessing.** Every claim about why a suite is slow comes from a measurement, and this file records the measurements that have already been made so they are not re-made badly.

## Quick Reference

| Action | Command |
|--------|---------|
| Run the gate (this is THE gate) | `.\scripts\test-local.ps1` |
| Run the gate plus parked suites (before a release) | `.\scripts\test-local.ps1 -Parked` |
| Run only the locked Gateway suite | `.\scripts\test-local.ps1 -Gateway` |
| Filter within a run | `.\scripts\test-local.ps1 -Filter "FullyQualifiedName~Snooze"` |

---

## Deciding what to run: ALWAYS ask the selector, never guess

`scripts/select-tests.ps1` answers "which suites can this change actually affect" from the files it
touched. **Use it whenever the question comes up** - before merging, when someone asks whether the
parked suites are needed, or when a change feels risky.

    .\scripts\select-tests.ps1 -Explain

It derives the answer from the .csproj REFERENCE GRAPH, not from a hand-written folder list: each
test project's transitive closure is computed, and a suite is selected when the change touched any
project inside it. A hand-maintained map goes stale silently the first time somebody adds a
ProjectReference; this one cannot.

It fails toward running MORE. An unrecognised path, a changed .csproj or .sln, a change to the gate
itself - all select everything. Only paths whose irrelevance is obvious (docs, markdown, images) are
allowed to select nothing.

**The gate calls it automatically and prints a COVERAGE GAP warning** when a change touches code that
a parked suite covers. That warning is the answer to "who remembers to run `-Parked`" - nobody has
to. Treat it as a required action, not a hint: run `-Parked` before merging, or say in the pull
request why not.

### Why the gate WARNS instead of auto-running the parked suites

Measured by replaying the last hundred merges: a parked suite was implicated in **69 to 80 per cent
of changes**, because `CcDirector.Core` and `Gateway.Contracts` are referenced by nearly everything.
Running them automatically would restore a twelve-to-forty-five-minute gate for seven changes in ten
- exactly the problem the budget removed. So the fast gate stands and the reader is told precisely
what it did not cover.

That number is also the honest measure of how much selection saves here: **less than you would hope.**
The fan-out from the shared projects is the real constraint, and the way to actually shrink it is to
make the parked suites fast enough to stop being parked.

### The map is validated against history, not asserted

`scripts/validate-test-selection.ps1` replays the last hundred merges and checks the only property
that can hurt: that the selection was never TOO SMALL. Ground truth is derived from paths, not from
the graph, so the check is not circular - a change editing `Engine.Tests` must select `Engine.Tests`,
a change under `src/` must select something, a change under `apps/` must select the web tests.

Last run: **100 changes, zero violations.** 44 per cent ran everything (fail-safe), 42 per cent
narrowed, 13 per cent needed no tests, 1 per cent web only.

Re-run it after changing the selector or restructuring projects. It is cheap and it is the only thing
standing between a clever map and a silent skip. It is honest about its limits too: a suite that
should run because of a RUNTIME coupling the reference graph cannot see will not be caught by any
path-based harness.

## The law: a two-minute budget, enforced

`$BudgetSeconds = 120` in `scripts/test-local.ps1`. Each suite gets that long. One that has not
finished is **killed** and the run fails naming it.

Exceeding the budget is **not a test failure** and must never be reported as one. It means the suite
no longer belongs in the default run. Park it, or make it fit.

**Do not raise the ceiling to make a red go away.** Every second added is paid by every person and
every agent, on every change, forever. The number is the point. It was a comment before it was
enforced, and as a comment it did not hold: a suite drifted to eleven minutes on a quiet machine and
thirty-three on a busy one, and people started working around the gate instead of running it.

`-Parked` and `-Gateway` deliberately suspend the ceiling - that run is the release gate and is
expected to be slow.

---

## What runs, and what does not

### In the default gate

| Suite | Tests | Notes |
|-------|-------|-------|
| `CcDirector.Gateway.UnitTests` | ~2760 | The PURE Gateway tests. No lock. |
| `CcDirector.Avalonia.Tests` | ~350 | |
| `CcDirector.Engine.Tests` | ~63 | |
| `CcDirector.HostedAgent.Tests` | ~88 | |
| `CcDirector.Launcher.Tests` | ~110 | |
| `CcDirector.Terminal.Avalonia.Tests` | ~24 | |

Roughly 3400 tests, about 80 seconds end to end including the build.

### Parked (opt in with `-Parked`)

| Suite | Cost | Why it cannot fit |
|-------|------|-------------------|
| `CcDirector.Gateway.Tests` | a QUEUE, not a runtime | Serializes machine-wide. Its cost is waiting for every other working tree on the machine. Runs of 45 minutes have executed ZERO tests. |
| `CcDirector.Core.Tests` | 11 min quiet, 33 busy | Its slowest tests each spend 30-80 seconds creating real git repositories, branches and worktrees on disk. Real subprocess work, not sleeping. |

**THE COST OF PARKING, WHICH MUST BE SAID OUT LOUD WHENEVER IT COMES UP.** Those two suites hold
real coverage - the Gateway's host-bound endpoint, tenancy and boundary tests, and most of Core. A
regression in either can reach main without a local red until `-Parked` runs. That is a deliberate,
temporary trade: a gate that is actually run beats one that is comprehensive and skipped. It is not
a reason to relax about it.

---

## The machine-wide lock, and why filters do not escape it

`CcDirector.Gateway.Tests` takes `GatewayTestSuiteLock`, which serializes runs of that assembly
across every working tree for one user on one machine. The lock itself is well-argued - overlapping
runs corrupt each other's evidence, and a lock a caller must REMEMBER to take is a convention, which
is why acquisition is automatic.

**It is armed by a `[ModuleInitializer]`, so it is taken when the ASSEMBLY LOADS** - before any test
runs, whatever `--filter` was passed. There is no way to opt a single test out from inside the
assembly. Running one pure unit test from that project still queues behind another working tree.

That is why the pure tests were MOVED OUT into `CcDirector.Gateway.UnitTests` rather than filtered.

Past `MaxWait` (45 minutes) a queued run stops with an error naming the holder. That is the designed
behaviour, not a bug: it never takes a lock from a living process. If a holder is genuinely stuck,
that is a decision for a human - measure a CPU delta before calling anything stuck.

---

## Which project does a new test belong in?

**The locked project is the default home. This direction is the safety property.**

Put a test in `CcDirector.Gateway.Tests` (locked) if it does ANY of:

- constructs a `GatewayHost`, binds a port, or drives `FakeTunnelDirector`
- spawns a process
- touches live Postgres
- **mutates a process-global** - `Environment.SetEnvironmentVariable`, a static toggle, the current directory

Otherwise it may go in `CcDirector.Gateway.UnitTests` (unlocked).

A test that needs exclusivity and is written in the unlocked project runs beside another working
tree's suite and produces corrupted-but-clean-looking evidence. A test that does NOT need it and
sits in the locked project merely runs slower. The asymmetry is the whole argument: **when unsure,
locked.**

Note the environment-variable rule specifically - it is not about cross-process safety, it is about
xUnit running classes in parallel WITHIN a process. 34 classes were moved back for exactly this.

---

## Diagnosing a slow suite

Follow this order. It is written down because guessing produced three wrong answers in one day.

### 1. Get per-class timings from the TRX. Do not read the code first.

Every run writes a TRX. Group by class and sort:

```powershell
$f = Get-ChildItem "$env:TEMP\cc-test-local-*\<Suite>.trx" | Sort-Object LastWriteTime -Desc | Select -First 1
[xml]$d = Get-Content $f -Raw
$rows = $d.TestRun.Results.UnitTestResult | Where-Object { $_.duration } | ForEach-Object {
  [pscustomobject]@{ Cls = ($_.testName -replace '\.[^.]+$','') -replace '^.*\.',''
                     Sec = [TimeSpan]::Parse($_.duration).TotalSeconds } }
$rows | Group-Object Cls | ForEach-Object {
  [pscustomobject]@{ Class=$_.Name; N=$_.Count; Total=[math]::Round((($_.Group|Measure-Object Sec -Sum).Sum),1) } } |
  Sort-Object Total -Descending | Select-Object -First 15
```

### 2. Compare a suspect class against a known-pure one

This single comparison is what actually located the biggest cost:

| class | tests | time | per test |
|-------|-------|------|----------|
| `SessionOrderingTests` (pure) | 118 | 53 ms | **0.45 ms** |
| `SnoozeRegistryTests` (database) | 31 | 11 s | **355 ms** |

A ratio like that names the culprit immediately. A flat profile (top class only a few per cent of
the total) means there is no hotspot and you are looking for a per-test fixed cost instead.

### 3. Only then form a hypothesis, and test it before shipping it

Run the change, measure again, and run the suite repeatedly. If the numbers do not move, **revert**.

---

## Known costs, and the false trails

### Confirmed and fixed

**The migration set was rebuilt once per database-backed test.** Constructing a `GatewayDatabase`
over an empty file applies every migration. Over half the Gateway suite is database-backed, so the
same schema was built from scratch hundreds of times a run - 355 ms per test against 0.45 for a pure
one. `GatewayDbTestHarness` now migrates ONCE per process, holds the result as bytes, and writes
those bytes per test. Behaviour-preserving: same constructor, same migrations, same code path, and
EF's own "no pending migrations" branch then skips the work. **2 minutes to under one.**

If you touch that harness, keep the template as BYTES. The first version copied a file and called
`SqliteConnection.ClearAllPools()` to release it - which is process-global and fired while other
tests were running.

### False trails - do not walk these again

**"Low CPU on the test host means the tests are sleeping."** FALSE, and it cost hours. Child
processes do not appear in the parent's CPU counter, and neither does time waiting on a database.
`Core.Tests` showed 25 seconds of CPU across 11 minutes and was declared sleep-bound; its slowest
tests were in fact spawning **git** the whole time. Measure per-test durations, not parent CPU.

**"It is full of sleeps."** FALSE for both suites. `Gateway.UnitTests` contains 19 sleep calls
totalling **one second**; `Core.Tests` has 68 totalling 24. Neither can explain minutes. Count them
before blaming them:

```bash
grep -rn "Task\.Delay\|Thread\.Sleep" --include=*.cs src/<Suite>/ | wc -l
```

**"More threads will fix it."** FALSE for `Gateway.UnitTests`. Measured: 4 threads ~120 s, 12 threads
163 s, 24 threads 149 s - and the higher settings brought back flaky failures. Parallelism is capped
in code (`AssemblyParallelism.cs`), not in `xunit.runner.json`: the JSON landed in the output
directory and was **demonstrably ignored** while the same value worked on the command line.

### Open, with two failed fixes recorded so they are not retried blind

**Intermittent database-test failures.** One or two tests fail per run under load, never the same
ones, always passing in isolation. Correlates with how many other suites are running on the machine;
does not reproduce on a quiet machine.

The leading hypothesis is that `GatewayDatabase.Dispose` calls `SqliteConnection.ClearAllPools()`,
which is **process-global**, and that roughly **55 test call sites do the same** deliberately - it is
the established idiom for "release the file so I can read it". With one database in a running Gateway
that is invisible; with hundreds disposing concurrently under test, every call reaches across every
other live database.

**Two fixes were tried and both reverted. Do not repeat them without new evidence:**

1. *Narrowing `Dispose` to `ClearPool` for its own connection string.* Broke six `DeviceKeyAtRest`
   tests deterministically - those tests rely on somebody's global clear to release a file they then
   read. The perpetrators and the victims are the same call sites, so politeness in one cannot work.
2. *Opening test databases unpooled, so they own no pooled handle to take.* Five runs gave 1, 2, 2,
   0, 0 failures - statistically indistinguishable from the baseline, and failures then appeared in
   non-database tests too. The hypothesis was **not confirmed**, so the production seam was reverted
   rather than shipped on a hunch.

Anyone picking this up should start by reproducing it deliberately (run the suite while several
other suites run) and by establishing how `Microsoft.Data.Sqlite` actually keys its pools.

---

## Bringing a parked suite back

1. Measure it first, using the recipe above. Do not start optimising.
2. Fix the dominant cost. Prove it with before/after numbers on the same machine.
3. Run it at least three times. It must be under 120 seconds AND deterministic.
4. Move it from `$parkedProjects` to `$defaultProjects` in `scripts/test-local.ps1`, and record the
   measurement in the comment beside it - the number is what lets the next person judge a regression.
5. Update the tables in this skill.

`Gateway.UnitTests` has already made this round trip: parked for exceeding the ceiling, then returned
once the per-test migration cost was removed. That is the pattern to follow.

---

## When a new test assembly is created

Two `[ModuleInitializer]` guards in `CcDirector.Gateway.Tests` are **live-data protection**, not
convenience, and a new assembly starts with neither:

- `TestStorageRootRedirect` - points `CcStorage.Root` at a throwaway temp directory. Without it,
  tests write into the owner's real `%LOCALAPPDATA%\cc-director`: live missions, cron jobs, key
  vault. This has already happened once, renaming a live statistics file aside.
- `TestEnvironment` - pins the Director instance-discovery directory away from the real one, and
  disables the turn-brief sweep and Tailscale serve provisioning.

**Link them, do not copy them** (`<Compile Include="..\CcDirector.Gateway.Tests\...">`), so the two
assemblies cannot drift on a guard this consequential.

Also load-bearing and easy to miss: a `ProjectReference` to
`CcDirector.Gateway.Migrations.Postgres`. Nothing calls into it - EF resolves the migration set by
assembly NAME at runtime, so its absence is a `FileNotFoundException`, not an unused reference.

---

## Coverage: what to say when asked

Answer with what is NOT running, not with a number of tests. The honest summary today:

- **Running by default:** ~3400 tests, ~80 seconds.
- **Not running by default:** the host-bound Gateway suite (endpoints, tenancy, boundaries) and all
  of `Core.Tests`. Both run under `-Parked`.
- **CI** still runs everything after a merge as a backstop; it takes about fifty minutes and does not
  block anyone.

If a change touches the Gateway's host-bound surface, or anything Core covers, say so and run
`-Parked` before merging. The budget buys speed on ordinary changes; it is not a licence to skip
verification on the ones that need it.

---

**Skill Version:** 1.0
**Last Updated:** 2026-08-03
**Enforced by:** `scripts/test-local.ps1` (`$BudgetSeconds`, `$defaultProjects`, `$parkedProjects`)
