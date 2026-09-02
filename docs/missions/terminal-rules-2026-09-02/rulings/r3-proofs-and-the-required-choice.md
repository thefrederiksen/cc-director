# Ruling 3 - the voice-turn proof passes when nothing happens; and the choice stays required

Architect ruling. Binding.

## Verified independently, not taken on report

The one-caller claim checks out. `git grep GetScreenGridAsync` over
`origin/mission/terminal-rules` returns three lines: the method's own definition on
`SessionVerbClient`, a doc comment, and exactly one real call - `GatewayScreenReader.cs:130`, inside
the reader. There is no second path to the tunnel.

Counting on the tunnel SEND rather than on a caller is right, and the reasoning given for it is the
reason: a counter kept on the callers cannot see a caller added later, and would read zero while
round trips were being made. Same for `LiveScreenRead` carrying `Unreadable` as a named source with a
null grid - unreadable stays a real answer and no caller's fail-closed branch moves.

## The hole: the voice-turn proof passes when the voice turn never happened

> the voice-turn proof will be counter-difference plus a known-bad control run showing the counter
> DOES move when the store cannot answer.

The known-bad control is good and keep it - it proves the **instrument** works, which is the thing
most people skip. But it does not close the hole, because it tests the wrong end.

The proposed pass condition is *the counter did not move*. A voice turn that **crashed on its first
line, was never triggered, or silently produced no narration** also does not move the counter. All
three of those pass this proof. The claim being made is "a voice turn completes with no tunnel screen
read", and half of that claim - the half that says a turn happened at all - is not being measured.

**The pass condition must be a conjunction, and both halves must be positive artifacts:**

1. **The turn completed** - proven by something the turn PRODUCED. Narration audio exists, or the
   turn's own completion record is present. Not "no error was logged". Not "the call returned".
2. **AND the pull counter is unchanged across it** - with the counter read immediately before and
   immediately after that same turn, not across the suite.

If the first half cannot be evidenced, the proof is not weakened, it is absent - report it as absent
rather than reporting the counter.

## This is the third one, so it is a class, not three mistakes

- **Ruling 1**: serve the stored screen while the byte count is unchanged - passes when no bytes are
  ARRIVING.
- **Ruling 2**: take the migration slot unless someone objects - passes when nobody answers.
- **Ruling 3**: the voice turn made no tunnel pull - passes when there was no voice turn.

One shape: **a pass condition that is satisfied by nothing happening.** Silence, stillness and
absence all read as success.

The test to apply to every remaining proof in this mission, before writing it: *if the thing I am
measuring never ran at all, does my check still pass?* If yes, restate it as a PRESENCE - name the
artifact that must exist, and treat an empty result as a broken instrument rather than a clean run.
`cc-devthrottle skill get checks-that-fail-open` is the fleet's writeup of this; read it once and
apply it to the store, sweep, tenant-scoping and offline proofs too, not only this one.

The tenant-scoping proof is the one to look at hardest under this test: "account B could not read
account A's screen" is absence-shaped by construction. Restate it so account B's request produces a
NAMED refusal, and show account B successfully reading its OWN screen in the same run - otherwise a
misconfigured account B that can read nothing at all passes it.

## The required choice stays required - do not default it

Eleven test call sites broke because the new parameter has no default. **That is the parameter
working.** It makes every caller state which of the two questions it is asking, and the two are not
interchangeable - one may be answered from a stored screen, the other may get a keystroke pressed on
it.

Do not give it a default to save touching those eleven. A default silently picks a mode for every
caller written after today, including one whose author never read ruling 1 - and it would pick it
invisibly, which is the same family of defect as the three above. Fix the eleven call sites, each
choosing deliberately. If a test's correct answer is genuinely "either", that test is not exercising
the distinction and should say so in a comment.
