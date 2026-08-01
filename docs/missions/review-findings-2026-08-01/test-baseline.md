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
| CcDirector.Gateway.Tests | PENDING - see below | | | | | |

The six TRX files these rows were read from are kept outside the repository, in this session's scratchpad
at `baseline-trx\`, so the numbers can be re-derived rather than taken on trust.

## Why the Gateway row is pending, and what it will be measured on

The first baseline run was STOPPED BY THE HARNESS, not by a failure, while the Gateway suite was still
executing - it had been running for roughly thirty-two minutes on a heavily loaded machine and its test
host was killed with the rest of the process tree. Six projects had already written their TRX and those are
the rows above. The Gateway suite had not, so it has no baseline yet.

It will be measured against the SAME baseline assemblies, without a rebuild. The Gateway test binaries on
disk were written at 10:58:22 and the earliest product edit of this mission was made at 11:12:12, so what
is in `src\CcDirector.Gateway.Tests\bin\Debug\net10.0` is still the untouched tree. Running that project
with `--no-build` therefore measures `8d92a3958`, which is what a baseline has to mean. The alternative -
a second worktree and a second full build - would measure the same commit at greater cost.

Recording it is blocked only on the machine-wide suite lock, which another working tree
(`D:\ReposFred\dt-pathfix`) took when this run's hold on it was released. One Gateway suite runs at a time
on this machine; this mission waits its turn rather than queueing a second one behind it.
