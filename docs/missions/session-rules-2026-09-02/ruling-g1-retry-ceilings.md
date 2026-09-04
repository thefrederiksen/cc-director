# Ruling G1 - the retry ceilings are the owner's numbers now, not the Architect's

**Decided by the owner, 2026-09-04, at the keyboard, in conversation.** This replaces the
Architect-invented bounds that fix round D introduced and that the handoff flagged as owed to him.

## What was wrong

Fix round D set a daily cap of at most 100 firings and a cooldown between 60 seconds and 24 hours.
Those were the Architect's numbers. They were never his, and the mission's own standing complaint -
an invented bound presented as the owner's decision - applied to them.

Asked plainly, his answer was immediate and much tighter than the invented one: **"We shouldn't retry
a hundred times. I think that's way too high."**

## What he decided

**Try again every fifteen minutes. Give up after six hours.**

That is twenty-four attempts at the default frequency, and then the rule stops and leaves the session
alone until the next day.

## The shape change his answer earns, and he confirmed it

The Architect had this as a COUNT of attempts. What he described is a STRETCH OF TIME. Those are only
the same thing while the frequency happens to be fifteen minutes - halve the cooldown and a count of
twenty-four silently becomes three hours of trying, not six.

So the giving-up is a **duration**, measured from the rule's first firing for a stoppage, with a
count kept behind it purely as a backstop. Asked "does 'stop after six hours of trying' match what
you meant", he answered yes.

## What this binds

- Default cooldown: **15 minutes**.
- Default give-up: **6 hours of trying**, expressed as a duration.
- The count cap remains, as a backstop only, and must not be the thing that defines when a rule gives
  up.
- The QA report no longer needs to say the ceilings are the Architect's. It should say they were, that
  they were wrong, and that the owner replaced them with these.

## One correction to how the question was asked

The Architect first put this to him as "a hundred keystrokes a day", which was sloppy - each firing is
one thing, the word `continue` and an Enter, not a hundred characters. He asked for the question to be
put again. The clarified question is the one he answered.
