# Session Rules - Architect handover, 2026-09-04

You are the Architect of this mission. This is a briefing, not the archive. The archive is
`handoff.md` beside this file - read that SECOND, after this, and read the rulings files it names
when you need them. Everything the previous Architect knew is in files; nothing important lived only
in its head.

## 1. What I am doing

Making DevThrottle's Session Rules feature actually work, and producing a QA report with screenshots
that shows three rules set up AND triggered: a usage limit (wait for the reset, then continue), a
provider outage (wait, then start it back up), and a negative control - a LIVE rule declining a screen
that carries its own trigger words but is not the session's own state. The last one is the proof the
engine judges rather than pattern-matches, and it is the one the owner cares most about.

**The report is the deliverable.** Everything else exists to make it possible and honest.

## 2. Where I got to

**Proven and merged before this mission:** the engine's first half is on `main` (pull request 2665) -
the rule store, five verified checks, the write-time validator, the evaluator, dry run and promotion.

**Built, inspected three times, sixteen defects fixed, NOT YET MERGED:** authoring in plain English,
the grounding re-architecture, the Rules page, the `cc-devthrottle rule` command group, and the
session-key guard. Nothing from this mission has reached `main`.

**Two defects found that were already shipped and broken:** promotion could never have worked for
anyone over HTTP (the grant read a request item nothing writes), and the whole agent-facing command
line answered 403 because `SessionKeyGuard` never learned the rule routes. Both fixed.

**Phase 0 (harness) is done and it is the most valuable thing built.** 32 real captured screens, 12
that should act and 20 that should decline, plus 3 rule sentences, run through the production
evaluator. It proved the engine never acted correctly on a single real limit screen.

**Phase 1 (run-time contract) is built and measured, and is mid-fix.** See section 3.

## 3. The exact next action

**Wait for session `53020892` ("Session Rules - Phase 1 Measurement") to report.** It is running the
owner's chosen fix. Do not seat anything over it and do not re-run its measurement.

It is adding a second cheap question that runs ONLY when the fast model answers "act", asking whether
the screen is the session's OWN state or a report about something else - then re-measuring against the
frozen 32-case corpus, three runs, both models.

**The gate is zero wrong negatives with the positives not regressing. If it does not reach zero, it
STOPS** and we fall back to the thinking model and report the feature honestly as safe but rarely
acting. **Do not tune until it passes.**

When it reports, in this order:

1. **Land the stack on `main`.** The order and the reasoning are in `handoff.md` under "THE LANDING
   PLAN" - follow it, it was verified by diffing. Briefly: land `mission/rules-fix-f` (which contains
   the authoring feature plus all three fix rounds), **close pull requests 2671 and 2672 as superseded
   rather than merging them**, then `mission/rules-p0`, then `mission/rules-p1`. Only documents
   conflict between branches; a source conflict means something moved.
2. **Run the full parked `Gateway.Tests` ONCE, yourself, on the final merged tree.** This is ruling
   E4 and it is the Architect's job - no seat can do it (a ten-minute tool cap kills it and queueing
   seats starve it).
3. **One inspection of the MERGED TREE** by a different agent family (Codex). Not another increment
   inspection - the trend was 10 findings, then 4, then 2, and increment inspection has reached
   diminishing returns.
4. **Phase 2, the clock.** Brief written: `phase-2-clock-brief.md`. Scenario A cannot be proven
   without it.
5. **The three demonstrations, then the QA report.** Brief written: `demonstrations-brief.md`.

## 4. Decisions and why - do not re-litigate these

- **The demonstrations run on an ISOLATED LOCAL rig**, never production. Recipe in `qa-report.md`
  section 2 and `scripts/phase2-gateway-proof.ps1`. Director slot 6 or above; slots 1-5 and the
  installed app are the owner's.
- **Grounding proves a quote came from the screen; it CANNOT tell whose state the screen describes.**
  Measured. This is why a model judgement is needed at all, and it means the feature's safety rests on
  one judgement with ceilings and dry run behind it - NOT on grounding.
- **The ceiling bounds (cooldown 60s to 24h, daily cap 1 to 100) are the Architect's numbers, not the
  owner's.** The report must say so; he can widen them.
- **Phase 3 shrank.** Ruling D2 moved the screen read onto the Gateway, which was the larger half of
  it. What remains is the conversation loop only.

## 5. Traps

- **Absence becoming a permissive value is this code's recurring habit** - found five times. Before
  accepting any "it works", ask what happens when the value is missing, null or empty.
- **Green suites here certify nothing about reachability.** Three defects were proven by constructing
  an object directly instead of driving a real request. Two of them shipped.
- **The local gate has reported FAILED with no results file on a fully green run** - a budget-ceiling
  artefact. Read the result file, not the summary line.
- **A test can depend on a defect without asserting it.** One did.
- **Inspectors get reaped fast.** Two of my acceptance replies never landed because the seat was
  already gone. Write rulings to files; the file is the record.

## 6. State

- Repository `D:\ReposFred\devthrottle`. My worktree was `D:\ReposFred\devthrottle-rule-authoring` on
  branch `rule-authoring-by-conversation` - that branch is the mission's DOCUMENT of record; push
  rulings there.
- Branch tips at handover: `main` `3f2e2b652`, `rule-authoring-by-conversation` `34bd74153`,
  `mission/rules-fix-f` `36ecfba7a`, `mission/rules-p0` `2145aae79`, `mission/rules-p1` `bfcad4eca`.
- Open pull requests: **2671** and **2672**, both marked do-not-merge, both to be CLOSED as superseded.
- Live seat: `53020892`, Phase 1 Measurement. Everything else is reaped.
- **Background monitors die with me.** Re-arm any watch you need by hand.
- Postgres proof rig container `cc-pg-test` on port 55432 must be up or the local gate is red for
  reasons unrelated to your change.

## 7. What I did NOT verify

- **I never ran the full parked `Gateway.Tests` suite.** Nobody on this mission has. Every claim of a
  green gate excludes it.
- **Nothing has been driven in a browser.** The Rules page has unit tests and a typecheck only.
- **No rule has ever been drafted against a real Director's screen** through the production
  locate-and-read path; every test substitutes the reader seam. An inspector cleared it by reading the
  code, not by running it.
- **The isolated rig has not been stood up during this mission.** I inherited the recipe from phase 2's
  report and confirmed the script exists. I did not execute it.
- **The three scenarios are all unproven.** Nothing has been demonstrated end to end by me.
- **Second-hand:** the June turn-package screens in the corpus, and phase 2's demonstration of a rule
  firing on a real session, are inherited from earlier sessions' reports. I did not re-run either.
- **The real usage-limit capture in `evidence/` is a SCREEN, not a recovery.** Nothing typed, nothing
  waited, no rule fired. It does not close row 3.
