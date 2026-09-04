# Ruling G2 - Phase 1 missed its gate, and WHY it missed changes the question

**Round A of the Phase 1 fix is measured, pushed at `521acee6c` on `mission/rules-p1`, and it did
NOT pass.** The seat stopped rather than tuning, which is what it was told to do and what makes the
number below trustworthy.

## The measurement

The second question - asked only when the fast model answers "act", and asking whether the screen is
the session's OWN state - **halves the damage and does not reach zero.**

| | Before | After |
| --- | --- | --- |
| Wrong negatives that would have TYPED (of 60) | 15 | **7** |
| Negative cases that would type at least once | 5 | **3** |
| Positives right, every run (of 12) | 11 | 11 - unchanged, nothing regressed |
| Timeouts | 0 | 0 |
| Flip rate | - | 1 of 32 |

The gate was zero. Seven is not zero. **Phase 1 has not passed.**

## Why it misses, and this is the finding

The second question **works exactly as predicted** on the class it was designed for - a test fixture
(n07), a session listing (n11), a report about a sub-agent (n10). Those are gone.

The remainder is a **different mistake**. n16 and n18 are this session's OWN context banner and its OWN
eighty-six-percent weekly banner. The whose-state question answers "own" - **and that answer is
correct.** What is wrong is upstream of it:

- a context-window limit is not an allowance, and
- eighty-six percent used is not a stopped session.

That is the FIRST question's judgement, and **no whose-state question can ever reach it.**

## The seat's sentence that names the fix, and it is kept verbatim

> "Any new question aimed at n16 and n18 has to be a SITUATION test rather than a whose-state test -
> the instruction names a situation and both screens are the session's own state in a different one."

Which says what the first question is actually doing wrong: **it is matching subject matter, not
circumstance.** It sees limit-shaped words and concludes "blocked", when the screen says "warned, and
still working".

## The fallback is GONE, and this is new

The plan's stated fallback was the thinking model, reported honestly as "safe but rarely acting".
Measured, that sentence is too kind and must not be written:

- positives **3 of 12** right, every run
- **18 of 96 timeouts**
- act latency 44.1s median, 80.9s worst - roughly DOUBLE the cost of an act

Three of twelve with one call in five never returning is not a cautious feature. It is a feature that
does not work. **If we fall back, the honest sentence is that the feature does not function, not that
it is conservative.**

Cost of the fast model with the second question, for the record: decline 5.1s median, act 7.5s,
against 3.3s flat before.

## Status

**Open, with the owner.** The choice is one further attempt aimed at the situation confusion, measured
against the same frozen 32 cases with the same zero bar - or stop and report as it stands. Not the
Architect's to take alone: the "stop if it does not reach zero" instruction was set above this seat,
and the residual is a named class rather than noise, which is the only condition under which a new
hypothesis is not tuning.

**Task B is not started and p09 still fails on the one-space citation** - deliberately, because
stopping means stopping.

## Two instrument facts from this round, both load-bearing

1. **`mission/rules-p1` did not COMPILE its unit tests when the seat picked it up.** The fix round F
   merge left `SessionRuleStoreTests` calling `Create` without the phase 1 `textToType` argument, so
   **no test run had happened on that branch since the merge.** Fixed in `bcef6fb0a`. The seat's green
   is therefore FIRST RUN SINCE THE MERGE, not a re-confirmation, and must never be quoted as one.
2. **The seat's six `CensusRouteTenancyProbeTests` failures are evidence of nothing.** Its narrow
   filtered attempt overlapped the Architect's parked run out of `devthrottle-landing`, and the lock
   file named session `8e4f5281` as holder from 15:25:39 UTC. That is precisely the failure-nobody-caused
   the machine-wide lock exists to prevent. A clean parked run is owed.
