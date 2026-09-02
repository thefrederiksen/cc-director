# Session Rules - the running handoff

The Architect keeps this current. It is the ONLY thing a fresh Manager needs, alongside `brief.md`
(the work), `plan.md` (the running order and the rulings) and the fleet's `mission` workflow (the
conduct). Not a transcript. Not a history.

---

## Where things stand

- **Phase:** FIX ROUND A - the disposition of the independent inspection and of the two defects
  phase 2 found in its own work. Read `fix-round-a.md`; it is the task.
- **Mission branch:** `mission/session-rules`, pushed. Phases 1 and 2 and the inspection are merged.
- **Landed on main so far:** NOTHING, and nothing lands until this round is done and re-inspected.

**Phase 1 is done:** the rule store, the contract, the reflection-derived check registry, the five
verified checks, the write-time validator. `phase-1-report.md`.

**Phase 2 is done and THE DEMONSTRATION IS CAPTURED** - the mission's headline, already safe on the
branch. On a real session, with a real screen read and a rule from the real store, `/usage-credits`
appears on the terminal with nobody having typed it, and the shell's own rejection of the command is
unambiguous evidence that the text arrived and was submitted. Both negative controls are live: a rule
declined a screen that merely DISCUSSED a limit, and a second rule declined a screen its instruction
does not reach. A live abandonment was captured too, where the screen moved on mid-decision.
`phase-2-report.md`, and the evidence itself is in `qa-report.md`.

**An independent inspection of landing A returned nine findings, three of them blocking**
(`inspection-a.md`). Phase 2 self-reported two more. The Architect's disposition of all of them,
including three new rulings A11, A12 and A13, is `fix-round-a.md`.

## The next Manager's task

Work `fix-round-a.md`, worst first, on a phase branch cut from `origin/mission/session-rules`, in its
OWN worktree. Nothing else. Do not rebuild the demonstration and do not start a new feature.

The single most important item is A13: two red-first claims in the phase 1 report do not reproduce
from the commits they name, and the filtered runner exits 0 on `No test matches`. **That report goes
to the owner. A false claim in it is worse than a missing feature.** Repair it by committing a
reproducible red probe, or delete the claim and say so plainly in what is not proven - never by
restating an unreproducible number in softer words.

## Branch and worktree convention on this mission

- Manager: worktree `D:\ReposFred\devthrottle-session-rules-p<N>`, branch
  `mission/session-rules-p<N>`, cut from `origin/mission/session-rules`.
- Worker: its OWN worktree, cut from the Manager's phase branch. Never the Manager's tree.
- The Architect merges the phase branch into `mission/session-rules`, and only the Architect lands
  anything on `main`.

## Migration slot

`mission/terminal-rules` holds an unlanded screen-store migration dated 2026-09-02 on top of the
same base. Generate this mission's migration on top of `origin/main` and expect to regenerate the
model snapshot if that mission lands first. Fetch with `--prune`; test PRESENCE ON MAIN, not
difference from the merge base.

---

## Migration slot sweep, run by the Architect 2026-09-02

Method (ruling A10): fetch with `--prune`, then for each candidate branch list its migration files
and test whether each one is PRESENT ON `origin/main`. Never test difference from the merge base - a
squash-merged branch still differs and therefore still votes, which produced a false holder on the
sister mission earlier the same day.

Result - three holders besides this mission:

| Branch | Migrations absent from origin/main |
| --- | --- |
| `origin/mission/terminal-rules` | `20260902154804_AddSessionScreens` |
| `origin/prompt-delete-erases` (pull request 2379, open, untouched since 8 August) | `20260802044500_PromptErasureWatermark`, `20260802141655_RollupMaterialReadTime`, `20260802153217_SealBoundAndFirstSeen` |
| `mission/session-rules-p1` (this mission) | `20260902191922_AddSessionRules` |

The Terminal Rules mission has given this one PRIORITY on the slot and will regenerate its own model
snapshot if the two collide. Take the slot; do not sequence anything on that mission's timing. Whoever
lands last regenerates - that is mechanical, not a dispute.
