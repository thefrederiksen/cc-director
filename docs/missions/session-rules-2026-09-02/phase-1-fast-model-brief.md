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

---

# ADDENDUM, written after Phase 0 ran. Read this before anything above.

**Phase 0 measured the engine against 32 real screens through the real evaluator, and the engine does
not work.** The numbers are in `phase-0-report.md` and they change what this phase is for.

| | wingman (thinking, production today) | wingman-fast (this phase's candidate) |
| --- | --- | --- |
| Wrong on negatives | 0 of 20 | **7 of 20** |
| Wrong on positives | **12 of 12** | **12 of 12** |
| of those, timed out at 60s | 9 | 0 |
| of those, blocked by the grounding check | 1 | 9 |
| Right | 20 of 32 | 9 of 32 |
| Model call time, median | 21.0 s | 12.8 s |

**Read the positives row twice. Both models are wrong on every single positive.** The rules engine as
shipped on `main` never acts on a real limit screen - the exact case the feature was built for. Nothing
typed, ever, on any of the twelve real limit screens in the corpus.

## RULING P1-A - the citation is a FIELD, not a hope

The unpredicted finding, and the one that matters most. `RuleReasonGrounding` refuses an act whose
stated reason carries no citation - Architect ruling A12, and **A12 is correct and stays**: it exists
because a live rule once quoted a sentence that had been on the screen twelve minutes earlier in an
unrelated run, and an act on evidence that was not there is the sharpest thing this mission has
learned about its own design. An act must point at something a person can go back and check.

But A12 was implemented by scanning a free-text reason for quotation marks and hoping the model put
some there. Phase 0 proves it does not: the grounding check blocked 9 of 12 positives on the fast model
and 1 on the thinking model. **That is a contract problem, not a model problem, and the contract is
what this phase is replacing anyway.**

So, in the new yes/no shape:

- **Ask for exactly one verbatim line copied from the screen, as its own named field.** Not "explain
  your reasoning and quote the screen" - one field, one line, copied.
- **Verify that field against the same screen excerpt the model was shown**, reusing the function
  ruling D2 built for authoring rather than writing a second one. One normaliser, one comparison.
- **An act with no citation field, or a citation not on the screen, is still refused.** A12's property
  is preserved exactly; only the way the model is asked for it changes.
- A DECLINE still needs no citation, and its record still says which it had. That asymmetry is
  deliberate and stays: declining does nothing, so an unfaithful decline is recorded as it happened
  with the mismatch noted.

This is the smallest thing a fast model can reliably produce that still makes an act checkable, and it
is why moving to yes/no is a safety improvement rather than a trade against one.

## RULING P1-B - re-run the harness, and the negatives are the gate

The fast model's 7 wrong negatives were measured through TODAY's contract, which asks it to write about
600 characters of JSON. **That is a baseline, not a verdict on the owner's decision.** This phase
changes the question to "is this that situation" plus one copied line, which is far less work.

- **Re-run the Phase 0 harness on the new contract, both models, and put both tables in your report.**
  The comparison IS the evidence.
- **The gate is the negatives count.** A false act is the unacceptable failure. Zero wrong negatives.
- **The positives now matter too, and they did not before.** A run where both models are wrong on every
  positive is not a pass on any reading. State the positives count as prominently as the negatives.
- **If the fast model still fails the negatives on the NEW contract, STOP and report to the Architect.**
  Do not quietly keep the thinking model, do not average them, do not tune the corpus. That is the
  owner's decision changing on new evidence and it is his to change, not yours and not mine.

## What Phase 0 did NOT find, and do not assume

Phase 0 ran the evaluator with a per-case environment. It did not run a Director, did not type into a
session, and did not exercise `GatewayHost.ReadRuleScreenAsync`. A green harness is not a working
feature; it is a working judgement. The typing end is proven by the demonstrations, not here.

---

# SECOND ADDENDUM - three rulings made mid-phase, on evidence from the first smoke runs

## RULING P1-C - a rule's judgement must not be a dice roll, and the gate is no longer one run

The Phase 1 smoke runs found **the same negative screen answering decline on one run and act on the
next.** That is the unacceptable failure - a false act - appearing intermittently, which is worse than
appearing consistently, because a harness run can then pass by luck.

Two consequences, and they are separate:

1. **It is a product defect, not a testing nuisance.** A standing instruction that types into a session
   on Tuesday and declines the identical screen on Wednesday is not a rule; it is a coin. Find out
   whether the run-time call can be made deterministic - a temperature or a seed on the brain - and if
   it can, use it. If it cannot, say so plainly rather than working around it.
2. **The gate changes shape.** "Zero wrong answers across twenty negatives" measured ONCE is luck, not
   a measurement, and this repository has already paid for a gate that was luck. The harness runs each
   case **at least three times**, and reports the **worst case per case** plus the total wrong-negative
   count across all runs. Never the best, never the mean.

**Report the flip rate as its own number**, whatever it turns out to be. If a rule's judgement is
non-deterministic, the owner needs that stated in the QA report as a property of the feature - and it
may change his model decision, which is his call to make and not ours to absorb.

## RULING P1-D - a blank reason is a refusal, not a pass

The fast model leaves the reason blank when it fills the quote. The quote is now the checkable evidence
and is mandatory - but the reason is what a person reads in the firing record when they ask why their
session was typed into, and an act carrying no reason is precisely the absence-shaped hole this mission
keeps closing.

Ask for two separate small fields, quote and reason. If either comes back empty, that is a REFUSAL,
recorded as one, naming which field was missing. Never a silent pass.

## RULING P1-E - if you normalise the quote, normalise the screen with the SAME function

The fast model mangled a glyph-laden line, and asking for the words only is the right fix. But
**normalising one side of a comparison and not the other rebuilds the defect ruling D2 just removed** -
the check and the prompt looking at different text.

One normaliser, applied to both sides, at one call site. The test is a real glyph-laden line from the
corpus: a faithful quote passes, an invented one still fails.
