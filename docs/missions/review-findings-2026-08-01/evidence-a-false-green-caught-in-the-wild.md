# A false green, caught in the wild, on this mission

Every argument in this repository for judging a test run by TRX outcome AND test count - rather than by
the console summary - has until now been a story about something that nearly happened once. This is the
thing happening, on a real run, caught.

It was produced by accident. The Gateway suite was killed part way through, deliberately, because it was
measuring a tree that would never land while holding the machine-wide suite lock. What it printed on its
way out is the artifact.

## What the console said

```
Passed!  - Failed:     0, Passed:  4628, Skipped:     6, Total:  4634, Duration: 30 m 10 s - CcDirector.Gateway.Tests.dll (net10.0)
```

A clean green. Zero failures. Nothing in that line is false, and nothing in it is a warning.

## What had actually happened

**519 tests never ran.** The recorded baseline for this project is 5153 total and 5113 executed; this run
reports 4634 total and 4628 executed. The suite died mid-flight and the surviving processes reported
success for everything they had managed to start.

## What noticed

The TRX `ResultSummary`, verbatim:

```xml
<ResultSummary outcome="Failed">
  <Counters total="4634" executed="4628" passed="4628" failed="0"
            error="0" timeout="0" aborted="0" inconclusive="0" ... />
```

Two fields, and **both were needed**:

- `outcome="Failed"` says the RUN did not complete - which is a different question from whether its
  assertions passed.
- `total="4634"` against a baseline of 5153 says how much never ran.

Note carefully what did NOT notice: **`failed="0"`**. The failure count in the TRX itself is zero, exactly
like the console line. A gate that checked "no failures" - in the console or in the TRX - would have
called this green. The outcome and the count are the only two things in the entire artifact that
disagreed with "everything is fine".

## Why this is worth keeping

The mission's gate was defined as outcome AND count against a recorded baseline before this happened, on
the strength of a previous near-miss in which a change silently stopped 1,340 tests from running. This
run is the same failure mode occurring, in this repository, on this branch, and being caught by exactly
the mechanism that was put in place for it.

It also answers the obvious objection to that gate - that comparing counts is bureaucratic. A count
comparison is the only thing standing between "Passed! - Failed: 0" and a 519-test hole.

---

# It happened AGAIN, four hours later, and this time nobody caused it

The collapse above was self-inflicted - a run killed on purpose - so a sceptic could fairly call it
manufactured. This one is a genuine test-host crash that nobody asked for, on the post-rebase gate run of
`cc5bce077`.

## What the console said

```
Passed!  - Failed:     0, Passed:  2476, Skipped:     6, Total:  2482, Duration: 21 m 36 s - CcDirector.Gateway.Tests.dll (net10.0)
```

## What had actually happened

**2,671 tests never ran** - 2482 against the 5153 baseline. The test host crashed twenty-one minutes in.
Standard error, verbatim, which is the only place the truth appeared in plain words:

```
The active test run was aborted. Reason: Test host process crashed
Test Run Aborted.
```

The TRX agreed with the crash and not with the console: `outcome="Failed"`, `total="2482"`,
`executed="2476"`, and - once again - **`failed="0"`**.

## Two in one day, at 519 and 2,671

That is the number worth keeping. This failure mode is not rare, the console line is blind to it both
times, and on both occasions the ONLY things that noticed were the TRX outcome and the count against a
recorded baseline. A gate checking "no failures" would have called both of them green.

---

# THE CRASH FINGERPRINT, recorded BEFORE the re-run

Written down deliberately in advance. A criterion that cannot be checked is not a criterion, and a
"was it the same place?" judgement made afterwards from memory returns whichever answer is convenient -
which would be "flake", because flake is the answer that lets the work carry on.

**This crash, as measured:**

| Fact | Value |
|---|---|
| Run window | first test finished 18:44:05, last finished 19:05:37 |
| Tests reported | 2482 of a 5153 baseline; 2476 executed, 0 failed |
| Test classes that reported | 285 |
| Last class to report | `LauncherRegistryEndpointTests` - the final **twelve** results are all from it |
| Standard error | `The active test run was aborted. Reason: Test host process crashed` |
| This mission's facts | all 8 tenant-guard facts had already run and PASSED |
| Not yet reached | `TheRejectedChainUpgradesToTipTests`, `ALateStatisticsStoreReachesTheRosterTests` |

**"The same place" means, checkably, ANY of:**

1. the last class to report is `LauncherRegistryEndpointTests` again, or
2. the executed count lands within roughly 100 of 2476, or
3. any of this mission's own test classes is the last to report, or fails.

Number 3 is the one that matters most and is deliberately the widest: it is the case where the crash is
MINE, and it must not need the other two to be caught.

**If any of those holds, it is a FINDING and gets investigated - not a third re-run.** A crash that
reproduces is a defect. Re-running until green is exactly how a real defect gets laundered into a flake,
and the laundering is invisible afterwards because only the green run gets reported.

If none holds, it is the known host-crash mode this suite has exhibited before, the re-run stands, and
this entry records that the judgement was made against criteria fixed in advance rather than chosen to
fit the outcome.

## The verdict, evaluated against those criteria

The re-run completed: `outcome="Completed"`, total 5173, executed 5167, 0 failed.

| Criterion, as written above | Result |
|---|---|
| 1. Last class is `LauncherRegistryEndpointTests` again | **NO** - it is `NetDiagMonitorTests`, finishing 19:55:11 |
| 2. Executed within ~100 of 2476 | **NO** - executed 5167, a difference of 2,691 |
| 3. Any of this mission's classes last to report, or failing | **NO** - 25 mission facts, 0 non-passed |

**NOT the same place, on all three.** The two fixtures the crash never reached -
`TheRejectedChainUpgradesToTipTests` and `ALateStatisticsStoreReachesTheRosterTests` - both ran and
passed this time, so the part of the suite that was never exercised when the host died is now covered.

The crash is therefore recorded as the known host-crash mode rather than a defect in this branch, and
that conclusion was reached by evaluating conditions written down before the second data point existed.
Had any of the three held, this section would say FINDING and the work would have stopped.

---

## The artifact

The raw TRX is 6.4 MB and is not committed. It was preserved outside the repository at the time, in this
session's scratchpad and beside the mission's inspection reports in `devthrottle_internal`. Everything
load-bearing about it is quoted above - the console line, the `ResultSummary` element, and the baseline it
is measured against, which is recorded in `test-baseline.md` in this same directory.
