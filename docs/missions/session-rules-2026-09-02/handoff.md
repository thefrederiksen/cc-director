# Session Rules - the running handoff

The Architect keeps this current. It is the ONLY thing a fresh Manager needs, alongside `brief.md`
(the work), `plan.md` (the running order and the rulings) and the fleet's `mission` workflow (the
conduct). Not a transcript. Not a history.

---

## Where things stand

- **Phase:** FIX ROUND B, in flight on `mission/session-rules-fb`. Twelve findings from the second
  independent inspection (`inspection-b.md`), four of them critical.
- **Mission branch:** `mission/session-rules`, pushed. Phases 1 and 2, fix round A, and both
  inspections are merged.
- **Landed on main so far:** NOTHING.

**Phase 1 is done:** the rule store, the contract, the reflection-derived check registry, the five
verified checks, the write-time validator.

**Phase 2 is done and THE DEMONSTRATION IS CAPTURED** - the mission's headline, safe on the branch.
On a real session, `/usage-credits` appears on the terminal with nobody having typed it, and the
shell's own rejection of the command proves the text arrived and was submitted. Both negative
controls are live, and a live abandonment was captured where the screen moved mid-decision.

**Fix round A is done** - the first inspection's nine findings, including deleting two red-first
claims that did not reproduce and closing the fail-open that let them stand.

**Fix round B is in flight** - the second inspection found the ceiling could be walked through by a
race (proven, not asserted), the human promotion boundary is still a convention any code could mint,
typing happens before the firing record is durably accepted, and one more set of red-first claims
does not reproduce.

## The running order from here

1. Fix round B finishes and is pushed.
2. **Inspection C**, scoped TIGHTLY to the fix round B diff and to one question per finding: is it
   really closed, or has it moved? A fix round is new writing and law 3 applies to it like any other.
3. The Architect merges to `main` through a pull request, and takes the whole-solution run on that
   pull request as the coverage the parked suite never gave - the machine-wide lock has been held by
   other missions and a release gate all day, and queueing behind a 48.88-minute suite on a
   45-minute wait can only ever produce a zero-test timeout.
4. The Architect finishes `qa-report.md` and emails the owner ONCE.

## Two things the Architect owes the report, and nobody else should write them

- **Row 5, how to write a rule**, is answerable now: `how-to-write-a-rule.md` was corrected to
  separate what is BUILT today (rules created over the Gateway interface, derived parts written by
  hand) from what is DESIGNED BUT NOT BUILT (saying it in English and having a model build it). An
  earlier draft told the owner to press a button that does not exist.
- **Row 1 stays PENDING and that is the honest answer.** Authoring by conversation is not built, and
  a run that stops before it is built says so rather than implying otherwise.

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
