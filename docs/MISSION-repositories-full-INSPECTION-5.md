# Inspection round 5 - findings, rulings, and outcomes

Mission: repositories-full. Branch: mission/repositories-full (round-4 fixes through ef85357f).
Inspector: the same independent reviewer, judging the round-4 fix diff. Verdict: BLOCK.

CLOSED this round: R4-2 (exact missing-ref cleanup), R4-3 (pending-compute registration),
R4-4 (scan lifecycle - including confirmation that the reworked round-3 test was NOT
weakened), R4-5 (gone-path observation stamping, with the injected repository predicate
confirmed to preserve the prior semantics exactly). Remaining: three findings, all in the
round-4 fix code itself. The fixes for these were made directly by the Architect (as with
the startup-wiring fix in commit e3b9d01a) because each was a surgical change of a few
lines; both carry regression tests watched failing first, and the round-6 inspection judges
them like any other fix.

## R5-1 (MAJOR) An exception from the destructive delete command bypasses restoration

Finding: the delete await preceded the compensation try block. A process-layer or injected-
runner exception thrown AFTER the child git process already deleted the ref escaped without
any restore attempt.

Ruling: the delete command itself moves inside the recovery boundary. The create-only
restore is safe in both directions - if the delete never happened, the ref still exists and
git refuses the create - so restore-on-the-way-out covers "threw before mutation" and
"threw after mutation" without knowing which occurred. The pre-delete cancellation check
stays OUTSIDE the boundary (a pre-delete cancellation mutated nothing and needs no restore
attempt), and a REFUSED delete (git said the ref moved) keeps its existing return path.

Outcome: FIXED in commit 804eb3ee. Regression test watched failing first
(Delete_DeleteCommandThrowsAfterItsMutation_RestoresTheRefBeforeTheExceptionPropagates,
with the new MutateThenThrowGitRunner seam that runs the mutation to completion and then
throws): before the fix the branch stayed deleted with the worktree broken; after it, the
ref is restored and the original exception still propagates.

## R5-2 (MAJOR) Diagnostic logging can defeat restore-on-the-way-out

Finding: FileLog.Write can throw during a concurrent logger shutdown (its queue refuses
adds once stopped). The log line before the restore attempt could skip restoration; the log
line before the rethrow could replace the original exception.

Ruling: logging inside the destructive-recovery path - and only there - is best-effort by
design: a SafeLog helper swallows logging failures, because in this one path the restore
outranks the log. Everywhere else a logging failure still surfaces normally.

Outcome: FIXED in commit 804eb3ee (same commit - same recovery path). Structural change
with no direct unit test: forcing the global logger to throw mid-test would destabilize
the parallel suite. Recorded as an accepted gap; the helper is three lines and the round-6
inspection reviews it directly.

## R5-3 (MINOR) A throwing progress subscriber skips the deferred-recompute drain

Finding: the owning scan's exit raised ProgressChanged and then drained; a subscriber
exception between the two stranded the deferred requests after IsScanning was already
cleared.

Ruling: the drain sits in a finally - it runs whatever the subscriber does, and the
subscriber's exception still propagates loudly.

Outcome: FIXED in commit e4fc3870. Regression test watched failing first
(Rescan_ProgressSubscriberThrowsAtExit_StillDrainsTheDeferredRecomputes): before the fix
the deferred recompute was stranded (one compute instead of two); after it, the drain runs
and the exception escapes.

## Suites after the round-5 fixes

Core 3450 passed / 0 failed / 8 skipped (includes the two new regression tests).
Avalonia 290 passed / 0 failed. Gateway rerun in progress at the time of writing; the
round-5 fixes touch no Gateway code.

## Status

- [x] Fixes implemented and committed per ruling, with tests
- [x] Core + Avalonia suites green (Gateway rerun for the record in progress)
- [ ] Sixth inspection pass (in progress)
- [ ] QA report to the owner
