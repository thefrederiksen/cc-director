# Clean up Your Throttle - running state

This is the compact handoff note. A fresh Manager needs THIS FILE, the brief beside it, and
`cc-devthrottle workflow instructions mission`. Nothing else. Keep it current and short.

**Updated:** 2026-09-05, by the Architect, opening phase two.

## Where the work lives

- Product repository worktree: `D:/ReposFred/devthrottle-throttle`, branch
  `mission/clean-up-your-throttle`.
- Internal repository worktree: `D:/ReposFred/devthrottle_internal-throttle`, branch
  `mission/clean-up-your-throttle`. Not touched until phase five.

## Current phase

**Phase two - the fixes that actually move the number.** Open.

Read the AMENDMENT at the foot of the brief first. Rule R2 is revised: the two holes the mission was
chartered to fix are worth nothing on the owner's real week, and three other defects are the whole
gap. Rulings R10 to R14 govern this phase.

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

## The next Worker tasks, in order

1. **Measure the shape of the 594 first, before any fix.** Their character volume is recorded even
   though their turns are not. Report the distribution - median, tenth and ninetieth percentile, and
   how many are under five characters. **If most of them turn out to be bare confirmations rather
   than composed prompts, RAISE TO THE ARCHITECT IMMEDIATELY and stop.** That would make counting
   them as turns the wrong fix and it is a question for the owner, not for this mission. The prior
   evidence is that they are real: the mentor report already sees 583 of them as prompts carrying
   text in the agent's own transcript.
2. Defect one, with its guard, as one slice (R11, R14) - the fix and the corrected page disclosure
   together.
3. Defect two: **prove the mechanism first**, then fix the cause (R11).
4. The fleet-message origin (R12).
5. Phone voice through the one-shot transcription (R10).
6. The validated forensic repair of the stored history, or the honest truncation (R13).

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
