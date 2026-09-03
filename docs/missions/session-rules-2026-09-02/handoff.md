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

---

## Added by the Architect, 2026-09-03, after establishing ground truth

### A defect found in the authoring slice before it merged, and it is load-bearing

`SessionKeyGuard` is a deliberate LITERAL allow list of the routes a session key may call, and
`gateway/rules` was never added to it. Observed against the live hosted Gateway with this session's
own key:

```
GET /gateway/rules -> 403 {"code":"session_key_out_of_scope"}
```

So the whole `cc-devthrottle rule` command group is refused on every call - it authenticates with
`gateway.session_key()`. "Claude Code, set up a rule" could not work at all. **Every suite was green**:
the guard is unit-tested against its own list and the Python tests mock the HTTP, so nothing connected
"a route was added" to "the guard was told". This is a known repeating failure in this repository.

Fix in flight on `mission/rules-guard`, seated as a Manager task (`TASK.md` in that worktree). The
substance was already ruled by the owner - an agent credential may do everything EXCEPT promote - so
this is implementation, not a design question. The fix is required to carry a test that DERIVES the
route set from the built application rather than from a hand-kept list, so the next route added cannot
be forgotten.

### The QA demonstrations run on an ISOLATED LOCAL rig. No production deploy is needed.

This was the mission's biggest open risk and it is closed. Phase 2's demonstration already ran this
way and the recipe is in `qa-report.md` section 2: a real Gateway built from the branch on its own
data root and port so it never touches the owner's own Gateway, a real Director on a spare slot
connected to it over the ordinary Director stream, and a real `RawCli` session running `cmd` -
deliberately a plain shell, because a command typed into it either ran or it did not and the screen
says which. Reuse it; do not redesign it, and do not deploy to production to demonstrate anything.

That run also independently confirms the Phase 1 diagnosis from the other direction: its single
run-time model call took **18.4 seconds** for 571 characters.

### Phase 0's screens come from a route a session key already has

`GET /sessions/{sessionId}/buffer` returns real terminal scrollback and a session key may call it -
verified, 386,379 characters on the first live session tried. Phase 0 does NOT build a screen store;
one already exists unlanded on `origin/mission/terminal-rules`. Full reasoning in `phase-0-brief.md`.

### The critical path to the deliverable, and what is off it

The deliverable is the QA report showing all three of the owner's scenarios set up AND triggered. The
phases are NOT equally load-bearing for that:

| Order | Work | Why it is on the path |
| --- | --- | --- |
| 1 | The guard fix | Blocks the command line entirely, and the Architect's own demonstration tooling |
| 2 | Phase 0, the harness | Phase 1 without it is an opinion, not an acceptance |
| 3 | Phase 1, the fast model | A live rule that fails one time in three is the feature failing |
| 4 | Phase 2, the clock | **Scenario A is "wait until it resets, THEN continue" - without the clock there is no wait, only an immediate firing.** This is the owner's headline case |
| 5 | The three demonstrations, into the QA report | The deliverable |
| 6 | Phase 3, the page agent | Improves the set-up screenshots; the page can already author with a screen supplied |
| 7 | Phase 4, pro gating | Real, and off the demonstration path entirely |

Scenario B (the provider outage) needs a cooldown-bounded wait and retry, which the shipped ceilings
already provide; it does not depend on the clock. Scenario C (the negative control) is largely proven
already as row 4 of `qa-report.md`, but must be RE-RUN on the fast model, because that re-run is the
stated gate on the owner's decision 1 - and it must be a LIVE rule, not a dry run.
