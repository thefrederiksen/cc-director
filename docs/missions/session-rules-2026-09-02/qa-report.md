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

To be filled in honestly at the end, and never left empty.

As of the end of phase 1: rows 1 to 5 above are all still PENDING. Nothing has fired, nothing has
been typed, no model has built a rule from English, and there is no user interface. Phase 1's own
list of what it did not prove is in `phase-1-report.md` and is carried forward here rather than
restated.

---

## Phase 1 - the rule store, the contract, the primitives (done)

Full account, with every red quoted before its green: `phase-1-report.md`. Branch
`mission/session-rules-p1`, head `48eeb1e83`.

What phase 1 proved, all on `48eeb1e83` unless a red is named:

- A rule ROUND-TRIPS through the store - the account's sentence, the derived screen description, the
  trigger words, the derived checks, scope, cooldown, daily cap and state - read back through a
  second store over the same database.
- A rule naming a check that does not exist is REFUSED at write time with a stated reason, proved by
  writing a refused rule and reading the reason, and nothing is stored.
- A rule handing the wrong arguments to a real check is REFUSED with a stated reason - thirteen
  distinct wrong-argument cases.
- The check registry is DERIVED by reflection, non-empty, and complete: the test finds the attributed
  methods with its own independent scan and requires every one to be reachable through the registry.
- The five checks have their own tests, including `is_path_inside` against `..`, a real link, and the
  prefix collision `repo-other` beside `repo`.
- A new rule is ALWAYS in dry run - `Create` has no state parameter - and a dry-run rule cannot record
  having typed anything.
- Nothing in the rules code can type into a session, proved by a reference scan of the built assembly
  whose scanner was first run against a known-BAD input and watched to fail by name.

What phase 1 did NOT prove: the parked `CcDirector.Gateway.Tests` suite did not run (the machine-wide
lock was held, and that suite measures 48.88 minutes against a 45-minute maximum wait, so a queued run
cannot acquire it); nothing ran against a live Postgres; no rule has fired; no model has built a rule
from English; and the decline is stored but not yet earned by an agent.

---

## Runs

Every number carries its exit code and the commit it ran on. A run is only evidence for the tree it
ran on.

| What ran | Commit | Exit code | Result |
| --- | --- | --- | --- |
| Rules tests, first run against unwritten code | `a8259bcbb` | 1 | 33 failed, 1 passed (the one pass is the instrument check) |
| Rules tests, after the checks and registry | `84c25911e` | 0 | 34 passed, 0 failed |
| Validator tests, first run against unwritten code | `84c25911e` | 1 | 18 failed, 0 passed |
| All rules tests, after the validator | `5523025ec` | 0 | 52 passed, 0 failed |
| Store tests, first run against unwritten code | `522b1cee5` | 1 | 21 failed, 0 passed |
| Store tests, after the store | `515759985` | 0 | 21 passed, 0 failed |
| Types-nothing guard against a known-BAD input | `c991921d2` | 1 | 1 failed, 2 passed - named the offending type |
| All rules tests, probe removed | `7a7422119` | 0 | 76 passed, 0 failed |
| Local gate, which caught the tenant-key defect | `7a7422119` | 1 | 1 failed, 3310 passed |
| Tenant guard and rules tests, after the key fix | `48eeb1e83` | 0 | 81 passed, 0 failed |
| Local gate, `scripts/test-local.ps1` | `48eeb1e83` | 0 | all 9 projects Completed; 4604 passed, 2 skipped |
| `has-pending-model-changes`, SQLite | `48eeb1e83` | 0 | no changes since the last migration |
| `has-pending-model-changes`, Postgres | `48eeb1e83` | 0 | no changes since the last migration |
| Parked `CcDirector.Gateway.Tests` | | | **PENDING** - machine-wide lock held; see phase 1 report |
