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

---

## Inspection D came back FAIL. Fix round D is the state of the mission.

An independent inspector from a different agent family returned **ten findings: one blocker, four
high, five medium** (`inspection-d.md`). Accepted in full. **Pull requests 2671 and 2672 do NOT merge
until fix round D is done.** The rulings are in `fix-round-d.md`, one per finding, and they are the
Architect's - where a finding could be closed either by fixing code or by withdrawing a claim, the
ruling says which.

The blocker was the `SessionKeyGuard` gap the Architect had already found and seated; the inspector
finding it independently is what makes it evidence rather than an opinion.

**The one ruling that changes the architecture, D2.** Grounding - the headline safety claim - was
optional, caller-asserted, checked against different text than the model was shown, and defeatable by
whitespace. Rather than patch four holes, the draft route stops accepting a caller-supplied screen at
all: it takes a SESSION ID and the Gateway reads that session's screen itself, and the same check runs
again at the create route, which is the one write gate. That makes an ungrounded rule structurally
impossible instead of merely refused, closes finding 3 with it, and **subsumes the screen-reading half
of Phase 3** - Phase 3 becomes the conversation loop only.

**Two things the inspection CLEARED,** and they are worth keeping because they are the owner's hardest
rulings:

- **No generated code runs anywhere.** No path parses, compiles or evaluates program text; an answer
  names a registry entry and typed argument values, and no argument is interpreted as a pattern,
  expression, format string or program.
- **A draft cannot promote itself.** Draft writes nothing, create ignores state fields, the store
  constructs a dry run.

**Two numbers in fix round D are the Architect's, not the owner's, and the report must say so:** the
ceiling bounds (cooldown at least 60 seconds and at most 24 hours; daily cap at least 1 and at most
100), chosen so a live rule cannot type more than a hundred times a day. The owner can widen them. An
invented bound presented as his decision is the defect this mission keeps naming.

## Briefs now written and waiting, so a Manager can be seated without designing anything

| Brief | Covers |
| --- | --- |
| `fix-round-d.md` | The ten findings. **Next up, and it blocks both pull requests** |
| `phase-0-brief.md` | The harness. Names where real screens come from and why no screen store is to be built |
| `phase-1-fast-model-brief.md` | The run-time call. **Establishes that this is a store change with a migration, not a model swap** |
| `phase-2-clock-brief.md` | The clock. **Corrects the plan: not cron jobs, not snooze - a rule-owned wake swept in the shape of `CronEngine`** |
| `demonstrations-brief.md` | The three scenarios and what each must not be allowed to fake |

## A shipped defect the mission found: promotion has never worked over HTTP

Found by the fix round D Manager, confirmed independently by the Architect reading the constants
rather than either report: `RulePromotionGrant` reads the request item `DeviceKeyId`; `AuthMiddleware`
writes `cc.auth.DeviceKey`; nothing writes `DeviceKeyId`; and the middleware sets no authenticated
principal. So the caller lookup returned null on every real request and **every promotion over HTTP
was refused as having no caller.**

Introduced by fix round A's hardening, which was correct in intent - promotion previously took a rule
id and a timestamp and nothing else - and shipped to `main` in pull request 2665. The only promotion
this mission ever demonstrated was on 2026-09-02 under the OLD shape, before the bug existed.

No account is affected in practice, but **the promote button in a released feature could never have
worked**, and it goes in the QA report as a user-facing defect the mission found and fixed - not as an
internal note. It is also this mission's own law demonstrated on itself: a fix round is new writing and
carries a new writer's risk, and fix round A was not gated as hard as the first draft.

Three defects this mission has now found share ONE shape - a decision proven by constructing an object
directly rather than by driving the real request: the session-key guard (a route nobody classified),
this grant (a caller nobody wrote), and inspection finding 9 (a tenant that could be a constant). That
is a finding about the SUITE, not three separate slips, and the QA report should say so.

---

## THE LANDING PLAN, verified 2026-09-03. Follow this order.

Several branches are stacked and it is not obvious from the outside which contains what. This is the
map, checked by diffing rather than assumed.

| Branch | Contains | Cut from |
| --- | --- | --- |
| `mission/rules-authoring-gateway` (pull request 2671) | Authoring Gateway half + the session-key guard fix | `origin/main` |
| `rule-authoring-by-conversation` (pull request 2672) | The above + both clients + the whole mission record | 2671 |
| `mission/rules-fix-d` | The above + fix round D + fix round E | 2671, then merged 2672 |
| `mission/rules-p0` | The harness and its 32-case corpus | `origin/main` |
| `mission/rules-p1` | `p0` + `fix-d` + phase 1 | `p0`, then merged `fix-d` |

**Land in this order, each on its own pull request:**

1. **`mission/rules-fix-d`** - the authoring feature as corrected. **Close 2671 and 2672 as superseded**
   rather than merging them: their content is inside this branch, and merging them first would put the
   version two inspections rejected onto `main` and then fix it, which there is no reason to do.
2. **`mission/rules-p0`** - the harness. Independent of the above.
3. **`mission/rules-p1`** - phase 1. Its diff against `main` shrinks to just phase 1 once 1 and 2 land.
4. Phase 2 (the clock), then the demonstrations.

**Conflicts to expect, and they are only documents.** Diffing `p0` against `fix-d` shows **zero source
files in common** - the only overlap is sixteen files in
`docs/missions/session-rules-2026-09-02/`, because every branch was seeded with the mission record from
`rule-authoring-by-conversation` at a different moment. Resolve those by taking the union, preferring
the most recent version of each file. No source merge is expected and a source conflict is a signal
that something has moved, not a thing to resolve quickly.

**The full parked `Gateway.Tests` suite is the Architect's to run, ONCE, on the final merged tree**
(ruling E4). No seat runs it - four seats queueing on one machine-wide serialised lock is what starved
inspection E's own run and left a placeholder in a gate table.

## Why Phase 2 is not seated yet, and it is deliberate

Phase 2 (the clock) is on the critical path for scenario A and nobody is on it. That is a decision, not
an oversight: Phase 2 adds a wake field and a migration, Phase 1 adds `TextToType` and a migration, and
both touch the evaluator. Running them at once collides on the migration slot and on the same file.
**Seat Phase 2 when Phase 1 reports**, on top of it.

---

## Where the mission stands, 2026-09-04

**Three fix rounds and three inspections have happened**, each one finding real defects in the round
before it. That is the machinery working, not the mission going backwards - but it is worth writing
down what the trend actually is, because it is the strongest evidence about the code's state:

| Round | Inspection | Found |
| --- | --- | --- |
| The authoring slice | D | 10 findings: 1 blocker, 4 high, 5 medium |
| Fix round D | E | 4 findings: 1 high, 2 medium, 1 low |
| Fix round E | F | 2 findings: 1 high, 1 medium |

**No more separate inspection rounds. The next inspection is of the TREE THAT WILL LAND**, after phase
1 merges - inspecting the thing that actually ships rather than each increment of it. Three rounds of
increment-inspection has reached diminishing returns, and an inspection of the merged tree also catches
what merging itself introduced, which no increment inspection can see.

## The recurring defect class, and it is now the mission's headline technical finding

**Five times in one feature, an absent or unreadable value became a permissive or positive one.**

| Where | Absence became | Closed by |
| --- | --- | --- |
| A write with no scope | Every session | Fix round A |
| A draft with an empty screen | Grounding skipped entirely | Ruling D2 |
| A present-but-null `rules` field | "No rules yet" | Ruling E2 |
| A missing `scope.agent` | `agent: null`, unrestricted | Ruling F2 |
| An EMPTY trigger-word list | "grounded" | Fix round F, on the Architect's call |

The fifth is the sharpest: `WhyNotGrounded` answered GROUNDED for a rule with no words at all, inside
the single function that defines what grounding means. It was caught by a sweep that derived its 39
files from the feature guard's own predicate rather than a hand-kept list, read all 383
absence-to-value sites, and made a second pass over 21 vacuous-truth forms - the first sweep in this
mission that is itself evidence rather than a claim.

**This belongs in the QA report as a finding about how this code was written**, not as five bullet
points. It is one habit, and the sweep is the thing that found it rather than a sixth inspection.

## A second class, found twice, and it is about the TESTS

Three defects share one shape: **a decision proven by constructing an object directly rather than by
driving the real request.**

- The session-key guard: a route nobody had classified. Every suite green.
- The promotion grant: a caller nobody wrote. Shipped broken in a release. Every suite green.
- Inspection D finding 9: a tenant that could be replaced by a constant.

And a fourth, related: the round E store test **depended on the empty-word-list defect without
asserting it** - minting evidence for an empty set was the only way to reach the store's own no-words
refusal through its public door. A test can rest on a defect without ever mentioning it.

## Instrument faults seen, so a later reader does not chase them

- **The local gate has reported FAILED with NO-TRX on a fully green run.** In fix round F it printed
  FAIL for `Gateway.UnitTests` at 2m18s against the 120-second budget ceiling while that suite had in
  fact passed 3,633 tests with zero failures; a re-run came back at 1m01s. It is a budget-ceiling
  artefact, not a test failure.
- **The full parked `Gateway.Tests` cannot be run by a seat** - a ten-minute tool cap kills it and four
  seats queueing on one machine-wide lock starves it. Ruling E4: the Architect runs it once, on the
  final merged tree.

## A method note worth keeping, from the fix round F seat's own handover

**"The residual only became provable because it was NAMED in the previous round's report rather than
closed or dropped. Had I quietly unified it then, the divergence would have been fixed and nobody
would ever have seen the two copies disagree."**

That is the case for reporting a residual instead of tidying it away, and it is not an abstract one.
Fix round E's report named a hazard it had deliberately not closed: the evidence factory kept its own
second copy of the grounding check. Fix round F then changed the grounding DEFINITION to refuse a rule
that watches for nothing - and **the second copy did not learn**, and went on minting evidence for an
empty word set. **The two copies disagreed within ONE COMMIT of the definition changing.**

So the divergence a duplicated check invites was not theoretical here, and it was not predicted by
argument - it was observed, only because the duplicate had been left standing and written down. Had it
been silently unified in round E, the fix would have been correct and the evidence would not exist.

**The wordless-rule sentence now appears in three places deliberately** - the code comment in
`RuleGroundingEvidence.Minted`, the F3 section of `fix-round-f-report.md`, and the commit body of
`a185b9c61` - so quoting any one of them is quoting the evidence rather than a summary of it.

There is now exactly ONE grounding check in this feature, with three callers: the draft route, the
write gate, and the evidence factory.
