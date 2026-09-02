# Session Rules - the phase plan

Architect's plan, written 2026-09-02 on `mission/session-rules` at base `fac79fb56` (origin/main).
The WORK is in `brief.md`. The CONDUCT is the fleet's `mission` workflow. This file is the running
order: what gets built, in what order, what proves each part, and where it lands.

The owner's acceptance test, restated because everything below serves it:

> Put words on a terminal screen. Something happens automatically because a rule said so. A QA
> report shows it, plus a plain explanation of how to write a rule.

---

## What is being built, settled

A **rule** is a standing instruction the account gives in English. A model reads that sentence and
BUILDS the rule - the screen it is watching for, the cheap trigger words, and which verified checks
to run. When a session goes idle, cheap code decides whether any rule could possibly apply; if one
could, an agent reads the screen and the instruction together and decides what to do, including
deciding to decline. It types through the existing prompt route. Every firing is recorded.

Three design points are OWNER RULINGS and are not open (`r11`, `r14`, `r15`):

1. **The terminal screen is the only input.** No condition abstraction with one implementation.
2. **A rule is an instruction, not a form.** No action enum. The user never writes a matching
   expression and never picks trigger words.
3. **No generated code runs, ever.** The Gateway ships verified primitives we wrote; the model
   chooses one by name and supplies arguments. The stored rule holds a validated CALL - a name plus
   argument values - never a program, expression, lambda or snippet. There is no interpreter.

### Architect's rulings, made here so nobody re-opens them

**A1. Two rule states ship: DRY RUN and LIVE.** A new rule is always created in dry run. The
"asks me first" middle state that `r14` lists under "still open" is NOT built - the owner has not
decided it, and building an undecided state is guessing. It is recorded as deferred, and the stored
state value is a plain value so adding a third later costs one migration and no branching.

**A2. The primitive registry is DERIVED, never hand-kept.** Primitives are ordinary reviewed static
methods carrying a `[RulePrimitive]` attribute. The set of legal names, their arities and their
argument kinds are read off those methods by reflection. Validation looks a call up in that derived
registry. There is no second list of names in a constant, a switch, a JSON file or a test. A test
asserts the registry is non-empty and that every attributed method is reachable through it - a
PRESENCE, so an empty registry fails rather than passing vacuously.

**A3. The five primitives that ship in phase 1**, exactly the ones `r15` names:
`is_path_inside(target, root)`, `retry_delay_from(screen_text, now)`,
`elapsed_since(first_failure, now)`, `matches_any(text, terms)`,
`extract_first(screen_text, kind)` where `kind` is a closed set (path, duration, timestamp).
No primitive takes a pattern, an expression or a format string. Adding one is a product change.

**A4. Storage holds no free-text code column, and that is proven by construction, not by a list.**
The derived part of a rule is a typed primitive call - a name plus named argument VALUES - and those
values are validated against the registry signature before the rule is stored. A rule naming a
primitive that does not exist, or supplying the wrong arguments, is REJECTED at write time with a
stated reason. Prove it by writing a rejected one, not by grepping the schema.

**A5. One agent call per screen, covering every candidate rule.** Not one per rule. The reply names
a candidate rule id, an understanding, a decision, the primitive calls it wants run, and the text it
wants typed. Every one of those is validated against what was actually offered: an id that was not a
candidate is rejected; a primitive not in the registry is rejected; a rejection is RECORDED as a
refusal, not swallowed.

**A6. The decline is a first-class outcome and it is proven, not asserted.** Bound 6 - the
instruction is the authority - is the one that decays quietly. Every phase from 2 onward owes at
least one test where the agent is given a screen its instruction does not cover and must DECLINE
with a recorded reason. A rule that never declines has not been shown to have a boundary.

**A7. The Rules page sits in the left rail directly after Workflows**, matching the mockups.
Cockpit is required. The mobile surface is required for the LIST and the RECORD (read-only is
acceptable on the phone if authoring will not fit); anything shared between the two shells lives in
`packages/client-core`, never duplicated in a shell.

**A8. The mockups are followed except where a ruling supersedes them.** `how-a-rule-runs.html` step 4
and `creating-a-rule.html`'s "Prompt + code" tag both predate `r15` and describe the agent writing
and running code. They are WRONG. The shipped product says "runs a check we wrote" and names it.

---

## How every phase is proved

These bind every Manager and every Worker on this mission. A phase that has not done these has not
finished, whatever its report says.

- **The test fails first.** Write the test, run it, watch it go RED, quote the red. Then make it
  green and quote the green. A test that passed the first time it was run has tested nothing and is
  to be rewritten until it fails on the unwritten code.
- **Restate every check as a PRESENCE.** Before writing a proof, ask: if the thing I am measuring
  never ran at all, does my check still pass? If yes it is a check that fails open - restate it as a
  specific thing that must be THERE. An empty result is a broken instrument, never a clean run.
- **Never hand-keep what you can derive.** Any list of names, files, primitives or cases is read off
  the thing itself. An allow-list is inverted into an exception list that has to be argued for.
- **Every number carries its exit code and the commit it ran on, or the word PENDING.** Prose
  written while a run is still going reads exactly like prose written after it, so it is not written.
- **Foreground only.** Nothing backgrounded, nothing detached.
- **ASCII only. No assistant, vendor or model named anywhere** - commits, pull requests, issues,
  comments, code comments, documents, the report.

---

## The phases, and where each one lands

Work accumulates on `mission/session-rules`. Only the Architect merges, and it merges in four
landings so no branch outlives a day's worth of work. Each landing: a Manager builds and is KILLED,
an independent inspector from a different agent family reads the diff adversarially and writes its
review to a FILE, findings go back to a FRESH Manager, then the Architect merges.

### Phase 1 - the rule store, the contract, the primitives (landing A)

- Rule and firing entities, tenant-scoped, one EF migration.
  A rule holds: the account's sentence (the authority), the derived screen description in plain
  English, the derived trigger words, the derived primitive calls, scope, cooldown, daily cap, state.
  A firing holds: the screen, the understanding, the decision, the reason, which primitives ran with
  what arguments and what they answered, what was typed, what happened next.
- The primitive registry and the five primitives, with the unit tests they deserve -
  `is_path_inside` gets the `..`, symlink and prefix-collision cases.
- The write-time validator: unknown primitive rejected, wrong arguments rejected, both with reasons.
- Storage plus reading and writing. Dry run only. Nothing types anything in this phase.

**Proves:** a rule round-trips; a rule naming a primitive that does not exist is refused with a
reason; the registry is derived and non-empty.

### Phase 2 - authoring by conversation (landing B, first half)

- English in, a built rule out. The model asks about genuine ambiguity rather than guessing, and
  what it built is shown back in the account's own words before anything is saved.
- **The refusal is part of the acceptance**: an instruction that needs an exactness no primitive
  provides produces a STATED refusal - or a rule built without that part, saying plainly what it
  cannot do - never a quiet approximation.

**Proves:** a sentence in, a stored rule out, quoted; and a sentence in, a refusal out, quoted.

### Phase 3 - the evaluator (landing B, second half)

- Hangs off the same working-to-idle event the supervisor already uses. Free checks first: screen
  changed, session idle, rule in scope, under cooldown and daily cap, trigger words present. Any no
  stops there, at zero cost.
- Then ONE agent call for the screen, covering all surviving candidates, with the reply validated as
  in A5.

**Proves:** a screen with no trigger word costs no model call - a PRESENCE check on an instrumented
call counter reading zero, never an absence of log lines; a screen that does match reaches the agent;
a screen the instruction does not cover is DECLINED and the decline recorded.

### Phase 4 - acting (landing C, first half)

- Re-read the screen immediately before acting; if it changed, abandon and record why.
- Type through the existing prompt route. Handle a confirmation picker if one appears.
- **Known trap, do not re-learn it:** a keystroke answering a picker returns HTTP 502 from the submit
  verifier - "never started a turn ... parked in the composer unsubmitted" - while having actually
  worked. Answering a picker is not a turn. That 502 is not a failure.
- Cooldown and daily cap enforced here, per rule per session, both required.

**Proves:** dry run types NOTHING - proved by an instrumented send seam counted at zero, never by the
absence of a log line; live types exactly the composed text; a screen that changed between the
decision and the keystroke is abandoned and the abandonment recorded.

### Phase 5 - the record and the surface (landing C, second half)

- The Rules page: the list as the sentences the account said, the firing record, the rule editor,
  and the "make a rule from this screen" entry point.
- Follow the mockups, minus what `r15` supersedes (A8).

**Proves:** the page renders a real stored rule and a real firing read from the store, screenshotted.

### Phase 6 - the QA report (landing D)

The mission's whole point. It carries, as artifacts and not as prose:

1. A rule created from plain English - the sentence in, the built rule out, quoted.
2. **The headline:** words are put on a terminal screen, and something happens automatically because
   a rule said so. The screen before, the rule that matched, what it decided, what it typed, the
   screen after.
3. The real case: a session blocked on a provider limit notice recovers with no human, verified by a
   COMPLETED TURN - not by an endpoint's own response and not by the reported current model alone.
4. **The negative control, not optional:** a session merely DISCUSSING a usage limit is not
   convicted, and a rule DECLINES a screen its instruction does not cover, with the reason recorded.
5. How to write a rule - short, in the owner's language, from the real screen.
6. What is NOT proven, stated plainly.

Emailed to the owner ONCE, at the very end.

---

## Known hazards, carried from the brief

- **The migration slot is contended.** `mission/terminal-rules` holds an unlanded screen-store
  migration dated 2026-09-02 on top of the same base this branch was cut from, and pull request 2379
  holds three from August. Test whether each migration is PRESENT ON MAIN, not whether it differs
  from the merge base, and fetch with `--prune` - a squash-merged branch otherwise still votes.
  Whichever mission lands second regenerates the model snapshot; that is mechanical, not a dispute.
- **The machine-wide test lock is shared.** A small filtered run takes it exactly like a full gate.
  A production incident outranks this mission for that lock without argument: yield immediately and
  say so.
- **The screen store is not landed**, this mission does not depend on it, and it must not wait for it.
