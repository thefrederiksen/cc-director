# A REAL usage limit, captured 2026-09-03 - and the seat it happened to

**This is the thing `demonstration-rig.md` said could not be manufactured honestly, and it happened on
its own.** Row 3 of the QA report - "a session genuinely blocked on a provider usage limit" - has been
PENDING since the mission began, with a standing instruction not to fake it. It no longer has to be.

## What happened

The session building the usage-limit rule hit a usage limit. `Session Rules - Phase 1 Manager`
(`773641bf`) was one hour forty minutes into the phase 1 measurement run - the run that asks a model
about 32 real screens, three times each, on two models - and exhausted its allowance mid-batch.

The Architect captured its terminal through `GET /sessions/{id}/buffer` while it was still on screen.
The raw capture is `real-usage-limit-phase1-manager-raw.txt` (171,279 bytes, unedited); the distinct
limit lines are extracted verbatim into `real-usage-limit-lines.txt`.

## The screen, verbatim

The warnings, as the allowance ran down:

```
You've used 90% of your session limit  resets 6:10pm (America/Toronto)  /upgrade to keep using Claude Code
You've used 93% of your session limit  resets 6:10pm (America/Toronto)  /upgrade to keep using Claude Code
You've used 94% of your session limit  resets 6:10pm (America/Toronto)  /upgrade to keep using Claude Code
```

Then the block itself:

```
You've hit your session limit  resets 6:10pm (America/Toronto)
/usage-credits to finish what your working on.
```

## Why this capture is worth keeping, and it is worth keeping THREE times over

**1. It is scenario A's trigger, real.** A genuine block, on a real session, with **a stated reset
time** - which is precisely what `retry_delay_from` exists to read and what the phase 2 clock is being
built to wait for. Every previous limit screen in this mission was either a June turn package or a
printed line.

**2. The warnings are the NEGATIVE class, from the same session, minutes apart.** The 90, 93 and 94 per
cent banners are the session's own state and they are **not** a block: the turn carried on normally
after each one. A rule that acts on those is wrong, and a rule that acts only on the fourth line is
right. Corpus case `n11` already holds a June banner of exactly this shape and expects a decline; this
capture is the same distinction caught live, with the positive and the negatives from one session
inside one hour.

**3. It is the honest limit of what the harness measured.** The corpus's positives came from saved turn
packages. This one arrived while the mission was running, unprompted, and matches them.

## What this does NOT prove

It is a captured SCREEN, not a demonstrated RECOVERY. Nothing typed `continue`, nothing waited until
6:10pm, and no rule fired on it - the phase 2 clock is not built yet and the rule engine was not
watching that session.

So this closes the "we have never seen a real one" gap and **does not** close row 3, which needs a
blocked session recovering with nobody watching, verified by a COMPLETED TURN afterwards. Row 3 stays
PENDING until that happens or is honestly reported as not proven. **Do not let this capture be read as
row 3.**

## The irony is not the point, but it is worth one line

The seat building the rule that waits out a usage limit was stopped by a usage limit, and had to wait
it out. The feature exists because this happens; it happened to the mission.
