# Phase 1 - make the run-time call reliable

The acceptance row is in `implementation-plan-for-the-architect.html`, section 6. This brief carries
what the Architect established by reading the code, and **the headline is that this phase is bigger
than the plan makes it sound.** It is not a one-line model swap.

**Why it comes early.** The measured fact that reorders the whole mission: the run-time call asks a
reasoning model to write about 600 characters of JSON and times out roughly one time in three. A live
rule that fails a third of the time is the feature failing, not a rough edge. Independently confirmed
from the other direction by the phase 2 demonstration, whose single run-time call took **18.4 seconds
for 571 characters** (`qa-report.md` section 2). The same work on the fast model as a yes/no question
was measured at **0.4 seconds**, and it answered all six screens correctly including the three that
carry the trigger words and must still be declined.

---

## What the plan says, and the part of it that is not built yet

The plan's acceptance row includes: *"The text typed is the authored text, verbatim - nothing composed
at run time."* **That is not the case today, and making it true is most of this phase.**

Verified by reading the code, not inferred:

- `RuleAgentContract` asks the run-time model for `"type": "the exact text to type into the session"`
  and reads it back into `TextToType`. **The keystroke is composed by a model at run time, on every
  firing.**
- `SessionRuleEntity` has `Instruction`, `ScreenDescription`, `TriggerWords`, `Calls`, the four scope
  fields, the two ceilings, `State` and `PromotedBy`. **There is no field for the text to type.**
- `RuleProposal` in `RuleDraftContract` - the authoring output - has no such field either.

So "the text was decided and approved at authoring time" describes the target, not the present. The
phase has to build it.

## The work, in the order it has to happen

1. **Give a rule the text it types.** A new field on the rule, written at authoring time and stored
   with the rule. A store change needs its migration in the SAME change - that is a repository rule,
   not a preference.
2. **Make authoring capture it, and the read-back SHOW it.** The read-back is what the person
   confirms, and this is the most consequential thing a rule does. A read-back that describes the
   situation but hides the keystroke is asking somebody to approve an action they were not shown. Show
   the exact text.
3. **Change the run-time call to a yes/no shape** and take the text from the stored rule. The
   question becomes "is this that situation?" and nothing else.
4. **Move the run-time call to the fast model.** One line in `GatewayRuleEnvironment.AskAgentAsync`,
   which today passes `WingmanModelRole.Thinking`; the enum's own comment says `Fast` is for
   "latency-sensitive response-only paths", which is precisely what this became at step 3.
   **Keep `Thinking` for AUTHORING** - `RuleAuthor` is wired separately in `GatewayHost` for exactly
   this reason, and a person is waiting there.

Steps 3 and 4 are the easy half. Steps 1 and 2 are the phase.

## RULING - what happens to rules that already exist

Rules stored before this change have no text to type. **They must not silently start doing nothing,
and they must not fall back to composing text at run time** - a fallback here would defeat the whole
point and would hide it. Decide one of these explicitly, implement it, and say which in your report:
refuse to fire with a recorded reason naming the rule as needing re-authoring, or migrate them by
deriving the text once and recording that it was derived. **A rule that silently stopped firing is a
trust failure** - that phrasing is the owner's and it is why this cannot be left implicit.

## Acceptance - and note the gate is the Phase 0 harness

This phase CANNOT be accepted without Phase 0. Measuring a model change by re-running the unit suite
proves the unit suite still passes.

| Row | What it takes to pass |
| --- | --- |
| Zero timeouts across 20+ cases | Against the Phase 0 corpus, through the real engine, on the fast model |
| Zero wrong answers on the negatives | **A false "act" is the unacceptable failure.** State it as a count |
| The negative control passes on the fast model | This is the owner's stated gate on his own decision to use the fast model - it is not optional and it is not a formality |
| The text typed is the authored text, verbatim | Show a firing where the typed text is byte-identical to the stored text, and show that no code path can compose one |
| Both models reported side by side | Answer, right or wrong, and time, per case, per model. The comparison IS the evidence for the decision |

## The owner's decision this phase rests on

He chose the fast model, **with the negative control re-run as the gate**. So a green run on the
positives is not the answer; the negatives are the answer. If the negatives regress on the fast model,
do not quietly keep the thinking model and do not average the two - stop and report it to the
Architect, because that is the owner's decision changing on new evidence and it is his to change.

## The gate

- `.\scripts\test-local.ps1` green. The Postgres proof rig must be up or the run is red for reasons
  that have nothing to do with you - container `cc-pg-test` on port 55432 was up on 2026-09-03.
- The migration slot: `origin/mission/terminal-rules` holds an unlanded `20260902154804_AddSessionScreens`
  on the same base, and phase 2 may add one too. Test whether a migration is PRESENT ON `origin/main` -
  never difference from the merge base, which makes a squash-merged branch vote. Whoever lands last
  regenerates the model snapshot; that is mechanical.
- Watch every new test fail first, with the reported symptom, and quote both runs.
- ASCII only. No mention of any assistant, model, vendor or AI tool in a commit message, a document or
  a comment. Naming a MODEL as a subject of measurement is different and is required here.

## How to finish

Commit and push on your phase branch. Report to the Architect in ONE SINGLE LINE - fleet messages
truncate at the first newline. Write the detail to
`docs/missions/session-rules-2026-09-02/phase-1-fast-model-report.md` and name it in your one line. Do
not open a pull request and do not merge; only the Architect lands work on main.
