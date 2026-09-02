# Session Rules - the QA report

**Status: IN PROGRESS. Every row below is either evidence with its exit code and the commit it ran
on, or the word PENDING. Nothing here is written ahead of the run that proves it.**

This report accumulates from the first proof onward rather than being written at the end, so that a
run which stops early still leaves an honest account rather than nothing. It is emailed to the owner
once, when the mission finishes or when it stops.

Mission: Session Rules. Branch `mission/session-rules`, cut from `origin/main` at `fac79fb56`.
Issue thefrederiksen/devthrottle#2644.

---

## What was asked for

> The goal is a QA report that clearly shows this feature working and shows also the rules of how to
> do something. The QA report needs to show that it can accept something into a screen if a rule is
> triggered. You should be able to test that fairly simply by just putting words in a terminal screen
> and then seeing that you can do something when those words happen automatically.

---

## 1. A rule created from plain English

The sentence in, the built rule out, quoted.

**PENDING.**

## 2. The headline - words on a screen, and something happens because a rule said so

The screen before, the rule that matched, what it decided, what it typed, the screen after.

**PENDING.**

## 3. The real case - a session blocked on a provider limit recovers with nobody watching

Verified by a COMPLETED TURN, not by an endpoint's own response and not by the reported current model
alone, which is turn-end truth and lags a slash-command switch.

**PENDING.**

## 4. The negative control - where the boundary is

Two things, and neither is optional. A report showing only successes has not shown the feature has a
boundary.

- A session merely DISCUSSING a usage limit is not convicted.
- A rule DECLINES a screen its instruction does not cover, and the reason is recorded.

**PENDING.**

## 5. How to write a rule

Short, plain, from the real screen.

**PENDING.**

## 6. What is NOT proven

To be filled in honestly at the end, and never left empty. As of the start of the mission, nothing is
proven, and this section will say exactly which of the rows above were never reached.

---

## Runs

Every number carries its exit code and the commit it ran on. A run is only evidence for the tree it
ran on.

| What ran | Commit | Exit code | Result |
| --- | --- | --- | --- |
| (none yet) | | | |
