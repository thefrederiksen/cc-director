# Inspection round 2 - findings and Architect rulings

Mission: repositories-full. Branch: mission/repositories-full (round-1 fixes through 53f4f97a).
Inspector: the same independent reviewer, judging the round-1 fix diff. Verdict: BLOCK.
Closed cleanly: F4, F8, F9, F10 (worktrees side), F11, F12. The rest carry residual gaps,
one fix regressed, and three new findings arrived. Rulings below are binding; the Manager
verifies each against the code first and records the outcome under each item.

Conduct: unchanged from round 1 - fix and regression test are one commit, plain English,
no attribution, ASCII only, no fallback programming.

## R2-1 (from F3) REGRESSION: update-ref delete bypasses checked-out-branch protection

Finding: "git update-ref -d refs/heads/x <sha>" binds the tip but does not reproduce
"git branch -D"'s refusal to delete a branch that is checked out. A checkout into a linked
worktree between verification and deletion leaves that worktree with a broken symbolic HEAD.
Also: the branch config section can be removed AFTER another process recreated the branch.

Ruling: keep the tip-bound atomic delete, add a compensating post-check. Immediately after
a successful update-ref delete, list the worktrees; if any worktree HEAD symbolically
referenced the deleted branch, RESTORE the ref at the verified sha (we hold the exact sha,
so restoration is lossless) and report "branch is checked out in a worktree - restored, not
deleted". Remove the config section only after confirming the ref is absent at that moment;
the residual millisecond window there is accepted and documented (worst case: a just-
recreated branch loses its tracking config - annoying, never destructive to commits).
Test: delete a branch that a linked worktree has checked out; the branch must survive (or
be restored) and the worktree HEAD must remain valid.

Outcome: FIXED in commit e572d940. Verified: DeleteAtVerifiedTipAsync ran update-ref -d and
then removed the config section unconditionally, with no post-delete worktree check (the git
premise was probed first: after the delete, worktree list --porcelain still names the branch
for the broken worktree, and recreating the ref at the held sha restores it losslessly). Fix:
after a successful delete the worktrees are listed (the primary checkout included); any HEAD
still referencing the branch triggers a lossless restore at the verified sha and the refusal
"branch is checked out in a worktree - restored, not deleted"; a failed worktree listing also
restores (fail closed); the config section is removed only after confirming the ref is absent
at that moment, with the residual window documented in the method remarks. Test:
Delete_BranchCheckedOutInAWorktreeAfterVerification_IsRestored_AndTheWorktreeHeadStaysValid -
run against the unfixed code first and seen failing (the delete succeeded).

## R2-2 (NEW, MAJOR) A cancelled scan can publish after a newer scan removed the repository

Finding: RescanAsync awaits the compute, then publishes without re-checking cancellation;
a superseded scan can republish a repository the newer scan just removed.

Ruling: publishes are guarded by compute-start ordering (see R2-5) AND a cancelled scan
never publishes: after the compute returns, re-check the token (and that this scan is still
the owner) under the gate before writing to the model. Test: a scan cancelled after compute
but before publish leaves the newer scan's model untouched.

Outcome: FIXED in commit f7391c1d (on top of the R2-5 publish path, fc88f6e8). Verified:
RescanAsync published after the compute with no token re-check - the next loop iteration's
check was too late. Fix: the token AND scan ownership (the scan's cancellation source is
still the monitor's current one) are re-checked under the gate at the publish itself and at
the reconcile. Tests: Rescan_CancelledAfterComputeButBeforePublish_NeverPublishes (seen
failing before the fix - the stamps alone cannot catch it because no newer compute ruled the
key) and Rescan_Superseded_CannotRepublishARepositoryTheNewerScanRemoved (the inspector's
scenario; already held via the R2-5 removal stamp, kept as the named guard).

## R2-3 (NEW, MAJOR) /repositories is fail-open for old-Director provisional safe counts

Finding: the zero-safe-count fold runs on the fixed Director's outgoing mapper only. A
pre-fix Director pushes Provisional=true with WorktreesSafeToReap greater than zero, and
the Gateway serves that through GET /repositories unchanged (only /worktrees got the
serve-time fold).

Ruling: the Gateway owns the verdict at SERVE time for both endpoints. GET /repositories
applies the same fold before serving: a provisional repository's safe count serves as
zero and its nested worktree states serve as "verifying". One shared fold, two call sites,
covered by a test that feeds the old-Director shape (Provisional=true, stale safe count).

Outcome: FIXED in commit 9c24bad8. Verified: GET /repositories served the pushed rows
verbatim - only /worktrees had a serve-time fold. Fix: the contracts assembly carries the one
repository-level fold (FleetWorktreeFold.FoldRepositoryForServe - provisional serves zero
safe count and "verifying" worktrees, as a copy, never mutating the cached instance); the two
call sites are the GET /repositories serve path and the Director's outgoing mapper, which now
builds the raw DTO and applies the same shared fold instead of its inline copy. Tests:
FoldRepositoryForServe_OldDirectorProvisionalShape_ServesZeroSafeCount_AndVerifyingWorktrees
(feeds the exact old-Director shape: Provisional=true, safe count 2, states "safe-to-reap")
and FoldRepositoryForServe_VerifiedRepository_PassesThroughUnchanged.

## R2-4 (NEW, MAJOR) A repeated Hello on the same connection resets sequence protection

Finding: RegisterConnection sets LastSequence = -1 unconditionally, so a second Hello on
the SAME connection lets an older sequence replay.

Ruling: reset the baseline only when the connection id actually changes; a repeat Hello
from the current connection keeps the existing baseline. Test: push sequence 100, repeat
Hello on the same connection, push sequence 50 - rejected.

Outcome: FIXED in commit a7372d80. Verified: RegisterConnection set LastSequence = -1
unconditionally. Fix: the baseline resets only when ownership changes to a different
connection; a repeated Hello from the current connection is a logged no-op. Test:
RepeatedHello_OnTheSameConnection_KeepsTheSequenceBaseline (the ruling's exact sequence:
push 100, repeat Hello, push 50 rejected) - run against the unfixed code first and seen
failing (sequence 50 was accepted).

## R2-5 (from F6) Scan-boundary races are narrowed, not linearized

Finding: the IsScanning check and the semaphore acquisition are not atomic, so an older
compute can still publish over a newer one across the scan boundary in both directions;
deferred recomputes discard the caller's token and swallow failures.

Ruling: enforce "newest compute wins" at the PUBLISH, not the lock: every compute takes a
monotonically increasing start stamp; the model records the stamp per key; a publish whose
stamp is older than the recorded one is dropped. The per-repository semaphore stays (it
is an efficiency device, not the correctness device). Deferred recomputes keep their own
tokens; a deferred request whose token is cancelled is skipped; a deferred failure is
logged as an ERROR, never silently absorbed into a success path. Tests: the two boundary
orderings the inspector described both end with the newer result in the model.

Outcome: FIXED in commit fc88f6e8. Verified: the IsScanning check and the semaphore
acquisition were not atomic; the drain used the scan caller's token and caught every
exception into an ordinary log line. Fix: every compute takes a monotonically increasing
start stamp; both publish sites go through one guarded method (PublishIfNewestLocked) that
drops a publish whose stamp is older than the key's recorded one; a removal (scan reconcile
or a gone-path recompute) counts as a publish of "absent" and is stamped, so an older
in-flight compute can never resurrect a removed repository. Deferred requests keep their
requester's own token (skipped when cancelled, cancellation logged as such at drain), and a
deferred failure is logged as an ERROR. Tests (both run against the unfixed code first and
seen failing): RecomputeOne_StartedBeforeAScanThatRemovedTheRepository_CannotResurrectIt
(boundary ordering 1 - the late publish resurrected the removed repository) and
RecomputeOne_DeferredThenCancelledByItsRequester_IsSkippedAtDrain (deferral semantics - the
drain ran the cancelled request under the scan's token).

## R2-6 (from F1) The detail-screen guard fails open on an unknown entry

Finding: OpenDetail refuses a KNOWN provisional entry but proceeds when FindForPath
returns null - an unknown path reaches the destructive detail surface.

Ruling: fail closed. OpenDetail requires a positively known, verified entry
(non-null AND Provisional=false); anything else is refused. Test: unknown path is refused.

Outcome: FIXED in commit 0e7c0768. Verified: OpenDetail refused only a known provisional
entry and proceeded on null. Fix: only a positively known, verified entry (present in the
model AND not provisional) opens the detail screen; null and provisional are both refused
with the reason logged. Test: OpenDetail_UnknownPath_IsRefused_FailClosed - run against the
unfixed code first and seen failing (the detail page opened for an unknown path).

## R2-7 (from F2) Multi-valued merge configuration defeats the upstream probe

Finding: git permits multiple branch.<name>.merge values; --get returns one, and the probe
can rule "upstream gone" while another configured merge ref survives.

Ruling: fail closed on ambiguity. Read with --get-all; more than one merge value means the
branch is NOT eligible for the origin-gone signal (C2 simply does not apply, same as a
missing value). Test: a branch with two merge values is never ruled safe via C2.

Outcome: FIXED in commit 66c071c8. Verified: the probe used --get, which silently returns
the LAST of multiple merge values (probed against real git first). Fix: the probe reads with
--get-all and more than one merge value makes the branch ineligible for the origin-gone
signal, exactly as if no upstream were configured; both callers (branch safety and the
worktree inventory) share the probe. Test:
Branch_WithTwoConfiguredMergeValues_IsNeverRuledSafeViaUpstreamGone (two merge refs, the one
--get would select deleted, the other surviving) - run against the unfixed code first and
seen failing with the exact false verdict ("Origin branch deleted after merge").

## R2-8 (from F5) The live-sessions source is fail-open until wired

Finding: LiveSessionsProvider is a nullable property; a scan or watcher recompute that
runs before MainWindow assigns it silently publishes session-blind classifications.

Ruling: scanning without a session source is a programming error and fails loudly:
RescanAsync and RecomputeOneAsync throw InvalidOperationException when no provider is
wired. The app wires the provider BEFORE it triggers the first background rescan (reorder
startup). Tests construct monitors with an explicit stub provider. Test: an unwired
monitor's scan throws.

Outcome: FIXED in commit 0be45623. Verified: the provider was nullable with a fail-open read
(provider is null -> compute with null sessions), and App.InitializeServices started the
first rescan BEFORE MainWindow (which wires the provider in its constructor) existed. Fix:
RescanAsync and RecomputeOneAsync throw InvalidOperationException while unwired, the
mid-compute fetch throws instead of returning null, and the first rescan moved out of
InitializeServices into ShowMainWindow immediately after the MainWindow constructor - the
wire-before-scan ordering is structural. All monitor-constructing tests across the Core and
Avalonia suites now wire an explicit stub provider. Test:
Monitor_WithoutALiveSessionsProvider_RefusesToScanOrRecompute.

## R2-9 (from F13) History key still conflates Directors; path normalization is thin

Finding: the key omits DirectorId (two Directors on one machine name and path overwrite
each other), and raw lowercasing plus untrimmed separators conflate or split paths.

Ruling: the key includes DirectorId (tenant|date|machine|director|path); paths are
trimmed of trailing separators before keying; lowercasing stays (a case-only collision on
a case-sensitive filesystem merges two history rows - accepted and documented, never
destructive). Test: same machine and path, two Director ids - two rows.

Outcome: FIXED in commit bb27d762. Verified: the key was tenant|date|machine|path with raw
untrimmed paths. Fix: the key is tenant|date|machine|director|path with trailing separators
trimmed before lowercasing; rows without a Director id are ignored on load and on observe
(the same no-migration rule as F13's pathless rows); the lowercase acceptance is documented
at the key. Tests (all three run against the unfixed code first and seen failing):
SameMachineAndPath_TwoDirectors_KeepSeparateRows, TrailingSeparator_DoesNotSplitTheRow,
Load_IgnoresLegacyRowsWithoutADirectorId.

## R2-10 (from F7) Residual: cancellation cannot interrupt a single blocked filesystem call

Ruling: REJECTED as a further code change, with rationale recorded here: cancellation is
now honored per enumerated file; the residual exposure is one blocked filesystem call
inside Directory.EnumerateFiles, which no token can interrupt without imposing a timeout,
and a timeout that silently truncates a size is the exact lie the no-fallback rule forbids.
The compute runs on a background thread; a blocked walk delays freshness, never the UI.
Documented as a known limitation in the measurement's remarks.

Outcome: REJECTED per the ruling - no code change. The documented-limitation remark was
added to MeasureWorktreeBytes in commit ba0d48b3.

## R2-11 (MINOR) Per-repository semaphores are never reclaimed

Ruling: evict semaphore entries alongside the size-cache eviction after a completed scan:
remove entries whose key is no longer in the model and whose semaphore is currently
un-held. The brief cross-over where a re-created key briefly holds two semaphores is
accepted and documented (single-flight degrades to double-flight for one compute; the
publish-stamp rule from R2-5 still guarantees the newest result wins).

Outcome: FIXED in commit 98e73336. Verified: _repoLocks had lookup and insert only. Fix:
after each completed scan, alongside the size-cache eviction, lock entries whose key left
the model and whose semaphore is un-held are removed (a held semaphore has a compute in
flight and is kept; evicted semaphores are not disposed because a stale reference may still
be awaited); the crossover is documented in the code. The R2-5 publish-stamp map gets the
same eviction so it cannot become the next process-lifetime leak (a removal stamp survives
exactly one more completed scan - long enough to drop any late publish from a compute that
started before the removal). Tests:
CompletedScan_EvictsSemaphores_ForRepositoriesNoLongerPresent and
CompletedScan_KeepsTheSemaphore_WhileAComputeStillHoldsIt.

## R2-12 (from F14) Test gaps

Ruling: every ruling above lands with its named regression test (that covers the
inspector's items for the fixes themselves, including the old-Director /repositories
compatibility shape). Of the remaining wish-list: a real-git watcher-during-scan test is
IN scope (it exercises R2-5 end to end). The hub-level reconnect integration test, the
mixed repository/session shared-sequence test, and the malformed-field CLI tests are
recorded as honest gaps for the QA report rather than blockers - each guards a surface
already covered by a unit-level test of the same rule.

Outcome: SATISFIED. Every ruling above landed with its named regression test in the same
commit (including the old-Director /repositories compatibility shape under R2-3). The
in-scope real-git watcher-during-scan test landed in commit c256484e:
Watcher_FiringDuringAScan_IsDeferred_AndTheNewestStateLandsAfterTheScan - a real
FileSystemWatcher over a real .git directory fires on a real commit made while a scan is
mid-flight; the recompute is proven deferred while IsScanning is true, and after the scan
publishes its pre-commit result the drained recompute lands the post-commit head (ruling
R2-5 end to end). HONEST GAPS for the QA report, per the ruling: the hub-level
reconnect/disconnect integration test over real Hello/OnDisconnectedAsync ordering, the
mixed repository/session shared-sequence test, and the malformed or missing-field
command-line tests.

## Status

- [x] Fixes implemented and committed per ruling, with tests (R2-1 e572d940, R2-2 f7391c1d,
      R2-3 9c24bad8, R2-4 a7372d80, R2-5 fc88f6e8, R2-6 0e7c0768, R2-7 66c071c8,
      R2-8 0be45623, R2-9 bb27d762, R2-10 ba0d48b3 remark only, R2-11 98e73336,
      R2-12 c256484e)
- [x] Core + Avalonia + Gateway suites green (Core 3433 passed / 0 failed / 8 skipped;
      Avalonia 290 passed / 0 failed; Gateway 3678 passed / 0 failed / 17 skipped, the skips
      being the usual gated live-Postgres proofs)
- [ ] Third inspection pass
- [ ] QA report to the owner
