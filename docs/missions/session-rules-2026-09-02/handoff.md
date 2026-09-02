# Session Rules - the running handoff

The Architect keeps this current. It is the ONLY thing a fresh Manager needs, alongside `brief.md`
(the work), `plan.md` (the running order and the rulings) and the fleet's `mission` workflow (the
conduct). Not a transcript. Not a history.

---

## Where things stand

- **Phase:** 2 - THE THIN VERTICAL SLICE, straight to the owner's demonstration (ruling A9).
- **Mission branch:** `mission/session-rules`, pushed, head `6513a1968` - phase 1 merged in.
- **Landed on main so far:** nothing.
- **Phase 1 is DONE and accepted** by the Architect: the rule store, the contract, the
  reflection-derived check registry, the five verified checks, and the write-time validator. Its
  account, with every red quoted before its green, is `phase-1-report.md`. Local gate exit code 0,
  4604 passed, on `48eeb1e83`; the only change after that run was documentation.
- **An independent inspection of phase 1 is IN FLIGHT** with a different agent family. Its findings
  come back to a FRESH Manager, not to the one that built it, and not to the inspector to patch.
  Phase 2 is being built on top of phase 1 in parallel rather than waiting, because the owner's
  model allowance may not last the mission (A9).

## The next Manager's task

Build the THIN VERTICAL SLICE described in `demonstration-rig.md`, on a phase branch cut from
`origin/mission/session-rules`, in its OWN worktree. This is the owner's acceptance test and it is
the single most valuable thing left in the mission. It may be crude. It must be REAL.

Read `demonstration-rig.md` before anything else - it is the executable design, and it says exactly
what each demonstration proves and what it does NOT.

## The acceptance rows phase 2 owes

1. **Demonstration A, captured as an artifact.** Words go onto a real terminal screen; the session
   goes idle on its own; the rule fires on its own; something is typed; the screen after shows it.
   The screen before, the rule that matched, what the agent understood and decided, which checks ran
   with what arguments and what they answered, exactly what was typed, and the screen after - all
   quoted into `qa-report.md` with the commit and the exit code, the moment it works.
2. **The negative control N1:** a session merely DISCUSSING a usage limit is NOT convicted, and the
   record says why not. Trigger words alone cannot tell that apart from the real thing; only reading
   the screen against the instruction can.
3. **The negative control N2:** a rule DECLINES a screen its instruction does not cover, with a
   stated reason, and the decline is a RECORDED FIRING. Silence is not a decline - a rule that did
   nothing because the evaluator threw looks identical to one that declined, unless it is written
   down. Prove the decline by the PRESENCE of its record.
4. **Dry run types nothing**, proved by an instrumented send seam counted at zero, never by the
   absence of a log line.
5. **The screen is re-read immediately before acting**, and a screen that changed between the
   decision and the keystroke is abandoned with the abandonment recorded.

Everything else - authoring by conversation, the user interface, the hardening - comes after. Do not
build them. If the slice needs a rule, create it through the phase 1 store directly.

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
