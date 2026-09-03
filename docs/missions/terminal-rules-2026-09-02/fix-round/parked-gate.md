# The parked gate - the runner's own verdict, and the half that is still owed

`.\scripts\test-local.ps1 -Parked`, started 2026-09-02 12:25 on commit `45529ffbb`.

## The verdict, as the runner gave it

```
TRX verdict - THIS is the gate. Outcome must be 'Completed' AND total at or above the baseline:
  CcDirector.Core.UnitTests                outcome=Completed    total=164
  CcDirector.Gateway.UnitTests             outcome=Completed    total=3262
  CcDirector.Avalonia.Tests                outcome=Completed    total=364
  CcDirector.Engine.Tests                  outcome=Completed    total=63
  CcDirector.HostedAgent.Tests             outcome=Completed    total=88
  CcDirector.Launcher.Tests                outcome=Completed    total=113
  CcDirector.Terminal.Avalonia.Tests       outcome=Completed    total=24
  cc-director-setup.Tests                  outcome=Completed    total=25
  cc-director-setup-engine.Tests           outcome=Completed    total=456
  CcDirector.Gateway.Tests                 outcome=Failed       total=0
  CcDirector.Core.Tests                    outcome=Completed    total=4382

RESULT: FAILED in 1 project(s):
  CcDirector.Gateway.Tests
```

**The parked gate is RED, and it is red because one suite DID NOT RUN.** That distinction is the whole
of this page and it must not be smoothed into either "green" or "my code broke it".

- **`Core.Tests`, the other parked suite: 4,374 passed, 8 skipped, 0 failed, 14 minutes 2 seconds.**
- **`Gateway.Tests`: total=0.** Not one test executed. The runner's verdict requires the total to be at
  or above the baseline, so a zero is a failure, which is exactly the right shape - a suite that did not
  run must never read as a suite that passed.

## Why it did not run - and why the log's own reason is not the cause

The Gateway suite takes a machine-wide, per-user lock because it is destroyed by concurrent runs. This
run spent its entire life QUEUED behind another worktree and never started testing:

```
2026-09-02 12:26:06 pid 50140: [gateway-test-lock] WAITING. Another run of CcDirector.Gateway.Tests
holds the per-user test lock ... Holder: process 55428, started 2026-09-02T16:25:21Z, owner
cc-director session d0bfe0f5-..., working directory D:\ReposFred\devthrottle-redfix\src\...
...
2026-09-02 12:35:38 pid 50140: [gateway-test-lock] Still waiting after 572s.

The active test run was aborted. Reason: Test host process crashed
```

**"Test host process crashed" is what the runner SAW, and it is not what happened.** The Architect
killed pid 50140 deliberately, and said so: this mission's run was second in the machine-wide lock queue
AHEAD of the session fixing a live production outage - Your Throttle down since 14:04 UTC, every turn
driven since then unrecorded - and was putting that fix an hour and a half out. A fix round on a phase
that is neither landed nor released does not outrank a live outage.

That correction is recorded rather than smoothed over, because it is the same shape as the defects this
round exists to answer: a tool's stated reason for a failure is evidence about what the tool observed,
never about the cause. Had nobody said so, this page would have recorded a crash that never happened,
and somebody would eventually have gone looking for it in the test host.

The holder ahead of it was `devthrottle-redfix`, confirmed independently by the Gateway seat working the
incident, who also confirmed the timing: the lock had been held since 12:25:21 and this run lost queue
position rather than test progress.

**No further Gateway.Tests run has been started, and none will be until the Architect clears it.** The
lock is machine-wide, so a small filtered run takes the slot exactly like a full one; there is no
"quick" version of getting back in that queue. A process scan for `dotnet.exe` and `testhost.exe`
carrying this worktree's path returns nothing, which is how being out of the queue was established
rather than assumed.

## What is therefore NOT covered, said plainly

The default gate does not run `Gateway.Tests`, and the runner said so itself:

```
COVERAGE GAP - this change touches code covered by PARKED suite(s) that did not run:
  CcDirector.Core.Tests
  CcDirector.Gateway.Tests
```

`Core.Tests` is now covered - it ran green above. `Gateway.Tests` is NOT, and this round touches code it
covers:

- `TestScreenReader`, its harness for the reader, which changed with finding 1.
- `PostgresProviderProofTests`, which holds the live Postgres idempotency proof for the screen store -
  the one place the store's key is exercised on the provider the hosted Gateway actually runs, and
  therefore the only place finding 3's new key component is checked against Postgres rather than SQLite.
- The Gateway host wiring the endpoint suites drive.

Both projects BUILD - the local gate builds every project before running any, and it did so here - so
nothing in this round fails to compile against them. What is unproven is their behaviour. **That is the
one outstanding item on this round, and it is owed rather than waived.**

## The other discrepancy, resolved rather than averaged

Ruling 12 asked for this. The previous round reported the DEFAULT gate at exit 0; inspection 01 observed
exit 1, because `InstallAsync_FailedVenvRebuild_LeavesNoManagedShim` failed after about thirty seconds
and then passed by itself on retry. Run here, twice, that suite is fully green (456 passed) and the
runner exits 0 with every TRX outcome Completed. Both observations were honest; the case is
intermittent, and neither report was wrong about what it saw.

## One thing this cost, worth writing down

The first attempt to run this gate piped the runner through `tail`, which buffers the whole run - so the
output file sat at zero bytes for thirty-seven minutes and the run was invisible while it was happening.
The runner's OWN per-project log files were readable the whole time and are what made the progress
visible. Do not pipe a long run through a pager to read its end; read the artifacts it writes as it goes.
