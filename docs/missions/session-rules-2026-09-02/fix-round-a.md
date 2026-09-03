# Session Rules - fix round A

The Architect's disposition of the independent inspection of landing A (`inspection-a.md`, nine
findings) and of the two defects phase 2 found in its own work and recorded rather than tidied away
(`phase-2-report.md`). One fix round covers both, because they are the same landing.

**Nothing goes to `main` until this round is done and re-inspected.** The demonstration is already
captured and safe on the mission branch, so this round is not racing anything.

---

## The Architect's rulings on the findings

### A11. Finding 4 is RECONCILED, not fixed as written - but its deeper half is real

The inspector reads `RuleInput` exposing `Now`, `FirstFailure` and `SessionRepositoryPath` as
conflicting with ruling 11, which says the terminal screen is the only input. Reconciled as follows,
and this is settled:

- **Ruling 11 governs what decides WHETHER a rule applies.** That is the screen, and nothing else.
- **Ruling 15 explicitly ships `retry_delay_from(screen_text, now)` and
  `elapsed_since(first_failure, now)`, and the owner's own example rule is "if it is asking
  permission to touch something inside the repo it is working in".** A clock and the session's
  repository root are therefore SANCTIONED as check ARGUMENTS by the owner's own ruling. Removing
  them would break the primitives he named.
- **The inspector's deeper half stands and is the actual defect:** nothing separates "a clock used to
  interpret what the screen says" from "a clock used to decide whether the rule applies". That
  separation is currently a convention, and a convention is not a bound.

**What this round owes:** not the removal of those inputs, but a stated and TESTED bound that the
decision to act is answerable from the screen. See A12, which is the same defect arriving from the
other direction.

### A12. An act's reason must be grounded in the screen it was given

Phase 2 recorded a run where the agent DECLINED and its stated reason quoted a sentence that was not
on the screen the firing record stores - the words had been on that session's screen twelve minutes
earlier, in an unrelated run. The decline was safe, because declining is the direction that does
nothing. **The same unfaithfulness in the other direction is a rule acting on evidence that was not
there**, and that is the sharpest thing this mission has learned about its own design.

The reply is already required to name a rule that was offered and checks that exist. The next bound
is of the same kind: **an ACT must be refused when its stated reason quotes screen text that the
screen does not contain.** A decline that does so is recorded as it is - declining is safe and the
record should show what actually happened - but it is recorded with the mismatch NOTED, so the
unfaithfulness is visible rather than silently smoothed over.

This is a PRESENCE check: the mismatch must be written down. A run where the grounding check itself
never executed must not be indistinguishable from a run where it passed.

### A13. Finding 3 is a truthfulness defect and it outranks the rest

Two of the five red-first claims do not reproduce from the commits the report names, and the
filtered runner exits 0 with `No test matches` - which is exactly the zero-work-passes-as-success
condition this mission's own standards forbid. **The report goes to the owner. A false claim in it is
worse than a missing feature.**

Repair it the way the standard requires, not by re-wording it:

- Commit a real red probe for each of the two features, so the red is REPRODUCIBLE by checking the
  commit out, exactly as phase 1 already did for the types-nothing guard and left in history on
  purpose.
- If a red genuinely cannot be reproduced, **delete the claim and say so in "what is not proven"**.
  Do not restate an unreproducible number in softer words.
- **A test run that collects ZERO tests is a broken instrument, not a pass.** Make the runner used
  for red-first evidence fail loudly on a zero collection, so this cannot recur silently.

---

## What this round must fix, worst first

1. **Finding 1 - the dry-run boundary has no enforced human gate.** `Promote` takes a rule id and a
   timestamp and nothing else, so any code that can read rules can promote its own. Dry run is the
   owner's most important bound and bound 6 forbids a rule promoting itself. The evaluator must
   receive an interface with NO promotion on it, and promotion must require something an automated
   caller cannot obtain. Prove it by showing a non-human caller REFUSED, not by showing a direct call
   succeeding.
2. **Finding 2 - the validator is not structurally the write gate the code claims.** The entity, its
   setters, the DbSet and the context factory are all public, so a caller can write an arbitrary
   call document, an arbitrary tenant and `State = "live"` without ever meeting the validator. Either
   close the route mechanically or delete the claim from the source comment - **a false structural
   claim in a comment is worse than an honest absence**, because the next author trusts it.
3. **Finding 3 - the unreproducible reds.** See A13. This one is not optional and not negotiable.
4. **A12 - the grounding bound.** Above.
5. **Finding 5 - a missing scope silently widens to every session.** Fail-open on the widest possible
   scope. A missing scope must be refused; "all sessions" must be an explicit value.
6. **Finding 8 - a null argument element crashes the validator** instead of producing a refusal. A
   refusal is a stated reason; a crash is not.
7. **Finding 9 - the firing store accepts an empty or invented record.** The record is the product;
   a record nobody can trust is worse than no record.
8. **Finding 6 - the types-nothing guard sees only direct references in one namespace.** Phase 2
   already tightened it once when it turned out not to see inside async methods, and reported that
   as clean. Tighten it again as the inspector describes, or state precisely what it does and does
   not cover. Do not let it keep passing on what it cannot see.
9. **Finding 7 - the suite does not enforce that exactly the approved checks ship.** Derive the
   comparison from the attributed methods; do not hand-keep a second list of names to compare
   against.

## What this round must NOT do

- Do not rebuild the demonstration. It is captured and it is the mission's headline.
- Do not start the authoring conversation, the user interface, or any new feature.
- Do not soften a number. If something cannot be proved, it goes in "what is not proven", in plain
  words, and that is a complete and acceptable answer.

## How it is proved

Every fix owes a test that FAILS FIRST against the un-fixed code, watched going red, with the red and
the green both quoted and both reproducible from the commits named. A fix round is NEW WRITING and
carries a new writer's risk - gate it as hard as a first draft.
