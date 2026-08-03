# The default gate is flaky on this machine, and it is not this branch - 2 August 2026

The default `scripts\test-local.ps1` run went red on `roster-fold-batch` three times. This records why that
red is NOT this work item's, established by a control rather than by an argument, because "it looks like
somebody else's problem" is what everybody says about a red in a file they did not touch.

## The measurements

Same machine, same evening, same invocation (the DEFAULT gate - no flags).

| Run | Tree | Failures |
|---|---|---|
| 1 | this branch (pre-rebase) | **2** - `MorningReportBuilderTests`, `WorkflowCloneTests` |
| 2 | this branch (pre-rebase) | **5** - five `WingmanVoiceServiceTests`, DISJOINT from run 1 |
| 3 | this branch (rebased onto `54de58ca0`) | **1** - `DirectorHubTests` |
| **C1** | **`a084c422c` - this branch's base, NONE of my commits** | **0 (green, 2752)** |
| **C2** | **`54de58ca0` - this branch's base, NONE of my commits** | **2** |
| **C3** | **`54de58ca0` - this branch's base, NONE of my commits** | **13** |

The control worktree was cut from `origin/main` and pinned to this branch's exact base, so the ONLY
difference between it and the branch runs is this work item's commits.

**C3's thirteen failures are all in `TenantRegistryEmailBackfillTests` and
`GatewayStatsStoreRefusalLeavesTheStoreUntouchedTests`** - classes this branch has never touched - and
**all thirteen carry the same error class as every failure above**: a SQLite lifecycle error, either
`The collection has been marked as complete with regards to additions` or
`ObjectDisposedException: SQLitePCL.sqlite3`.

**So the control fails harder than the branch does.** 0, 2 and 13 against the branch's 2, 5 and 1. A
change that caused this would not produce a control that fails worse than the tree under test.

## Why one green control was not enough

C1 was green, and a green control is exactly where it would have been convenient to stop and say "main is
fine, so the red must be mine" - or, worse, to stop after run 1 and say "not my file, moving on". Neither
is a conclusion a single sample supports for an INTERMITTENT fault: one green proves nothing about a fault
that appears in a third of runs, and the failing sets being disjoint each time is the signature of one.
Three control runs were needed before the comparison meant anything.

## The mechanism, named rather than guessed

`GatewayDatabase.Dispose` calls `SqliteConnection.ClearAllPools()`, which is **process-wide**. The suite
split (`a084c422c`) moved about 2,750 tests into `CcDirector.Gateway.UnitTests` and set that assembly to
run **four collections at once**; **79 test classes in it share `GatewayDbTestHarness`**. So one class
finishing its work clears the connection pool out from under any other class that is mid-query. Every
failure observed here is that collision.

This is a real defect in the split, not a flake to be re-run away, and it will keep costing whoever meets
it next - the failing test is always in a file the reader has never opened, which is the most expensive
shape a red can have. It belongs to whoever owns the split; this work item is not the place to fix it, and
`SqliteConnection.ClearAllPools()` cannot simply be deleted (the harness needs it to release the file it is
about to remove).

The split's own commit message documents the same shape from its own testing - "three consecutive runs
failed 1, then 8, then 7 tests - a DIFFERENT set every time, and every one of them passing in isolation" -
and capped parallelism at four to control it. Four is evidently still too many on a machine carrying the
load this one was carrying tonight.

## What this branch's own facts did

**This work item's five fast-suite facts passed in all three branch runs**, named individually in each TRX:
`EveryShapeOfRow_FoldsToTheSameAnswerItDidBefore`, `TheSnapshotAndThePerSessionReaders_CannotDisagree`,
`TheSetBasedRead_IsScopedToItsTenant`, `TheReadAsksOnlyAboutTheSessionsBeingFolded`, and
`ADeferredHold_IsNotTheSameAsNoHold_WhichIsTheDistinctionABatchCanLose`.

That is the claim this branch can make, and it is deliberately narrower than "the gate is green": the gate
is not green, on this machine, for anybody.
