# Inspection round 1 - findings and Architect rulings

Mission: repositories-full (devthrottle_internal#510). Branch: mission/repositories-full.
Inspector: independent Codex review of the full mission diff (5950 lines), verdict BLOCK.
This file records each finding and the Architect's ruling on how it is to be fixed. The
Manager verifies each finding against the code first; a finding that turns out not to be
real is documented here as rejected, with the evidence, instead of being "fixed".

Conduct: every fix lands WITH its regression test in the same commit (a fix and its guard
are one unit). Plain-English commit messages, no abbreviations, no attribution of any kind,
ASCII only. No fallback programming: fail loudly, never silently degrade.

Fix round outcome summary (Manager, 2026-07-24): all fourteen findings verified against the
code; thirteen confirmed and fixed with regression tests, one commit per finding (F4/F5/F6
land together as one coherent monitor reshape). One half of F1's ruling (the recommendation
engine skipping provisional entries) was found already implemented and tested on the branch;
the evidence is recorded under F1. F14 is satisfied by the tests the fixes added.

## F1 (BLOCKER) Provisional repository rows still expose destructive actions

Finding: warm-start (provisional) rows are dimmed but clickable; opening one attaches its
cached path to the detail screen where stage, commit, discard and branch deletion are all
reachable. Only the Worktrees panel guards provisional state.

Ruling: a provisional entry must never open the detail screen. The row click is ignored
while the entry is verifying (the row already carries the "verifying" chip that explains
why). Additionally the recommendation engine must skip provisional entries entirely - a
recommendation is an acting surface. Test: a provisional entry's row click does not open
detail; recommendations over a provisional entry list are empty.

Outcome: FIXED in commit 3b7bc63e. Verified: the row click and RepositoriesView.OpenDetail
ignored Provisional. Fix: a still-verifying row never opens the detail screen (guarded at the
row click AND at OpenDetail, so every route in is covered). Note: the second half of the
ruling - the recommendation engine skipping provisional entries - was ALREADY implemented on
the branch before this fix round (RecommendationEngine.Evaluate filters on !r.Provisional,
guarded by the existing test ProvisionalEntries_NeverGenerateRecommendations); no change was
needed there. Tests: ShouldOpenRow_ProvisionalRow_IsNotOpenable_VerifiedRowIs and
OpenDetail_ProvisionalEntry_IsRefused_VerifiedEntryOpens.

## F2 (BLOCKER) C2 can produce a false "origin gone" verdict

Finding: both WorktreeInventoryService and GitBranchService treat any branch.<name>.merge
value as proof an upstream existed, then test existence of origin/<local-name> - ignoring
the configured remote and the configured upstream ref name. A branch tracking a different
remote, or an upstream with a different name, can be falsely ruled origin-gone and safe.

Ruling: C2 means "the CONFIGURED upstream ref no longer exists on the CONFIGURED remote".
Read branch.<name>.remote and branch.<name>.merge; query that remote for that ref. If
either config value is missing the branch is not eligible for C2 (unchanged). Fix both
implementations. Regression tests: (a) upstream ref name differs from the local name and
still exists on the remote - NOT safe; (b) configured upstream genuinely deleted - safe;
(c) the existing never-pushed and squash-merge tests stay green.

Outcome: FIXED in commit 6706845d. Verified: both implementations read branch.<name>.merge
only as an existence gate, then queried ls-remote origin <local-name>. Fix: a shared
ConfiguredUpstreamProbe reads branch.<name>.remote plus branch.<name>.merge and queries the
configured remote for the configured ref; either value missing means C2 does not apply. Both
new tests were proven to FAIL against the old code before the fix landed. Tests:
Branch_TrackingDifferentlyNamedUpstream_ThatStillExists_IsNotSafe,
Branch_WhoseConfiguredUpstreamWasDeleted_IsSafe,
WorktreeBranch_TrackingASecondRemote_WhoseRefStillExists_IsNotSafe; the existing
never-pushed and squash-merge tests stay green.

## F3 (MAJOR) Branch force-delete has a time-of-check to time-of-use gap

Finding: the verdict is computed, then "git branch -D" runs as a separate process; a
concurrent commit can move the branch tip in between, and the force delete destroys it.

Ruling: delete atomically against the verified tip: "git update-ref -d refs/heads/<name>
<verified-sha>". Git refuses the delete if the ref no longer points at that sha - the
race window closes. On success remove the branch.<name> config section as cleanup. On
refusal, report "branch moved since it was verified - not deleted" and leave it. Test:
delete with a stale expected sha is refused and the branch survives.

Outcome: FIXED in commit 11c43c71. Verified: verdict and "git branch -D" were separate
subprocess calls with nothing binding the verified tip to the delete. Fix: BranchInfo carries
TipCommit; deletion runs "git update-ref -d refs/heads/<name> <verified-sha>", which git
refuses when the ref moved; on success the branch.<name> config section is removed. Tests:
Delete_WithAStaleVerifiedTip_IsRefused_AndTheBranchSurvives,
Delete_SafeBranch_RemovesTheBranchConfigSection.

## F4 (MAJOR) The reaper and the monitor can receive a linked-worktree path

Finding: WorktreesView falls back from the resolved entry path to the raw session path,
which may be a linked worktree; RecomputeOneAsync accepts a path whose .git is a FILE -
which is exactly what a linked worktree has - and would store it as a repository entry.

Ruling: canonicalize to the primary repository before acting or storing. Resolve
"git rev-parse --git-common-dir" from the target path and derive the primary checkout;
the reaper and RecomputeOneAsync both operate on the primary path only. A linked-worktree
path passed to RecomputeOneAsync must result in the PRIMARY entry being recomputed, never
a new entry keyed at the worktree path. Tests for both.

Outcome: FIXED in commit 127d8875 (together with F5 and F6 - one monitor reshape). Verified:
RunReapAsync fell back to the raw session path and RecomputeOneAsync accepted a .git-file
path as an entry. Fix: RecomputeOneAsync canonicalizes a linked-worktree path to its primary
checkout via git rev-parse --git-common-dir (failing loudly when it cannot); the reaper only
ever runs against the resolved repository entry - the fallback to the session path is gone.
Tests: RecomputeOne_LinkedWorktreePath_RecomputesThePrimaryEntry (real git),
Reap_FromASessionSittingInAWorktree_TargetsThePrimaryRepositoryPath,
Reap_WhenTheOwningEntryIsUnresolved_DoesNothing.

## F5 (MAJOR) Every background compute is session-blind

Finding: the app's background rescan (App.axaml.cs) and every watcher recompute pass no
live sessions, so the in-use-by-session state is only ever present after a manual panel
refresh; a watcher recompute actively ERASES it, and the erased result is pushed to the
Gateway and can mark an occupied worktree safe to reap.

Ruling: the monitor owns a LiveSessionsProvider (an async function returning the current
live sessions). RescanAsync and RecomputeOneAsync call it on every compute; the per-call
sessions parameter is removed so there is exactly one source. MainWindow wires the
provider at startup (same source the panels use today). Test: a watcher-style recompute
preserves the in-use classification supplied by the provider.

Outcome: FIXED in commit 127d8875. Verified: the watcher and the background rescan passed no
sessions, and each recompute overwrote the session-aware classification. Fix: the monitor
owns LiveSessionsProvider and consults it on every compute; the per-call sessions parameters
on RescanAsync and RecomputeOneAsync are removed; MainWindow wires the provider at startup
(the same source the panels use). Test:
RecomputeOne_ConsultsTheLiveSessionsProvider_PreservingInUse.

## F6 (MAJOR) Full scans and single recomputes can overwrite each other

Finding: RecomputeOneAsync is uncoordinated with a running scan; concurrent computes for
the same repository publish in completion order, so an older result can land last.

Ruling: single-flight per repository, and recomputes requested during a full scan are
deferred until the scan completes (then run). The invariant under test: a recompute
racing a scan can never leave the model holding the older of the two results.

Outcome: FIXED in commit 127d8875. Verified: RecomputeOneAsync was uncoordinated with a
running scan and with concurrent recomputes. Fix: computes are single-flight per repository
(a per-repository semaphore serializes compute plus publish), and a recompute requested while
a scan runs is deferred and drained after the scan completes. Both tests were proven to fail
against a mutated monitor with the lock and the deferral disabled. Tests:
RecomputeOne_DuringAScan_IsDeferred_AndRunsAfterTheScan,
RecomputeOne_RacingRecomputes_NeverLeaveTheOlderResultLast.

## F7 (MAJOR) Worktree size measurement is unbounded and non-cancellable

Finding: MeasureWorktreeBytes walks every file with no cancellation; first discovery of
many large worktrees can stall the scan pipeline with no way to supersede it.

Ruling: thread the compute's CancellationToken into the measurement and honor it during
enumeration. No file caps or byte budgets - a silently truncated size is a lie; cancel
cleanly or measure fully.

Outcome: FIXED in commit e80a7b3c. Verified: MeasureWorktreeBytes walked every file with no
cancellation path. Fix: the compute's CancellationToken is threaded into the measurement and
checked per file; cancellation propagates (GetStatusAsync no longer folds it into a failure
status) and a partial measurement is never cached or returned. No caps or budgets, per the
ruling. Tests: Measure_Cancelled_Throws_AndStoresNoPartialResult,
Measure_Uncancelled_ReturnsTheFullSize_AndCachesIt.

## F8 (MINOR) The size cache grows forever

Finding: entries for reaped, moved or deleted worktrees are never evicted from the
process-wide size cache.

Ruling: after each completed scan, evict cache entries whose path was not among the
worktrees seen by that scan.

Outcome: FIXED in commit 3b395c8e. Verified: the static size cache had lookup and insert
only. Fix: after each completed scan the monitor evicts cache entries whose path was not
among the worktrees that scan saw. Test:
CompletedScan_EvictsCachedSizes_ForWorktreesNoLongerPresent.

## F9 (MAJOR) An old SignalR connection can retake ownership of the pushed store

Finding: sequence rejection only applies to the SAME connection; a late push from a
superseded connection is accepted because its id differs. Two live connections can
alternate, each overwriting the other with staler data.

Ruling: only the director's CURRENT connection may push. The Gateway already tracks the
live connection per director for command routing; the hub checks the pushing connection
against it and drops pushes from any other. Test: connection 1 pushes, connection 2 takes
over, a late push from connection 1 is rejected.

Outcome: FIXED in commit 31ab7f86. Verified: sequence rejection applied only to the same
connection id, so a superseded connection's push was accepted as "new". Fix: the repository
store now follows the session store's ownership discipline - the hub registers the current
connection at Hello and unregisters it on disconnect, and ApplySnapshot drops any push that
is not from the current connection. Tests: LatePush_FromASupersededConnection_IsRejected
(conn1 -> conn2 -> late conn1), Push_WithoutARegisteredConnection_IsRejected,
Unregister_OnlyClearsTheCurrentConnection.

## F10 (MAJOR) Provisional status is erased from the fleet worktrees endpoint

Finding: flattening drops the provisional flag; cached (unverified) worktrees are served
as "safe-to-reap" facts, and the CLI counts their bytes as reclaimable.

Ruling: fail closed in the fold (the Gateway owns the verdict; clients stay dumb). For a
provisional repository: the flattened worktree state string is "verifying" (never
"safe-to-reap"), FleetWorktreeDto carries Provisional, and the repository DTO's safe
count folds to zero until verification completes. The CLI needs no new logic - it already
keys off the folded state string. Tests at the mapper.

Outcome: FIXED in commit 587e55d3. Verified: both flatten paths dropped the provisional flag
and served cached worktrees as safe-to-reap. Fix: one shared fold (FleetWorktreeFold in the
contracts assembly) used by BOTH the Gateway /worktrees endpoint and the Director's local
relay serves a provisional repository's worktrees as "verifying" (never "safe-to-reap") and
FleetWorktreeDto carries Provisional; the Director-side mapper folds the repository DTO's
safe count to zero while provisional. The CLI keys off the folded state string and needed no
change. Tests at the mapper:
Map_ProvisionalRepository_FoldsSafeCountToZero_AndWorktreesToVerifying,
Flatten_ProvisionalRepository_ServesVerifying_EvenWhenThePushedStateSaysSafe, plus the two
verified-path counterparts.

## F11 (MAJOR) Discard permanently destroys tracked work with one click

Finding: "Discard changes" invokes the destructive write immediately - no confirmation,
no statement of what is lost.

Ruling: a plain-words confirmation before any discard: "This permanently deletes your
changes in N files. There is no undo." with the file names visible, buttons "Delete
changes" and "Keep changes". (This matches the owner's standing instruction that
destructive actions get a recency display and a plain-English warning, not lawyer speak.)

Outcome: FIXED in commit f35c21bc. Verified: DiscardButton_Click invoked the destructive
write directly. Fix: discard first shows "This permanently deletes your changes in N files.
There is no undo." with the file names visible and buttons "Delete changes" and "Keep
changes"; only the explicit Delete runs the write, and the pending set is consumed so a
stray second confirm is inert. Tests: DiscardWarning_StatesTheLossInPlainWords,
Discard_ShowsTheConfirmation_AndKeepChangesRunsNoWrite,
Discard_DeleteChanges_RunsTheWrite_ForExactlyTheNamedFiles.

## F12 (MINOR) Rail navigation hides the detail screen without detaching it

Finding: navigating via the left rail hides the detail page but leaves its monitor
subscriptions live.

Ruling: leaving the detail page through ANY path detaches its subscriptions.

Outcome: FIXED in commit 7dee9aa7. Verified: only CloseDetail detached; ShowPage (the rail
buttons) only hid the page. Fix: ShowPage detaches the detail page, so leaving it through
ANY path releases its monitor subscriptions. Test:
RailNavigation_AwayFromDetail_DetachesTheDetailPage.

## F13 (MAJOR) Repository history conflates repositories that share a leaf name

Finding: the history key is tenant|date|machine|name - two repositories with the same
folder name on one machine overwrite each other's daily snapshots.

Ruling: the key includes the repository PATH (tenant|date|machine|path, lowercased);
the name stays for display. Records without a path are ignored on load (the file format
is days old and unreleased - no migration). Test: same-name different-path repositories
do not collide.

Outcome: FIXED in commit 7b272aab. Verified: the key was tenant|date|machine|name. Fix: the
key is tenant|date|machine|path (lowercased); the name stays for display; rows without a
path are ignored on load and on observe (no migration - the format is days old and
unreleased). Tests: SameLeafName_DifferentPaths_DoNotOverwriteEachOther,
Load_IgnoresLegacyRowsWithoutAPath, PathlessPushedRow_IsIgnored_NotKeyed.

## F14 (NOTE) Untested claims

The inspector's list of untested claims is accepted. Each fix above lands with its
regression test; the specific gaps called out (provisional action blocking, reaper path
canonicalization, C2 upstream shapes, stale-connection rejection, history key collision)
are exactly the tests the fixes must add.

Outcome: SATISFIED by the tests added across F1-F13 (each fix landed with its regression
test in the same commit). The specific gaps the inspector called out are covered:
provisional action blocking (F1, F10), the reaper path from a linked-worktree session (F4),
C2 upstream shapes (F2), watcher-style recomputes with the live-session provider (F5),
watcher activity during a full scan (F6), navigation detach (F12), cancelled size walks and
cache eviction (F7, F8), stale-connection rejection (F9), and the history key collision
(F13).

## Status

- [x] Fixes implemented and committed per finding, with tests (F2 6706845d, F1 3b7bc63e,
      F4/F5/F6 127d8875, F3 11c43c71, F7 e80a7b3c, F8 3b395c8e, F9 31ab7f86, F10 587e55d3,
      F13 7b272aab, F11 f35c21bc, F12 7dee9aa7; F14 via the tests above)
- [x] All three suites green (Core 3423 passed / 0 failed / 8 skipped; Avalonia 289 passed /
      0 failed; Gateway full-suite result recorded in the Manager's report)
- [ ] Second inspection pass over the fix diff
- [ ] QA report to the owner
