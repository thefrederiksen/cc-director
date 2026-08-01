# The gate baseline - review findings mission, 1 August 2026

Every run on this mission's branch is judged against the numbers below. A run counts as green only when
BOTH hold, per work item, per project:

1. the TRX `ResultSummary/@outcome` is `Completed`, and
2. the total test count is at or above the baseline recorded here.

**The console summary is not evidence and is never the verdict.** `dotnet test` prints
`Passed! - Failed: 0` for a run that passed everything it managed to START, so a crashed test host
produces a green with a collapsed count that nobody looks at. That shape has already very nearly certified
a change which silently stopped 1,340 tests from running. `scripts\test-local.ps1` now writes a TRX per
project and prints the outcome and the total beside each result, so the pair above is readable without
anyone remembering to go and find it.

## What the baseline was measured on

- **Commit:** `8d92a3958` (`origin/main` at the time the mission worktree was cut), plus `e71f4e99d`, which
  adds the TRX emission to `scripts\test-local.ps1` and changes nothing about which tests run.
- **Tree:** `D:\ReposFred\dt-review-findings`, branch `stats-hosted-serve`, before any product edit.
- **Machine:** SOREN_NORTH, `Debug`, .NET SDK 10.0.302.
- **Run started:** 2026-08-01 10:58 local.
- **Gated live proofs:** NOT configured for this run. `CC_GATEWAY_TEST_PG_CONNECTION`,
  `CC_GATEWAY_TEST_PG_STATS_CONNECTION` and `CC_GATEWAY_DB_CONNECTION` were all unset, so every
  PostgreSQL-gated fact reported SKIPPED. The skipped facts are inside the totals below as
  total-minus-executed, which is why that column is recorded rather than only the passed count: a later run
  WITH the rig up will execute more of the same total, and the count must not read as a regression.

## The numbers

| Project | Outcome | Total | Executed | Passed | Failed | Skipped |
|---|---|---|---|---|---|---|
| CcDirector.Core.Tests | Completed | 4179 | 4171 | 4171 | 0 | 8 |
| CcDirector.Avalonia.Tests | Completed | 353 | 353 | 353 | 0 | 0 |
| CcDirector.Launcher.Tests | Completed | 110 | 110 | 110 | 0 | 0 |
| CcDirector.HostedAgent.Tests | Completed | 88 | 88 | 88 | 0 | 0 |
| CcDirector.Engine.Tests | Completed | 63 | 63 | 63 | 0 | 0 |
| CcDirector.Terminal.Avalonia.Tests | Completed | 24 | 24 | 24 | 0 | 0 |
| **CcDirector.Gateway.Tests** | **Completed** | **5153** | **5113** | **5113** | **0** | **40** |

The six TRX files these rows were read from are kept outside the repository, in this session's scratchpad
at `baseline-trx\`, so the numbers can be re-derived rather than taken on trust.

## How the Gateway row was measured, and why it was measured separately

The first baseline run was STOPPED BY THE HARNESS, not by a failure, while the Gateway suite was still
executing - roughly thirty-two minutes in on a heavily loaded machine, and its test host was killed with
the rest of the process tree. Six projects had already written their TRX; the Gateway suite had not.

It was re-measured against the SAME baseline assemblies, WITHOUT a rebuild, so the row still describes
`8d92a3958`. `CcDirector.Gateway.Tests.dll` was written at 10:58:22 (5,934,592 bytes) and the earliest
product edit of this mission was made at 11:12:12; the whole `bin\Debug\net10.0` directory was verified
to contain nothing newer than 11:00 immediately before the run started, and the directory was copied to
this session's scratchpad first so that a stray rebuild would cost a restore rather than the baseline.
A stale-artifact check was worth doing: five PRE-COMPILE files under `src\CcDirector.Gateway\obj` carried
an 11:30:43 stamp from an editor design-time pass, and no `.dll` or `.pdb` anywhere had moved.

Two operational notes worth keeping, because both cost time:

- **Run it detached.** The re-run went out through `Start-Process`, outside the harness process tree, so a
  harness kill could not take it down the way it took down the first one.
- **Judge liveness by processor time, not by the clock.** The Gateway suite took **1 hour 1 minute** here
  against a typical nine minutes, because several working trees and an unrelated build were competing for
  the machine. Two CPU readings a minute apart showed it working throughout. Elapsed time would have
  called it hung four times over.

The run waited for the machine-wide suite lock rather than queueing behind another working tree's run.

## The gate for W1 is STRONGER than this baseline

An independent inspection made the point that this document's own numbers prove: the run above had
`CC_GATEWAY_TEST_PG_STATS_CONNECTION` unset, so **every hosted statistics acceptance fact reported
SKIPPED**. A green run in that state proves compilation and the self-host controls - it does not prove the
hosted behaviour W1 exists to deliver. Removing the hosted wiring entirely could leave it green.

So the Architect has ruled that W1's gate additionally requires **the six hosted facts to appear in the
TRX as EXECUTED and passed, with the PostgreSQL rig up**, recorded here as evidence. A skipped fact does
not gate this work.
