# Ruling 11 - the terminal screen is the only input a rule reads

Owner's decision, 2026-09-02. Settled. Do not re-open it and do not re-ask him.

## The decision, in his words

Asked whether a rule should be able to trigger on things other than the terminal screen - how long a
session has been waiting, its token spend, the conversation text, its machine going offline - the
owner answered:

> "Let's leave it at just the terminal for now."

## What that means

**A rule's condition is a description of a terminal screen, and nothing else.** One input, one
question: does this screen match what the account described? Scope fields (agent, repo, machine,
mission) still decide WHICH sessions a rule applies to - that is addressing, not a condition, and it
was already in the brief.

The wider design - "when this is true about a session, do something", with the screen as one of
several things that can be true - is **deferred, not rejected**. "For now" is his word and it is
recorded as his word.

## What this forbids, and it is the part that matters

**Do not build the wider thing behind an abstraction "so it is ready later".** No condition-type
hierarchy with one implementation. No `ICondition` with a single `ScreenCondition`. No union type in
the stored rule shape whose other arms are unreachable. No field on the contract that exists only to
be filled in by a feature nobody has been asked to build.

Speculative generality is not free and it is not neutral:

- It doubles the surface a reviewer has to read, for behaviour that cannot occur.
- It fills the rule contract with shapes the validator must either reject - unreachable code - or
  accept, which is a way for a half-built condition to be stored and silently never evaluated.
- It guesses the shape of a feature whose requirements do not exist yet, and that guess is almost
  always wrong in a way that is then expensive to unpick.

Build the narrow thing, properly and completely. If the owner widens it later, widening a small clean
thing is ordinary work; unpicking a wrong abstraction is not.

The one thing to avoid actively is making a later widening *impossible* - which mostly means not
naming things as though the screen were the only conceivable input forever. A stored rule may say
what KIND of condition it holds, as a plain value with exactly one legal value today, if and only if
that costs a single field and no branching. If it costs more than that, do not do it.

## Consequences

- **Phase 1 is unchanged in scope**: the rule store, the contract, CRUD, validation, dry-run only.
  The condition is the screen description plus the optional watch-words, exactly as `brief.md` has it.
- The mockups are updated: the input question is answered on the page rather than left as an open
  decision, so a future reader does not re-raise it as undecided.
- The inputs table in `how-a-rule-runs.html` keeps the other rows, marked as deliberately deferred
  with this ruling named. Deleting them would lose the fact that they were considered and decided,
  which is worth more than a shorter table.
