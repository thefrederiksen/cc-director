# Session Rules - the running handoff

The Architect keeps this current. It is the ONLY thing a fresh Manager needs, alongside `brief.md`
(the work), `plan.md` (the running order and the rulings) and the fleet's `mission` workflow (the
conduct). Not a transcript. Not a history.

---

## Where things stand

- **Phase:** 1 - the rule store, the contract, the primitives.
- **Mission branch:** `mission/session-rules`, pushed. Base `fac79fb56` (origin/main).
- **Landed on main so far:** nothing.
- **Head of the mission branch:** `21c86e85d` - brief and plan only, no product code yet.

## The next Manager's task

Build phase 1 as `plan.md` describes it, on a phase branch cut from `origin/mission/session-rules`,
in its OWN worktree. Push often. Report to the Architect when it is done and pushed, then stop - the
Architect kills the Manager and calls the inspection.

## The acceptance rows phase 1 owes

1. A rule round-trips through the store: the account's sentence, the derived screen description, the
   derived trigger words, the derived primitive calls, scope, cooldown, daily cap, state.
2. A rule naming a primitive that does not exist is REJECTED at write time with a stated reason.
3. A rule supplying the wrong arguments to a real primitive is REJECTED with a stated reason.
4. The primitive registry is DERIVED by reflection and is non-empty, and every attributed primitive
   is reachable through it.
5. The five primitives have their own unit tests, including `is_path_inside` against `..`, a
   symlink, and a prefix collision (`/repo-other` is not inside `/repo`).
6. A new rule is created in dry run and nothing in phase 1 types anything anywhere.

Every one of those is a test that was watched going RED before it went green, with both quoted.

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
