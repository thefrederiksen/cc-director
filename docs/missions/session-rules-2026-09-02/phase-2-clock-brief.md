# Phase 2 - the clock

The acceptance row is in `implementation-plan-for-the-architect.html`, section 6. This brief carries
the Architect's design rulings, made by reading the code rather than by reasoning from the plan, and
**one of them corrects the plan.**

**This is the owner's headline case.** A usage limit at 3am with every session sitting until morning.
Scenario A of the QA report is "wait until it resets, THEN continue" - so without the clock there is
no wait, only an immediate firing, and scenario A cannot be proven at all.

---

## RULING 1 - the plan is wrong about which machinery to reuse, and this is the correction

The plan says to use "DevThrottle's existing schedule machinery to re-evaluate". **Do not put this on
cron jobs.** Read `CronJobDto` in `CcDirector.Gateway.Contracts`: a cron job says WHEN to run, on
WHICH MACHINE, and WHAT TO RUN - and its `Action` is documented as "what the fired SESSION runs". A
cron job SPAWNS a session on a machine. It has no way to say "go and look at THIS existing parked
session again", which is the entire operation the clock needs.

Reusing it would also put an internal mechanism into a user-facing feature: every wait would appear
in the owner's own Schedule page as a mystery job he did not create and must not delete.

**Nor is the snooze machinery it.** `SnoozeRegistry` holds `sessionId -> SnoozeUntilUtc` and is
evaluated LAZILY - `IsExpired(sessionId, now)` is asked when something renders a badge. Nothing in it
ACTS when a deadline passes. It is the right data shape and the wrong mechanism.

**Build a rule-owned wake, swept by the Gateway.** Small, tenant-scoped, and owned by the feature
that needs it:

- The evaluator, on deciding "not now, come back at T", records a wake for (tenant, session, rule) at
  T. It belongs beside the firing record, because the wait IS part of the firing and the acceptance
  row requires the record to show "the wait and the wake".
- A background sweep wakes what is due. **`CronEngine` is the shape to copy, not the store to reuse**
  - it already models exactly this: a tick, a due query (`Enabled && NextRunUtc <= now`), a fire, and
  a mark-fired that records the outcome. Read it and follow it, including that a one-off fires once
  and then auto-disables.

## RULING 2 - a wake RE-EVALUATES. It does not carry out a decision made earlier.

At wake time the honest question is not "now type the thing I decided at 3am" - it is "is this still
that situation?". So the wake calls the evaluator for that session exactly as a turn-end would, and
everything the evaluator already does still applies: the free checks, the screen re-read, the
cooldown, the daily cap, and the dry-run gate.

This matters for a reason worth stating in the code: between the decision and the wake, the owner may
have fixed it himself, the session may have been killed, or the limit may have cleared early and the
session moved on. A wake that blindly types the authored text would be typing into a situation nobody
looked at. **The wake is a trigger, not a promise.**

## RULING 3 - how the evaluator gets called, and the trap in it

Today the only path in is `RuleTurnEndLauncher` hanging off `TurnEndWatcher`
(`GatewayHost.cs` around line 2550). Its comment is explicit that turn-end is "the only thing that
can wake it, so a Working session is out of its reach by construction rather than by a rule somebody
has to remember."

**You are adding a second way in, and that comment stops being true the moment you do.** So:

- The tenant must be carried on the wake row and entered the same way the launcher does
  (`enterTenantScope`), not resolved a second time. Two independent resolutions of the same question
  is the failure this codebase repeatedly names.
- A session that is WORKING when the wake fires must not be acted on. Turn-end gave that guarantee
  structurally; your path must give it explicitly, with a test, and the comment on the launcher must
  be corrected rather than left standing as a claim that is no longer true. **Deleting a guarantee's
  mechanism does not delete the sentence that promised it.**

---

## Acceptance - the row from the plan, with what each part actually takes

| Row | What it takes to pass |
| --- | --- |
| A rule scheduled to re-look fires within a small window of the stated time | On a real session, end to end, on the isolated rig. State the window you achieved, measured |
| A limit that never clears does not loop past the daily cap | Let it re-wake repeatedly and show the cap stopping it, with the cap-reached record |
| The firing record shows the wait AND the wake | Two records, or one with both, but the wait must be visible as a decision - not inferred from a gap in timestamps |
| Proven on a real or faithfully simulated limit screen | Not only in the unit suite. `retry_delay_from` already ships - use it to read the reset time |

## The rig

Do NOT deploy to production to demonstrate this. The mission's rig is an isolated local Gateway and
Director, and it is already written down: `qa-report.md` section 2 for what it was, and
`scripts/phase2-gateway-proof.ps1` for a worked example of standing up an isolated Gateway and
Director from a worktree on their own storage roots and ports, at Director slot 6 or above. The
owner's own Gateway and slots 1 to 5 are never touched.

## The gate

- `.\scripts\test-local.ps1` green. The Postgres proof rig must be up or the run is red for reasons
  that have nothing to do with you - container `cc-pg-test` on port 55432 was up on 2026-09-03.
- A store change needs its migration in the SAME change. `origin/mission/terminal-rules` holds an
  unlanded `20260902154804_AddSessionScreens` on the same base, so expect to regenerate the model
  snapshot if it lands first. Test whether a migration is PRESENT ON `origin/main` - never difference
  from the merge base, which makes a squash-merged branch vote.
- Watch every new test fail first, with the reported symptom, and quote both runs.
- ASCII only. No mention of any assistant, model, vendor or AI tool in a commit message, a document
  or a comment.

## How to finish

Commit and push on your phase branch. Report to the Architect in ONE SINGLE LINE - fleet messages
truncate at the first newline. Write the detail to
`docs/missions/session-rules-2026-09-02/phase-2-clock-report.md` and name it in your one line. Do not
open a pull request and do not merge; only the Architect lands work on main.
