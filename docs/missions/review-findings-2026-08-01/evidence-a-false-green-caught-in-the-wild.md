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

## The artifact

The raw TRX is 6.4 MB and is not committed. It was preserved outside the repository at the time, in this
session's scratchpad and beside the mission's inspection reports in `devthrottle_internal`. Everything
load-bearing about it is quoted above - the console line, the `ResultSummary` element, and the baseline it
is measured against, which is recorded in `test-baseline.md` in this same directory.
