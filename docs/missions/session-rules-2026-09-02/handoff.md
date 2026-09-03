# Session Rules - the running handoff

The Architect keeps this current. It is the ONLY thing a fresh Manager needs, alongside
`implementation-plan-for-the-architect.html` (the settled design, the phases, the acceptance rows)
and the fleet's `mission` workflow (the conduct). Not a transcript. Not a history.

**Last updated: 2026-09-03 by the Architect, on evidence, replacing a stale version that said
NOTHING had landed on main.**

---

## Where things stand, verified against origin/main

`origin/main` is `2a8679007`. Verified by reading `origin/main` directly, not a working tree.

**ALREADY ON MAIN.** The engine's first half shipped in pull request 2665 (`d447736c1`): the
tenant-scoped rule and firing store with its migration, the five verified checks and the
reflection-derived registry, the write-time validator, the evaluator, the dry-run shape and the
promotion boundary, and the demonstration that a rule fires on a real session. The mission record
through fix round B and inspections A and B is on main too.

**LANDING NOW - the authoring half, built in the worktree and uncommitted until today.** Two slices,
two stacked pull requests, both open:

| Slice | Pull request | Branch | What |
| --- | --- | --- | --- |
| Gateway | 2671 | `mission/rules-authoring-gateway` | `RuleDraftContract`, `RuleAuthor`, the draft route, grounding, agent scope, the cross-tenant probe |
| Clients | 2672 | `rule-authoring-by-conversation` | The Rules page, `client-core/rules`, the `cc-devthrottle rule` command group, the mission record |

2672 is stacked on 2671 and retargets to `main` when 2671 merges.

**The gate, run by the Architect on the branch, not taken on trust:**

| Suite | Result |
| --- | --- |
| `.\scripts\test-local.ps1` (9 projects) | green, 4,848 tests, every outcome Completed |
| `Gateway.Tests` (parked) filtered to the two new classes | green, 7 tests |
| Every web workspace + `npm run typecheck` | green - 979 + 106 + 292 + 14 |
| `pytest tools/cc-devthrottle/tests/test_rule_ops.py` | green, 10 tests |

The Postgres proof rig was already up on port 55432 (container `cc-pg-test`); without it this gate is
red by default on this machine and the red is environmental. Full parked `Gateway.Tests` is the one
remaining coverage gap and is owed before 2671 merges, because that slice touches `GatewayHost.cs`.

**Inspection D is in flight** - an independent inspector from a different agent family, on its own
worktree `D:/ReposFred/devthrottle-inspect-d`, brief at `inspection-d-brief.md`, verdict due at
`inspection-d.md`.

## The phases still to build, in dependency order

From `implementation-plan-for-the-architect.html`, which holds the acceptance row for each. Do not
re-derive the design; it is settled.

0. **The harness of real captured screens.** Nothing later is trusted until it exists. At least 20
   real cases, at least half NEGATIVE (trigger words present, right answer still "decline"). Runs
   against the real engine, not a copy. Reports per model, per case: answer, right or wrong, time -
   and the count of wrong answers on negatives, which is the number that matters.
1. **Make the run-time call reliable.** The measured fact that reorders everything: the run-time call
   asks a reasoning model for ~600 characters of JSON and times out about one time in three. Move it
   to the fast model as a yes/no question - measured at 0.4s, and it answered all six screens
   correctly including the three that must be declined. Gate: zero timeouts and zero wrong answers
   on the negatives, against the Phase 0 harness.
2. **The clock.** The owner's most common case: a limit at 3am with everything sitting until morning.
   A rule says "come back at 11:50pm" and something wakes the stopped session then. `retry_delay_from`
   already ships; use the existing schedule machinery. Ceilings still bound it.
3. **The page agent.** The authoring call becomes a tool-use loop that can list sessions and read a
   screen itself, so the page stops needing a pasted screen. The command line already reaches this
   via `--session`; this is the page catching up.
4. **Pro gating.** Gate rule CREATION behind Pro. Do NOT gate RUNNING - a free account's live rules
   must keep firing, because a rule that silently stopped is a trust failure.

Deferred on the owner's call: Director-side rules (issue 2669) and the "only turn red when it needs
me" case.

## The owner's four decisions - ANSWERED, run with these

He authorised the defaults. They are no longer open.

1. Fast model for the run-time call, **with the negative control re-run as the gate**.
2. Agent credentials can do everything **except promote**.
3. Screens are captured **automatically on idle**.
4. Captured terminal text gets **short, tenant-scoped retention**, same as turns.

## What the QA report must prove, and it is the deliverable

Three scenarios, each with screenshots showing set-up AND the rule being used and triggered:

- **A - the usage limit.** Authored from the real limit screen, waits until the stated reset time,
  types continue, and the screen afterwards shows it accepted.
- **B - the provider outage.** Waits the cooldown, continues, and stops at the daily cap rather than
  looping when the error persists.
- **C - the negative control.** A LIVE rule whose trigger words are on the screen - but in
  documentation the session is reading, not in its report of its own state - DECLINES and says why.
  This is the proof it judges rather than pattern-matches, and a decline is proven by the recorded
  firing, never by the absence of a keystroke.

The report must state plainly what is proven versus only unit-tested.

## Conventions on this mission

- Manager: its own worktree `D:/ReposFred/devthrottle-rules-p<N>`, branch `mission/rules-p<N>`, cut
  from `origin/main`. Never the shared checkout, never the Architect's tree.
- Worker: its OWN worktree, cut from the Manager's phase branch.
- Only the Architect lands anything on `main`.
- Merge on local green. Never wait for continuous integration; chase a red forward.

## Migration slot

`origin/mission/terminal-rules` holds an unlanded `20260902154804_AddSessionScreens`. Phase 0 needs a
screen store, so expect a collision. Method: fetch with `--prune`, then test whether each candidate
migration is PRESENT ON `origin/main` - never difference from the merge base, which makes a
squash-merged branch vote and produced a false holder earlier. Whoever lands last regenerates the
model snapshot; that is mechanical, not a dispute.
