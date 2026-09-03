# Inspection 02 - the brief, written before the fixes land

Written by the Architect in advance, deliberately. Writing the inspection brief after seeing the
fixes lets the fixes shape the questions, which is how a second pass becomes a rubber stamp.

Seat a fresh inspector from a **different agent family to the fix Manager**, in its **own worktree**
cut from `mission/terminal-rules`, when the fix round reports. It writes to
`inspection-02.md` and replies with one line. It fixes nothing.

## What it is inspecting

The round answering `inspection-01.md`'s six findings, governed by `rulings/r12` and `r13`, planned
in `fix-round/plan.md`. Its diff is `git diff origin/main...HEAD`.

**Tell it plainly: the previous round was reported complete with 3,189 tests green and six real
defects in it.** That is not a slur on the builder - it is the base rate this inspection exists for.

## The standing questions, same as inspection 01

1. What does the report CLAIM that the code does not support? Quote both sides.
2. Where can a constant be substituted, a guard deleted or a branch inverted with the suite still
   green? Mutate, run, revert - actually do it.
3. Any proof that still passes when the thing it measures never ran.
4. Anything unguarded: unbounded write, missing validation, swallowed exception, a bound that can be
   exceeded, a row retained past seven days.

## The questions specific to THIS round - the ones I would get wrong

**Every fix was supposed to owe a test that failed first.** Take that at face value and check it:
`git stash` the production fix, run its test, and confirm it goes red. A fix whose test passes
without it has tested nothing, and that is finding 4 recurring inside the round that answers finding
4. Do this for all six.

**Finding 1 - is the live path really gone, everywhere?** `ReadLiveAsync` must never return a stored
screen by any route. Look for a survivor: a caller that still reaches the store for a live question,
a cached grid, a helper that reads `ReadStored` and treats the result as current, or a re-entry
through the wingman or supervisor paths. The mechanism was deleted late and under time pressure;
deletions leave stumps.

**Finding 2 - the new mark.** The claim is now that the mark and the frame come from one consistent
observation because the counter is incremented inside the parser lock. Try to break it. Is there any
path that increments the counter without holding that lock, or produces rows without the counter?
Reproduce the original rendezvous and confirm it now fails to produce the bad pairing.

**Finding 3 - the key.** `DirectorId` joining the key must actually prevent the collision, not just
appear in the type. Two Directors, same session id, same capture instant, distinct rows: both stored
and both readable.

**Finding 4 - the instrument that caught the last round.** Re-run the inspector 01 mutation exactly:
replace the sink's mapped rows with a constant. The mapping test must go red **in the default gate**,
without a rig. If it does not, the round's headline fix did not land.

**Finding 5 - the drop counter.** Does it move by exactly one on a drop and not at all on a
successful push? And does any surviving prose still claim a miss costs "never a record"?

**Finding 6 - the sweep's over-cap trim.** Prove the repair, and prove it does not delete rows it
should keep - the same shape as the retention row's control.

**And the rows themselves (r13).** Row 7 must be WITHDRAWN, not re-run or re-scoped. Row 6 must be
replaced, not left passing on the absence of the mechanism. Check the proof plan and the report for a
row that survived by being quietly reworded.

**The report.** It was to be rewritten, not amended. Does its opening describe the system that now
exists, or the deleted three-fact certification with corrections bolted on?

## Reporting

Findings need file and line, what is claimed, what is true, and how it was established. "Looks wrong"
is not a finding. A clean inspection honestly reported is a valid result; inventing findings to look
thorough is worse than none. If the round is genuinely sound, say so and say what you tried.
