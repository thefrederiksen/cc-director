# The turn log - what we capture at the end of every turn, and why

**The owner's instruction, 4 September 2026.** This is the first piece of work in the turn-end research
plan, and it is the instrument every later question is answered with. Written from his words; where a
decision is his it says so.

## Why this exists

We have been tuning a judgement against **32 screens captured once in June**. The fleet produces about
**400 turn ends a day** and keeps none of them. Every argument about accuracy, cost, gating and model
choice has been fought on a sample too small to settle anything.

The owner's direction: **start logging reality, then optimise against it.**

## Where it goes, and it is NOT the product

**Store it locally, in the `devthrottle_internal` repository.** This is an internal test asset, not a
customer-facing feature and not part of the hosted product's data. The owner wants it kept because the
corpus itself is the valuable thing - a test set that grows on its own, every day, from real work.

That placement also sidesteps the retention question for now: this is our own repository and our own
sessions, not another account's terminal content.

## THE DESIGN RULE - every record stands alone

**The owner's own words: "each turn needs a lot of background information that needs to be stored
independently. So we'll have overlapping data, but that's okay."**

That is the whole design, and it is deliberate:

- A record must be **replayable by itself**. Reading one file is enough to re-run any judgement against
  that turn. No joins, no lookups, no other file, no live Gateway.
- **Duplication between records is expected and accepted.** The last five turns will appear in several
  records. That is not a bug to normalise away - it is what makes a single record self-sufficient.
- Store more than we think we need. **"We may or may not need it"** - a field we did not capture is a
  question we cannot ask later, and the turn is gone.

Anything that normalises this into a relational shape to save space has misunderstood the instruction.

## What each record carries

### Who and where
- The **agent** that is running (Claude Code, Codex, Pi, Gemini...). A screen means something relative
  to the agent that printed it.
- The **session name** and session id.
- The **computer name**.
- The repository and branch, the Director id, the mission and role if set.
- The model the session is running on.

### The moment
- Timestamp, and the turn number.
- The activity state before and after, and what the turn-end detector saw.
- How long the turn took, and how long the session had been quiet.

### The terminal
- **The screen** at the moment the turn ended - the real captured tail, the thing every judgement reads.
- Store it whole and raw. Not a summary of it, not a trimmed excerpt: the excerpt is a decision the
  harness should be able to re-take later with a different window size, which is impossible if we only
  kept the excerpt.

### The conversation around it
- **The last five turns - possibly the last ten - from BOTH the agent and the user.** The owner's call,
  and the reason is that a screen alone often cannot say whether a session is stuck, waiting, or done;
  what came before it can.
- Store the turn VALUES, not just the text: what was sent, what came back, timings.

### What the machinery decided
- What each judgement that ran would have decided, and why, and how long it took, and what it cost:
  the spoken summary, the supervisor's word table, the rules engine, and any needs-me verdict.
- **Whether a model was called at all**, which model, and the token counts.

### What SHOULD have happened
- The field that makes this a test set rather than a log. Filled in afterwards - by the owner, or by a
  reviewing seat - and never guessed at by the thing being tested.
- An empty verdict is UNLABELLED, and must never be read as "it was fine". A record with no verdict is
  evidence of nothing, exactly like an absent check.

## What this instrument must not do

- **It must never change what the product does.** It observes; it does not gate, delay or veto. A turn
  must end identically whether the log is on or off.
- **It must never be the reason a turn is slow.** Write it away from the turn-end path.
- **A failure to log is not a failure of the turn.** But it must be RECORDED as a gap rather than
  silently skipped, or the corpus will quietly acquire holes exactly where the interesting cases are.

## The first questions it answers

Named now so the fields are chosen for them, not for tidiness:

1. **How often does a turn end in a state that actually needs the owner?** Nobody knows. The whole
   needs-me judgement is being designed without this number.
2. **How much would a word table have caught?** Run the supervisor's classifier over the corpus and
   count. This is the keyword-first decision, measured instead of argued.
3. **How many turn ends does the supervisor MISS?** Today a miss writes no log line, so it is
   uncountable - which is why our measurement said "working" and the owner said "only sometimes". The
   log is what makes a miss visible for the first time.
4. **Does the spoken summary's call already contain enough to answer the other questions?** It runs on
   82 percent of turn ends and already reads the screen.
5. **Why can a snooze never outlast half an hour?** Capture the byte that some agents tap into the
   terminal every thirty minutes and see it for what it is.
