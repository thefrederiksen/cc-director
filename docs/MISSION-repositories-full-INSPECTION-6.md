# Inspection rounds 6, 7, and 8 - findings, rulings, outcomes, and the PASS

Mission: repositories-full. Branch: mission/repositories-full.
Inspector: the same independent reviewer, one round per fix diff. These three rounds
judged the tail of the loop - each one a diff of a few files - and ended in the mission's
first PASS verdict. Fixes in these rounds were made directly by the Architect (surgical
changes of a few lines each); every one carries a regression test watched failing first,
except where the record below says a trigger is untestable and why.

## Round 6 (BLOCK) - judging the round-5 fixes (804eb3ee, e4fc3870)

All three round-5 fixes CLOSED. Two new findings:

R6-1 (MAJOR): pulling the delete inside the recovery try also pulled the REFUSED-delete
return in with it - a throwing log line on the refusal path would reroute into the restore
attempt, and when the branch was already entirely gone, that restore would RECREATE a
branch another process had legitimately deleted.
Ruling: the refusal is reported OUTSIDE every recovery boundary. Two separate boundaries
(delete command; compensation) with the refusal between them; the restore factored into
one never-throwing helper used by both.
Outcome: FIXED in 35f83481. The regression trigger (a log line throwing during logger
shutdown) is untestable without destabilizing the global logger for the parallel suite;
the restructure makes the reroute impossible by construction, and the new test
Delete_RefusedBecauseTheBranchIsAlreadyGone_NeverRecreatesIt pins the reachable
invariant.

R6-2 (MINOR): with the drain awaited inside a finally, a drain failure silently replaced
an in-flight progress-subscriber exception.
Ruling: capture the subscriber exception, rethrow it with its type intact after the
drain; when both throw, both surface together in one aggregate.
Outcome: FIXED in 1fa7bb1e. The existing round-5 test still proves the subscriber
exception propagates with its exact type.

## Round 7 (BLOCK) - judging the round-6 fixes (35f83481, 1fa7bb1e)

Both round-6 fixes CLOSED. One new finding:

R7-1 (MAJOR): Exception.Message is virtual; the restore helper's diagnostic evaluated it
BEFORE the restore attempt, so an exception with a throwing Message getter - surfaced by
the process layer after the delete mutated the ref - skipped the restore entirely and
replaced itself with the getter's exception.
Ruling: the restore attempt comes first; every diagnostic is after the fact; message
extraction goes through a guarded helper (falls back to the exception type name).
Outcome: FIXED in 2a0f6188. Test watched failing first
(Delete_ExceptionWithAThrowingMessageGetter_StillRestoresAndPropagatesTheOriginalType,
with a ThrowingMessageException seam): before the fix the branch stayed deleted and the
wrong exception type surfaced; after it the branch is restored and the original type
propagates.

## Round 8 - judging the round-7 fix (2a0f6188)

R7-1 CLOSED: argument construction before the restore touches only existing strings and
constants; both later message accesses are guarded; neither toxic getter can replace the
original exception.

One MINOR: the refused and failed restore log arms no longer named the initiating
exception. Fixed as prescribed (SafeMessage(cause) appended after the restore attempt in
both arms) - the change is entirely inside the after-the-fact logging, so no further
inspection round was called for it.

VERDICT: PASS - nothing above MINOR remains.

## Where that leaves the loop

Eight rounds total: 14 findings in round 1, narrowing every round, PASS in round 8.
Every fix landed with a regression test watched failing first except the two recorded
untestable triggers (logger-shutdown throw; both documented in the code). The accepted
limitations and test gaps are recorded in rounds 2 through 5 and restated in the QA
report.
