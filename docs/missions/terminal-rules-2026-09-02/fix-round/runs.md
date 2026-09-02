# The runs, with their exit codes and the commits they ran on

Every number this round quotes, in one place. The Architect's two recording rules apply throughout: a
count without an exit code is how a previous round reported exit 0 where the inspection saw exit 1, and
a run is evidence only for the tree it ran on.

## The tip gate

| attempt | commit | verdict | exit |
|---|---|---|---|
| 1 | `98493f075` | RED - 1 failed in `Gateway.UnitTests` | 1 |
| 2 | `43694cffa` | RED - 1 failed in `Gateway.UnitTests`, 1 in `cc-director-setup-engine.Tests` | 1 |
| 3 | `43694cffa` | **GREEN** - nine projects, every TRX outcome Completed, 4,556 tests, 0 failed | **0** |

The green run in full:

```
CcDirector.Core.UnitTests            outcome=Completed  total=164     0 failed
CcDirector.Gateway.UnitTests         outcome=Completed  total=3262    0 failed, 3 skipped
CcDirector.Avalonia.Tests            outcome=Completed  total=364     0 failed
CcDirector.Engine.Tests              outcome=Completed  total=63      0 failed
CcDirector.HostedAgent.Tests         outcome=Completed  total=88      0 failed
CcDirector.Launcher.Tests            outcome=Completed  total=113     0 failed
CcDirector.Terminal.Avalonia.Tests   outcome=Completed  total=24      0 failed
cc-director-setup.Tests              outcome=Completed  total=25      0 failed
cc-director-setup-engine.Tests       outcome=Completed  total=456     0 failed

RESULT: all projects exited zero.        RUNNER_EXITCODE=0
```

The one skip in `Gateway.UnitTests` beyond the two long-standing ones is `StoredScreenRigReadTests`,
which is gated on a rig database and reports SKIPPED without one. A skip is not a pass; the rig asserts
that test actually ran, and it did - see the rig row below.

## Why the first two attempts were red, established rather than assumed

**Attempt 1 was MY defect and it is fixed.** `TenantSettingsResolverTests` - a test this round never
touched - failed with `Cannot access a disposed object: SQLitePCL.sqlite3` inside
`GatewayDbTestHarness.Open`, while every screen test passed. `GatewayDatabase.Dispose` calls
`SqliteConnection.ClearAllPools()`, which is PROCESS-GLOBAL, and dropping the model-built screen
database had left a helper opening a fresh `GatewayDatabase` on every call - so `SessionScreenStoreTests`
accumulated a dozen and disposed them in a burst, clearing the pool repeatedly under whatever else was
running. One database per tenant, cached, fixed that (`43694cffa`).

**Attempt 2 was NOT mine, and that is settled with an artifact rather than an argument.** A worktree was
cut from `origin/main` at `a694b39d7` - none of this round's changes in it - and `Gateway.UnitTests` was
run five times:

```
main run 1: Passed!  0 failed, 3235 passed, 2 skipped        exit 0
main run 2: Passed!  0 failed, 3235 passed, 2 skipped        exit 0
main run 3: Passed!  0 failed, 3235 passed, 2 skipped        exit 0
main run 4: Failed!  1 failed, 3234 passed, 2 skipped        exit 1
main run 5: Failed!  1 failed  - WorkListStorePersistenceTests.InterruptedDrain_AfterRestart_...
            System.ObjectDisposedException : Cannot access a disposed object.
            Object name: 'SQLitePCL.sqlite3'
               at GatewayDatabase.Open()
```

**Two failures in five runs on plain `main`, each in a DIFFERENT test, all with the identical
`ObjectDisposedException: SQLitePCL.sqlite3`.** The defect is pre-existing: `GatewayDatabase.Dispose`
calls a process-global pool clear, and every database-using test class in that assembly disposes one, so
any of them can have its connection pulled by another finishing at the wrong moment. This round did not
introduce it and does not fix it - fixing it means changing disposal semantics in production code, which
is a different change with its own proofs. It is raised here as a finding about the SUITE.

The second failure in attempt 2 was `cc-director-setup-engine.Tests`, in
`EverySignInCancelledMessage_StatesTheFact_AndNamesNoButton` - it received a message about
`DEVTHROTTLE_HOSTED_GATEWAY_URL` where it expected "Sign-in was cancelled". That environment variable is
NOT set on this machine at process, user or machine scope (checked), so it is another process-global
race inside that assembly. It is the same assembly in which inspection 01 saw a DIFFERENT intermittent
case, `InstallAsync_FailedVenvRebuild_LeavesNoManagedShim`. Also not this round's code, and green in
attempt 3.

**What this means for reading any local gate here:** a single red run of this suite is not evidence of a
defect until it has been judged against the parent. Attempt 1 was mine and attempt 2 was not, and the
difference was established by running `main`, not by reasoning about the diff.

## The three filtered runs, all on `43694cffa`

| finding | filter | result | exit |
|---|---|---|---|
| 1 | `~GatewayScreenReaderLiveReadTests` | 5 passed, 0 failed | **0** |
| 2 | `~CaptureMarkDescribesTheCapturedFrame` + `~TurnEndScreenCapture` | 4 passed, 0 failed | **0** |
| 3 | `~Append_TwoDirectorsCapturingOneSession` | 1 passed, 0 failed | **0** |

Findings 4, 5 and 6 need no separate filtered run: the tip gate subsumes them, and their own filtered
greens are recorded in their pages.

## The row 4 rig, on `43694cffa`

`scripts\terminal-rules-screen-proof.ps1 -Slot 18 -GatewayPort 7996`, exit 0:

```
STEP 1 PASS: the throwaway Director is connected to the throwaway Gateway
session activity states observed, in order: Working -> WaitingForInput
STEP 2 PASS: the Gateway logged storing a screen pushed by the rig Director
STEP 2b PASS, machine up: [GatewayScreenReader] sid=f8b7c11f-...: pulled the screen over the TUNNEL
the three run markers are present in the terminal buffer, so the comparison below has a real subject
STEP 3 PASS, machine stopped: the route refused before the read: {"error":"session not found on any director"}
read-back verdict rests on ...\stored-row.txt, which names session f8b7c11f-2d78-4f8e-8128-9c2606d97a4e
  and all three of this run's markers
STEP 4 PASS: the real store read the screen back over the real migrated schema, machine offline
ROW 4 PROVEN.
removed the rig root ...\screen-proof-20260902-153655-acfb0849, database included

  stored   | TR_SCREEN_PROOF_20260902-153655_ALPHA  -> stored row 35
  terminal | TR_SCREEN_PROOF_20260902-153655_ALPHA
  stored   | TR_SCREEN_PROOF_20260902-153655_BRAVO  -> stored row 36
  terminal | TR_SCREEN_PROOF_20260902-153655_BRAVO
  stored   | TR_SCREEN_PROOF_20260902-153655_CHARLIE -> stored row 37
  terminal | TR_SCREEN_PROOF_20260902-153655_CHARLIE
```

All forty stored rows were compared against the Director's own terminal text, not only these three.

## `Gateway.Tests` - the attempts, and who ended them

**Three attempts. Two were ended by the Architect, and NEITHER was a fault of the suite or of this
round's code.** That is recorded here in those words because the failure shape has now happened twice
and both times the artifact would have said something untrue.

| attempt | when | what happened | cause |
|---|---|---|---|
| 1 | 12:26-12:55 | queued 30 minutes behind `devthrottle-turn-push`, ended with ZERO tests; its log says "Test host process crashed" | **the Architect killed it**, because it was queued ahead of a live production outage's fix. It did not crash. |
| 2 | 17:21 | ended at once, exit 1, no log file written | **the Architect killed it in error.** Its watcher classified any `Gateway.Tests` process without "terminal-rules" in its command line as another worktree's - and this invocation uses a RELATIVE project path, so it contains no worktree name at all. The competitor it saw was this run's own parent process. |
| 3 | 17:23 | started on commit `02d66df15` with the lock verified free by exclusive open and zero `Gateway.Tests` processes | see below |

**The lesson both times is the same one, and it is the mission's own.** Absence of a string from a
command line is not evidence of another owner, exactly as an absence of newer bytes was not evidence
that a terminal had not moved. A check whose pass condition is an absence fails open, and here it failed
open in the direction of killing a healthy run. On attempt 2 the first instinct in this seat was to blame
its own shell quoting - exit 1 with no log file looks exactly like a command that never started - and
that would have gone into this file as a cause that was never observed. It did not, because the
Architect said what it had done. **If a run here dies and this seat did not end it, the cause is asked
for, not inferred.**

## `Gateway.Tests` - the result of attempt 3, and what it found

**It ran to completion on `02d66df15`: 2,325 tests, 2,315 passed, 6 FAILED, 4 skipped, 56.5 minutes,
exit 1.** It acquired the lock rather than queueing, which is the condition that makes a result possible
at all:

```
17:23:29 pid 14124: [gateway-test-lock] Acquired the per-user Gateway test lock. Starting the run.
```

This is the payoff of the parked suite and the reason the coverage gap was worth naming: the default
gate cannot see any of the six.

### The one that was MINE

`PostgresProviderProofTests.Collation_ExplicitC_OnEveryStringKeyColumnTheModelDeclares_OnRealPostgres`:

```
these columns carry an explicit C collation but are neither a string key column nor a listed
exception: known_repositories.MachineKey, known_repositories.PathKey
```

That is the check working, and it is a consequence of the MERGE rather than of either change alone.
Those columns arrived on main while this mission was replacing the hand-written allow-list with the
model-derived check (rulings 9 and 10), and the two met for the first time when main was merged. They
sit in a non-unique index rather than a key, so the derived enumeration cannot see them - which is
exactly the case the inverted exception list exists for. The first fix put them in `CollationExtras`, the list whose entries each carry a written argument.
**That was wrong and the Architect caught it.** An entry there records a DECISION - that a column does
not need what the model would otherwise demand - and nobody decided anything here: these are another
mission's brand-new columns and they were MET, not reasoned about. Recording debt as a reasoned
exception is the allow-list failure returning in the new list's clothes, turning "nobody has looked at
this" into "we looked at this and it is fine".

They now sit in `InheritedCollatedNonKeyColumns`, whose comment says they are an open question, with
the date, the fact that they arrived on main mid-round, and the three ways whoever owns those columns
could resolve it.

One correction to the instruction, reported because it is a fact about the code rather than a
disagreement: the Architect named `InheritedUncollatedKeyColumns` as the list to move them to, and they
cannot go there. That set is subtracted from the DERIVED KEY columns and exists for key columns carrying
NO collation - the opposite shape - and its own comment says a new column may not be added to it. The
new sibling set carries the same meaning for the direction these columns are in.

Note also what this same run says about finding 3:
`SessionScreens_IdempotentOnTheNaturalKey_AndByteOrdinalAboutIt_OnRealPostgres` **PASSED (216 ms)** -
the only exercise of the screen store's key on the provider the hosted Gateway actually runs, with
`DirectorId` in that key. The Postgres gap named earlier in this file is closed.

### The five that were NOT mine, settled by artifact rather than by argument

`VoiceServingLoopIsolationTests` (two), `TurnEndWatcherTenantIsolationTests` (one) and
`WingmanVoiceTurnLiveScreenProofTests` (two). The first three fail on a ten-second wait returning null;
the last two on a hundred-second HTTP timeout.

The instinct was that they must be this round's, because they are voice, turn-end and live-screen tests
and this round changed how a live screen is read. **That instinct was wrong, and it was checked rather
than acted on.** A worktree cut from current `origin/main` ran the same five:

```
origin/main 13e43603b, filtered to those three classes:  16 passed, 0 failed, exit 0
mission branch ca26e8c09, same filter:                   11 passed, 5 FAILED, exit 1
```

They are main's own defects from its turn-push work, and main had already fixed them in two commits that
landed AFTER this branch's earlier merge:

- `ad4820fd6` - "the parked voice tests watch the store, not a tunnel read that no longer exists"
- `13e43603b` - "the wingman says which computer to update instead of waiting for a conversation that is
  never coming"

The second one is almost a description of the observed symptom. Merging current main (`3ca41dbee`)
brought both in.

### After the merge and the collation exception

```
dotnet test ...Gateway.Tests.csproj --filter <the four affected classes>   on 2912b3e62
Passed!  - Failed: 0, Passed: 22, Skipped: 0, Total: 22        exit 0
```

All six resolved. A COMPLETE run at the merged tip is still owed - see below.

### A complete run at the tip cannot be taken in the foreground

The harness kills any command at ten minutes (observed twice, exit 143), and this suite is 49 minutes.
The earlier complete run survived only because the harness chose to move it to the background rather
than kill it, which it no longer does. Running it in the background is forbidden without the Architect's
say-so, so it was ASKED rather than decided. What is in hand meanwhile: a complete run at `02d66df15`
with its six failures each accounted for, and all six passing at `2912b3e62`.

## The tip gate is OVER BUDGET, and it is not this round's doing

After merging current main, `test-local.ps1` twice reported:

```
RESULT: OVER BUDGET - 1 suite(s) exceeded the 120-second ceiling and were STOPPED:
  CcDirector.Gateway.UnitTests
```

Every suite that produced a TRX was `Completed` with zero failures both times; the runner's own message
says this is NOT a test failure. **The ceiling was not touched and must not be** - the runner says so
itself, and every second added to it is paid by everyone on every change forever.

It is not this round's doing, and that is measured rather than asserted. This round's screen tests run
in **6 seconds** (29 tests, filtered). On UNMODIFIED `origin/main`, the same suite measured **1m25s,
1m49s, 1m58s and 2m04s** across the four runs taken while attributing the earlier flake - already at and
sometimes past the 120-second ceiling with none of this round's tests in it. The remedy the runner names
is to park the suite or make it fit, and both are decisions for the seat that owns the gate rather than
for a mission passing through.


**It has not run, and it is the only thing outstanding.**

Checked immediately before each attempt by opening the lock file exclusively and seeing whether it
throws - which is a better instrument than counting processes, because a count cannot say WHO holds the
lock and reads a queued competitor as a free machine:

```
GATEWAY.TESTS LOCK: HELD
pid 68428, 42560  created 15:10:22  worktree devthrottle-turn-push
```

**It must not be queued.** A healthy full run measures 48.88 minutes against the lock's 45-minute
MaxWait (issue 2653), so a run that queues can NEVER acquire - it times out after three quarters of an
hour with ZERO tests and reads as a failure when it is a queue. That is exactly what happened to this
mission's earlier attempt, and it is why that result must not be read as flakiness. The lock belongs to
the `Gateway.Tests` ASSEMBLY and is taken before any test runs, so a filtered run contends identically to
a full one; runs of other assemblies do not contend, which is why everything above could proceed.

**The Architect seat was GONE when this report was ready.** `cc-devthrottle message send` answered
"No session matches", and `cc-devthrottle session list` answered "No sessions are running in the fleet"
at 15:41 - the Architect, the Gateway seat working the incident, and every other seat had ended. So this
report was never delivered by message; it is delivered by being on the branch. Whoever picks the mission
up should read that as "the Manager finished and the seat it reports to had already gone", not as a
Manager that failed to report.

**The CI fallback needs a decision this seat cannot make.** `.github/workflows/ci.yml` triggers on
`push` to `main` and on `pull_request` targeting `main` - not on a push to a mission branch. `gh run
list --branch mission/terminal-rules` returns an empty list, confirming it. So getting CI to run
`Gateway.Tests` means opening a pull request against `main`, and that is the Architect's act, not the
Manager's. The alternatives are: the Architect opens the pull request, or the lock frees (expected
around 16:00 local) and it runs here. Either way the result belongs in this file with its exit code and
its commit.
