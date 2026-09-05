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

**Phase three - the library.** Open, on ruling R9. Phase two is DONE and pushed.

Read the brief's last three sections before planning: R9 (the shape), then the rulings closing phase
two (R16 to R18 and the honesty note).

**First act, before any library work: `.\scripts	est-local.ps1 -Parked`, in full.** Phase two closed
without it and the mission is almost entirely Gateway statistics. A red there belongs to phase two.

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
