# The turn log - what was built, and what is not proven

**4 September 2026.** The first piece of the turn-end research plan, built to the specification in
`turn-log-harness-spec.md` and to the owner's decisions taken during the build.

## What it is

At every turn end on a machine an administrator has switched on, the Gateway writes **one
self-contained record**. Reading that one file is enough to replay any judgement against that turn -
no join, no database, no live Gateway, no Director still running.

## What one record contains

**Who and where** - the whole session snapshot as the Gateway held it, serialized entire rather than
a chosen handful of fields, plus a scannable header carrying the session name, the computer, the
agent, the repository and the account.

**The moment** - the activity state before and after, whether this was a live working-to-waiting
boundary or a catch-up of a turn that ended earlier, how long the session had been quiet, the quiet
threshold it was judged against, and how long each read and the whole capture took.

**The terminal** - the live grid whole and untrimmed, with the cursor cell, whether the cursor is
visible, and whether the agent has the terminal in the alternate screen buffer; and two thousand
lines of scrollback behind it. The excerpt every judgement actually reads is deliberately NOT stored
in its place: the window is a decision we will want to re-take, and keeping only the excerpt makes
that impossible once the turn is gone.

**The conversation** - the last ten full turns from both sides, a full turn being a user message and
the agent's reply together, with parts intact rather than flattened to text so a tool call and its
result survive as a tool call and its result. The cut lands ON a user message, so no agent reply
reaches the corpus without the prompt that caused it.

**What the product decided** - whether the supervisor was switched on for that account, whether the
session was a voice session, the state label and triage bucket the product actually showed, and
whether it had already decided the session needed the owner.

**The verdict** - empty, until a person or a reviewing seat fills it in.

**The gaps** - anything that could not be collected, named with the reason.

## The decisions behind it

| Decision | Owner's, or taken here |
| --- | --- |
| Ten full turns, both sides | Owner |
| Log everything about the session; overlap between records is fine | Owner |
| Store it locally, pulled into `devthrottle_internal` | Owner |
| Switchable per account AND per machine, from an admin screen, including accounts that are not ours | Owner |
| No expiry on a switch; permission is obtained by the owner before one is thrown | Owner |
| Every turn end, not a sample | Here - a miss is rare by definition, and sampling discards exactly those |
| One compressed bundle per day per machine | Here - four hundred loose files a day, and several gigabytes a year uncompressed |
| Independent of the supervisor, at the cost of a second screen read | Here - see below |

## Why it is not part of the supervisor

The supervisor already reads the screen at this exact boundary, so riding its read was the obvious
saving. It is the wrong design.

The most valuable thing this instrument can capture is a turn end the supervisor did NOT act on. Those
misses write no line anywhere today, which is precisely why our own measurement said the supervisor
works while the owner's experience said it works only sometimes - the evidence could never have
contradicted him. A log living inside the supervisor falls silent exactly when the supervisor does,
reproducing the blindness it exists to cure. So it hangs off the boundary independently and pays for
its own screen read.

## The three properties, and the tests that hold them

**It goes first on the turn-end path.** The supervisor and the rules engine can both type into a
session, and either changes the screen. A capture started after them would record the screen *after
we intervened* while the record claims to be the screen the turn ended on - and the corpus would then
teach us that faults recover themselves.

**Switched off, it reads nothing.** No screen, no scrollback, no conversation, no file.
`OnTurnEnd_CaptureSwitchedOff_ReadsNothingAtAll` asserts it. An instrument that costs a tunnel round
trip per turn end while switched off is measuring itself.

**What it fails to collect is named.** A part that could not be gathered goes into the record's gaps.
An absent field and an unavailable one look identical in a corpus and mean opposite things, and the
holes would land exactly where the interesting turns are.

## What is NOT proven

- **No record has been written by a real Gateway against a real Director.** Everything is unit-level.
  The first real capture happens after the deploy.
- **What the machinery DECIDED is thin.** The record carries whether the supervisor was switched on,
  not the verdict it reached on that screen, and not what the rules engine made of it. Capturing
  those means reaching inside two live features to have them report into the record. Deferred
  deliberately: a week of real screens is worth more than a perfect record shape, and the screens are
  gone if we wait.
- **The scrollback read is a second tunnel call per turn end** on top of the screen read. Its cost on
  a busy fleet has not been measured, only bounded - the capture has a thirty-second ceiling and runs
  off the turn path.
- **The admin screen on the website has not been built.** The capability is a Gateway endpoint; the
  screen that calls it is not written yet, so a switch is currently thrown by calling the endpoint.
- **Nobody has labelled a record.** Until verdicts exist, the corpus is raw material and not yet a
  test set. An unlabelled record is evidence of nothing.

## The questions it was built to answer

1. How often does a turn end in a state that actually needs the owner? Nobody knows, and the
   needs-me judgement is being designed without the number.
2. How much would a word table have caught? Replay the supervisor's classifier over the corpus and
   count, instead of arguing it.
3. How many turn ends does the supervisor MISS? Today a miss writes no line, so it is uncountable.
4. Does the spoken summary's call - already made on 82 percent of turn ends - already contain enough
   to answer the others?
5. Why can a snooze never outlast half an hour? Capture the byte some agents tap into the terminal
   every thirty minutes and see it for what it is.
