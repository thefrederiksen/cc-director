# Session Rules - Architect handover, 2026-09-04 (second)

You are the Architect. This supersedes `ARCHITECT-HANDOVER.md`, which is now history: the mission
changed direction today on the owner's instruction, and most of what that document told you to do next
is no longer what to do.

Read this, then `turn-end-research-plan.html`, then `turn-log-harness-spec.md`. Read the older handover
only if you need the pre-history.

## 1. What changed today, in one paragraph

The mission was "make Session Rules work and demonstrate three scenarios". Investigating the cost of the
run-time judgement turned up that **a shipped feature already does most of scenario B**, that **a model
call already happens on 82 percent of turn ends** for the spoken summary, and that **the rules engine has
been running in production for weeks doing nothing at all** because the half that lets a person write a
rule never merged. The owner then reframed the work: stop patching the judgement, treat the end of a turn
as one thing, and **start logging real turns so we can optimise on evidence rather than argument.**

## 2. The owner's rulings today - do not re-litigate these

| # | Ruling |
| --- | --- |
| G1 | **Retry every 15 minutes, give up after 6 hours** - and give-up is a DURATION, not a count of attempts. Replaces the Architect-invented cooldown bounds and the daily cap of 100, which he called "way too high" |
| - | **Cost is not the blocker.** A separate smart-model call on every turn end is ~$12/month at today's rate for the heaviest account. Absorbed - it is an included service |
| - | **This is a Pro feature** |
| - | **The rules engine goes keyword-first.** Search for the words; wake a model only when they are on the screen |
| - | **"Does this session need me" cannot be keyword-gated and we pay for it anyway.** It is the biggest reducer of his cognitive load |
| - | **The supervisor's job becomes the rules engine.** One engine, not two - built-in rules we ship, alongside rules an account writes |
| - | **Log real turns, locally, in `devthrottle_internal`.** Spec written |

## 3. The exact next action

**Build the turn log.** `turn-log-harness-spec.md` is the specification and it is written from his own
words. The design rule that matters most: **every record stands alone, and duplication between records
is deliberate.** Anything that normalises it has misread the instruction.

## 4. What we established today, with evidence

Everything below was measured from the running hosted Gateway or read from `origin/main`. None of it is
recalled or inherited.

- **The parked `Gateway.Tests` suite passes** - 2,336 tests, 0 failed, 51m52s, on the landing branch.
  **This is the first time anyone on this mission has run it**, and it was the oldest unverified gap.
- **The local gate is green on the landing branch** - 4,929 tests, every suite Completed. The exit code
  is 1 because `Gateway.UnitTests` takes 2m07s against a 120-second budget ceiling. That is the known
  budget artefact, not a failure.
- **Phase 1 missed its gate.** The second question halves the damage - wrong negatives that would have
  typed fall from 15 of 60 to 7 - and does not reach zero. The seat stopped rather than tuning.
- **The residual is a DIFFERENT class.** n16 and n18 are the session's own state and the whose-state
  question answers them correctly; what is wrong is upstream - a context limit is not an allowance, and
  86 percent used is not a stopped session. The measurement seat's sentence, kept verbatim: *any new
  question must be a SITUATION test, not a whose-state test.*
- **The thinking model is not inaccurate, it is being cut off.** Every one of its failures on the
  positives is a timeout at exactly 60.0s. Counting only answers it was allowed to give: **60 of 60 on
  negatives, 18 of 19 on positives.** The 60 seconds is `HostedInferenceBrain.DefaultCallTimeout`, a
  general default shared with the voice path. The codebase already overrides it where judgement matters
  (3 minutes for the dictionary scan, 90 seconds for the history summariser). The rules path did not.
- **`SessionSupervisor` is live and works ONLY SOMETIMES.** 24 faults caught in two days, 8 continues
  typed, 19 recoveries, 4 escalations - and **the misses are uncountable**, because a session it never
  noticed writes no log line. Our measurement said "working"; the owner says "only sometimes"; the
  evidence could never have contradicted him. This is the mission's own absence-fails-open law, on us.
- **The supervisor costs almost nothing** - 10 model calls in two days against ~1,200 turn ends, because
  a deterministic word table decides everything else. Its classifier already handles our corpus's
  negative classes: a real-content window, an error-marker requirement beside ambiguous words, and
  `usage limit reached` among its signatures.
- **The rules engine is deployed and empty.** 407 evaluations today, every one `no-rules: this account
  has no rules`. Nobody can create a rule because authoring never merged.
- **The spoken summary is already a model call on 82 percent of turn ends** - 334 summaries against 407
  turn ends today, each with its own text-to-speech call. The needs-me judgement can ride it.
- **The turn brief is NOT running.** Zero generated today. The 774 rich `needsYou` records are from June.
  An earlier section of finding G3 overstated this and was corrected.

## 5. Numbers you will need

| | |
| --- | --- |
| Turn ends, 4 September | **407** (775 on 3 September) |
| Fast-model calls today | 484 successful, 2 timed out |
| **Thinking-model calls today** | **1**, and it timed out |
| Spoken summaries today | 334, with 334 text-to-speech calls |
| Supervisor model calls, two days | 10 |
| Input tokens per rules question | ~1,700 (screen measured at 3,940 chars median) |

**The models, and where the rates live.** `devthrottle/wingman` maps to `zai-org/GLM-5.2`;
`devthrottle/wingman-fast` maps to `Qwen/Qwen2.5-72B-Instruct`. The mapping and the rates are in
`devthrottle_internal` at `website/api/_lib/inference-providers.js` - GLM-5.2 at $0.75/$2.40 per million
list, currently 35 percent off; Qwen2.5-72B at $0.36/$0.40. **They are not in the cc-director repo**, and
the hosted spend routes refuse a session key by design.

## 6. Operational notes that cost time to learn

- **A spawned session's `--prompt` PARKS in the composer on this Director and never fires.** Seat with
  `POST /sessions/{id}/prompt` and `appendEnter: true`; a bare carriage return with `appendEnter: false`
  unparks one already stuck. **Verify a seat actually STARTED** - a spawn returning an id proves nothing.
- **Reading the hosted Gateway log:** Azure subscription `DevThrottle`
  (`8641a436-ec6f-471b-a3ed-04c92b76569c`), app `devthrottle-gw`, resource group
  `rg-devthrottle-hosted-gateway`. Get publishing credentials with
  `az webapp deployment list-publishing-credentials`, then POST to
  `https://devthrottle-gw.scm.azurewebsites.net/api/command` with a `command` and
  `dir: /home/gateway/cc-director/logs/director`. **Build the JSON with a file, not an inline shell
  string** - quoting will defeat you otherwise.
- **`ls` reports the live hosted log as 0 bytes; `wc -c` says 149 MB.** Never read the listing size as
  evidence a log is empty.
- Postgres proof rig `cc-pg-test` on port 55432 must be up or the local gate is red for reasons
  unrelated to your change.

## 7. Repository state

- Mission document branch: `rule-authoring-by-conversation`. Everything in section 4 is committed there.
- **Landing branch `mission/rules-landing`** in worktree `D:/ReposFred/devthrottle-landing` - it is
  `mission/rules-fix-f` plus the mission documents, verified by diffing to be a strict superset of both
  open pull requests, and both gates are green on it. **It is NOT merged and NOT pull-requested.**
- Pull requests **2671 and 2672 are still open** and are still to be CLOSED as superseded, not merged.
- Branch tips: `origin/main` `3f2e2b652`; `mission/rules-p1` `521acee6c` (phase 1 round A, pushed).
- The Phase 1 measurement seat `53020892` was reaped mid-conversation; its work is safe on
  `origin/mission/rules-p1` and its findings are in `ruling-g2-phase-1-residual.md`.

## 8. The open question the direction change created, and it is the big one

**What are rules FOR, now that a working supervisor already recovers stuck sessions and the owner has
ruled that the supervisor should become the rules engine?**

The honest answer affects everything downstream - whether the landing branch should merge as-is, what the
three demonstrations should demonstrate, and whether the QA report's scenarios are still the right ones.
**Do not answer this alone.** It is the owner's, and he is engaged and available.

## 9. What is still NOT verified

- **No rule has ever been drafted against a real Director's screen** through the production path.
- **Nothing has been driven in a browser.** The Rules page has unit tests and a typecheck only.
- **None of the three demonstrations has been run.** The rig recipe exists and was never executed on
  this mission.
- **The supervisor's miss rate is unknown and currently unknowable.** That is what the turn log fixes.
