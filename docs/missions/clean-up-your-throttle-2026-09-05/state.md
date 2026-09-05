# Clean up Your Throttle - running state

This is the compact handoff note. A fresh Manager needs THIS FILE, the brief beside it, and
`cc-devthrottle workflow instructions mission`. Nothing else. Keep it current and short.

**Updated:** 2026-09-05, by the Manager, closing phase two.

## Where the work lives

- Product repository worktree: `D:/ReposFred/devthrottle-throttle`, branch
  `mission/clean-up-your-throttle`.
- Internal repository worktree: `D:/ReposFred/devthrottle_internal-throttle`, branch
  `mission/clean-up-your-throttle`. Not touched until phase five.

## Current phase

**Phase two - the fixes that actually move the number. DONE, pushed, and awaiting inspection.**
**Phase three - the shared library - is next, on the R9 ruling at the foot of the brief.**

Read the AMENDMENT at the foot of the brief, and then the R9 ruling after it. Rule R2 is revised:
the two holes the mission was chartered to fix are worth nothing on the owner's real week, and three
other defects are the whole gap.

### What phase two changed, in the order it landed

1. **Task one, the measurement that had to come first.** The 594 terminal-typed turns are composed
   prompts, not bare confirmations: median 29 characters and 6 words, 92.7 per cent carry two or more
   words, 3.3 per cent are under five characters, and the short ones are instructions rather than
   acknowledgements. `task1-shape-of-the-594.md`. The stop condition did not fire.
2. **Defect one, with its guard and the R14 disclosure, as one slice.** `StampSubmission` now stamps
   the submission event AND counts the turn, in one write; `SessionInputStats` no longer has a method
   for recording characters without a turn. Guarded in `CcDirector.Core.UnitTests`
   (`TerminalTypingIsATurnTests`), which is in the DEFAULT gate - the other Session tests are in the
   parked half and would not have run.
3. **Defect two: mechanism proven, then contained, and deliberately not fixed.**
   `task3-defect-two-mechanism.md`. The one-line containment (a REJECTED removal no longer forgets
   the counting baseline) landed with a revert-proved guard in `DirectorHubTests`.
4. **R12, the fleet-message origin.** `PromptRequest.AgentDriven` had no producer anywhere in the
   product - the Director honoured it, three tests proved it honoured it, and nothing set it. Both
   Gateway fleet paths now set it; the fanout decides from the AUTHENTICATED caller, never the
   sender field in the body.
5. **R10, phone voice through the one-shot transcription.** A transcription now hands back the id of
   the utterance it produced and the send carries it, so a dictated turn is recorded as speech. The
   claim is dropped the moment the words stop being purely a transcription - a second segment, any
   keystroke, or typed text sent alongside. Both shells, the same rule, a test on each. The page's
   voice caveat was rewritten in the same change, because the fix made the old sentence false.
6. **R13 is DISCHARGED BY R9, as truncation rather than repair.** See below.

Every fix was watched failing with its reported symptom and restored. The local gate is green
(`CcDirector.Gateway.UnitTests` 3,613 passed run directly, the rest through
`scripts	est-local.ps1`), and all four web workspaces type-check with 1,285 tests passing.

### R13: truncated, not repaired - and the truncation is measured

R13 offered one validated forensic repair of the stored history OR an honest truncation. R9 settles
it: every TURN figure moves to the submission ledger, so there is no second tally left to repair for
the number anyone reads. **The truncation is the answer, and its size is a fact rather than a
policy** - `activity_events` for `turn-submitted` runs from **2026-08-06 to now, exactly thirty
days**, measured 2026-09-05. The ledger also carries `AgentKind` and `SessionId`, so the per-agent
split and the repository join the ruling asks for are both available on it.

### What phase three inherits, unstarted

- Move every turn figure to the ledger; no two numbers from different substrates without the page
  saying so; the selector must never offer more than thirty days.
- **Decide and tell the Architect: disclose the inflated character volume, or drop the figure.** The
  Manager's recommendation is to DROP it. It is the only number left standing on the untrusted tally,
  R8 already makes turns the unit of every share, and a page that has just been made honest should
  not carry one figure whose own footnote says not to believe it.
- The conformance check over real weeks for both accounts, reusing phase one's reconstruction method.

## What is done and pushed

- The brief, and its amendment carrying rulings R10 to R14.
- `reconciliation.md` and `evidence/` - phase one's account, over 2026-W35 (Monday 24 August to
  Sunday 30 August, America/Toronto), for `soren@centerconsulting.com`.

## What phase one found, in one place

- Over the same week, same zone, same person: the report says 58.76 per cent spoken, the Your
  Throttle store says 91.46, and **the Gateway's own submission ledger says 56.83**. The report is
  close to the truth. **The 92 is the number that is wrong.**
- **Defect one - 28.3 points.** `Session.SendInput` records characters and never calls
  `InputStats.RecordTurn`, so a turn typed at the desktop terminal is not counted as a turn at all:
  594 turns, 77 per cent of the week's typing, absent from the ring's denominator. Verified
  independently by the Architect: `RecordTurn` has one call site in the product and `SendInput` does
  not reach it.
- **Defect two - 8.2 points.** 2,061 of the 3,279 stored turns for the week are restated cumulatives
  or duplicated rows, 96 per cent of it on voice. The MECHANISM IS NOT PROVEN.
- **Defect three - 0.3 points, plus the ambiguity.** `GET /stats/data` has no window: it serves every
  turn since 2026-08-02, unlabelled. Handled in phase four.
- **Both holes named in the original R2 are at zero.** The chat relay is unreachable code - verified
  independently, no construction site and no mapped route. No phone-voice-endpoint transcription
  happened in the week; the ceiling on that hole is 60 turns, 3.4 points.
- **The owner's question:** no session-to-session traffic is counted as his on either side, but 292
  of the week's 296 fleet messages were recorded as ordinary `UserInput` with no origin rather than
  as agent traffic, so both sides exclude them by accident and the agent-driven lane under-reports.
- One Gateway, two machines. The self-hosted Gateway has recorded nothing since 2026-07-21, so R1
  costs nothing.

## Phase two's task list - all six closed

1. Measure the 594 first - DONE, and the stop condition did not fire.
2. Defect one with its guard and the R14 disclosure, one slice - DONE.
3. Defect two: prove the mechanism, then fix the cause - MECHANISM PROVEN; the cause is not fixed
   here, by the Architect's R9 ruling, and the containment landed instead.
4. The fleet-message origin (R12) - DONE.
5. Phone voice through the one-shot transcription (R10) - DONE.
6. The repair or the truncation (R13) - DISCHARGED as truncation by R9; reach measured at thirty days.

## What phase two did NOT prove, said plainly

- **Why a session that is still counting has its high-water row removed, one to twenty-six times.**
  The route is proven (the row was absent and was re-inserted from zero) and the only deleter is
  named, but the trigger is not. It needs an instrumented run or a log line that does not exist.
  Out of scope now that the figure leaves that tally.
- **The one-line containment is not what produced the week's inflation.** On the hosted day examined,
  every dropped push was a snapshot and none was a remove. It closes a real hole; it is not the cure.
- **R10 and R12 did not fire in the owner's measured week**, so nothing was re-measured to show them
  moving the number. They are fixes to what COUNTS as spoken and as agent traffic, which is a
  definition, and the definition had to be right before the library is built on it.
- **The parked suites did not run.** `Core.Tests` and `Gateway.Tests` both COMPILE and their tests
  touching this work were run by name, but neither full suite was run.

## Rulings that arrived after phase two opened

- **R15 (owner, RELAYED - see the brief).** Phase four changes the default to a rolling seven days
  DIRECTLY: no sequencing, no migration note, no care for what an existing viewer saw. The page must
  still state which period it is showing - that half stands.

## R9 IS SETTLED - see the brief's final section

The shared figure derives from the SUBMISSION LEDGER, not from the `stat_delta` cumulative tally.
Defect two stops existing for it rather than being fixed. The second tally is not repaired and not
trusted; the incarnation token and the wire-contract change are OUT OF SCOPE. Reach falls to thirty
days, which is the ledger's retention, and the selector must not offer more.

Every TURN figure on the page moves to the ledger, including the per-agent split (the ledger carries
the agent kind) and the per-repository split (through the session-history join, which keeps ninety
days and carries the repository name). Character volume stays where it is and stays inflated; phase
three decides whether to disclose it or drop it and tells the Architect which.

## Open, for the Architect only

- Nothing. Phase three may proceed on the ruling above.

## Known ground already established (do not re-derive)

- The report's headline is a share of human PROMPTS, the same unit as Your Throttle's turn share.
  The report's word-based voice figure is a different metric and is out of scope.
- `GET /stats/data` is tenant-scoped and refuses when no tenant resolves. A SESSION key cannot call
  it at all - it answers 403. A conformance check needs a device key.
- There are two accounts in the mentor configuration, `soren` and `mario`. The conformance check in
  phase three runs over real weeks for both, reusing phase one's reconstruction method.
- All time for his tenant: 23,958 turns other agents drove into his sessions against 14,189 of his
  own, and that 14,189 is itself inflated.
