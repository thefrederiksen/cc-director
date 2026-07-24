# Inspection round 3 - findings and Architect rulings

Mission: repositories-full. Branch: mission/repositories-full (round-2 fixes through c9b429a2,
plus e3b9d01a - see R3-3). Inspector: the same independent reviewer, judging the round-2 fix
diff. Verdict: BLOCK.

CLOSED this round: R2-2 (cancelled-scan publish), R2-3 (/repositories serve fold), R2-4
(repeated Hello), R2-6 (unknown-path fail closed), R2-7 (multi-valued merge), R2-9 (history
identity), R2-10 (accepted limitation), R2-11 (semaphore eviction). Remaining: the branch-
delete compensation, the monitor's stamp/lifecycle correctness, and test gaps. Rulings below
are binding; the Manager verifies each first and records the outcome under each item.

Conduct: unchanged - fix and regression test are one commit, plain English, no attribution,
ASCII only, no fallback programming.

## R3-1 (MAJOR) The branch restore can overwrite a concurrently recreated branch

Finding: the compensation restore runs "git update-ref refs/heads/x <verifiedSha>" with no
expected old value. If another process recreated the branch at a new commit between the
delete and the restore, the restore silently rewinds that new branch to the old commit.

Ruling: the restore is CREATE-ONLY: "git update-ref refs/heads/<name> <verifiedSha>
<40 zeros>" - git refuses when the ref already exists. If the ref exists again, the
concurrently recreated branch stands (the worktree HEAD is valid again by that very fact)
and the outcome reports plainly that the branch was deleted and a new branch of the same
name has since appeared. Test: recreate the branch at a different commit between delete
and restore; the recreated tip must survive untouched.

Outcome: FIXED in 44f6895d. Probed against real git 2.49 first: create-only on an absent
ref succeeds; on an existing ref it exits 128 ("reference already exists") and leaves the
tip untouched. Restore now passes the forty-zero expected old value; a refused create with
the ref present reports the branch deleted and a same-named branch since appeared; with
the ref absent it stays the loud restore-failure path. GitCommandRunner unsealed (virtual
RunAsync) as the test seam. Test watched failing first: the recreated tip came back
rewound to the verified sha.

## R3-2 (MAJOR) Cancellation can bypass the post-delete compensation

Finding: everything after the successful ref delete still runs on the caller's token;
cancellation in that window skips the worktree check and the restore, leaving a checked-out
worktree with a broken HEAD.

Ruling: compensation is a non-cancellable phase. From the moment the delete succeeds, the
worktree listing, the restore decision, and the config-section cleanup run with
CancellationToken.None, structured so that every exit path (including exceptions from the
listing itself, which already restore per the round-2 ruling) completes compensation before
the method returns or throws. Test: cancel the token immediately after the delete succeeds;
the branch must end either cleanly deleted (no worktree held it) or restored - never
deleted-with-broken-worktree.

Outcome: FIXED in 146fc55e. Every command after the successful delete (worktree listing,
create-only restore, recreated-ref probe, ref-absence check, config cleanup) runs on
CancellationToken.None; the caller's token governs only the delete. Two tests watched
failing first (both threw OperationCanceledException out of the post-delete worktree
listing): cancel-with-worktree now ends restored with a healthy worktree HEAD;
cancel-without-worktree ends cleanly deleted with the branch config section removed.

## R3-3 (MAJOR) Production startup faulted the first scan unwired - ALREADY FIXED, VERIFY

Finding (inspector, and independently the live slot-5 Director): the provider was wired in
MainWindow_Loaded, which fires after layout, while ShowMainWindow starts the first rescan
synchronously - the scan lost the race, threw the refuse-to-scan error, and the failure
died in the unobserved-task finalizer; the model stayed provisional forever.

Status: the Architect already fixed this in commit e3b9d01a after the live Director exposed
it (wiring moved into the MainWindow constructor, which ShowMainWindow runs to completion
before triggering the scan; the fire-and-forget task now has an OnlyOnFaulted continuation
that logs an ERROR immediately). Live proof recorded: before the fix, slot 5 served all 30
repositories as "verifying" indefinitely with the exception in the log; after it, the live
harness passes 8 of 8 with verified states. The Manager's job here: VERIFY e3b9d01a fully
covers the finding (constructor ordering, observed task) and record the outcome; no code
change unless verification fails. An application-level unit test would require constructing
the full MainWindow in a headless harness; the live-Director proof stands in its place and
is recorded as such (honest gap in the QA report).

Outcome: VERIFIED against the working tree at e3b9d01a and after. Evidence: (1) the
provider is wired in the MainWindow CONSTRUCTOR (MainWindow.axaml.cs, before
BuildNativeMenu) - App.ShowMainWindow runs "new MainWindow()" to completion before it
calls StartRepositoryRescan, so the ordering is structural; (2) StartRepositoryRescan
observes the task with a ContinueWith(OnlyOnFaulted) that logs an ERROR immediately;
(3) InitializeServices deliberately starts no scan (LoadCache only) and the watcher set
installs only on ScanCompleted, which cannot fire before a scan; (4) no other scan or
recompute trigger exists before window construction (the only other RecomputeOneAsync
caller is the user-driven Worktrees view, post-UI). No code change needed.

## R3-4 (MAJOR) Removal tombstones do not survive the computes they protect

Finding: (a) the gone-path recompute stamps absence only when the model already holds the
key - a first-time compute in flight can later publish a repository that a newer check saw
vanish; (b) EvictStaleComputeState drops a removal stamp after one further scan even while
the key's semaphore is held, which is proof a compute is still in flight.

Ruling: absence is ALWAYS stamped, whether or not a model row exists; and eviction never
removes stamp state for a key whose semaphore is currently held. Tests: the first-time-
compute-races-gone case, and the compute-held-across-two-scans case, both ending with the
repository absent.

Outcome: FIXED in 4810f469. The gone path stamps absence unconditionally (row or no row),
and the eviction sweep skips any stamp whose key's semaphore is currently held. Both
interleaving tests watched failing first - each ended with the removed repository
resurrected in the model before the fix, absent after.

## R3-5 (MAJOR) Scan removals are ordered by reconciliation time, not observation time

Finding: reconciliation takes a fresh stamp when it publishes absence, so an old scan's
delayed reconciliation can outrank - and remove - a repository that a newer compute
legitimately created and published after that scan enumerated.

Ruling: a scan's removals publish with the SCAN'S OWN START stamp, captured when the scan
began. Any compute that started after the scan therefore outranks the scan's removals and
survives. (A repository that is genuinely gone will be removed by its own later gone-path
recompute or the next scan.) Test: newer add during an older scan's tail survives the older
scan's reconciliation.

Outcome: FIXED in 6f68fde7. The scan captures its start stamp under the gate at the
cancellation-source swap; reconciliation publishes every removal with that stamp and skips
any key whose recorded publish stamp is newer (that publish came from a compute that began
after the scan). Test watched failing first: a repository created and published while the
older scan was still enumerating was removed by its reconciliation before the fix, and
survives untouched after, while the scan's own results and genuine removals still land.

## R3-6 (MAJOR) Scan lifecycle state is not owned by the active scan

Finding: a superseded scan's finally clears IsScanning while its replacement is still
scanning (letting watcher recomputes bypass deferral), and an externally cancelled scan
returns without draining deferred requests, stranding them until some later scan completes.

Ruling: lifecycle state belongs to the CURRENT scan only: IsScanning is cleared (and
progress reset) only by the scan whose cancellation source is still the monitor's current
one, checked under the gate. Deferred requests are drained on every scan exit path unless
a newer scan has taken ownership - in which case that newer scan is responsible for the
drain, and its completion path must provably reach them. Tests: superseded-scan-does-not-
clear-IsScanning, and cancelled-scan-with-no-successor drains its deferred requests.

Outcome: FIXED in 4b0da3c5. The scan's exit checks ownership under the gate against the
monitor's current cancellation source; only the owning scan clears IsScanning, raises
progress, and drains the deferred requests - in the finally, so every owning exit path
(completed, cancelled, faulted) reaches the drain, and a superseded scan touches nothing.
A non-completed scan still never raises ScanCompleted. Both tests watched failing first:
the superseded scan cleared IsScanning for its live replacement (a watcher recompute then
bypassed deferral), and the cancelled scan stranded its deferred recompute. The
superseded-scan test also proves the NEWER scan's completion path reaches the requests
deferred during the superseded interval.

## R3-7 Test gaps from round 3

Ruling: every ruling above lands with its regression test watched failing first. In
addition: the old-Director /repositories compatibility test must exercise the ACTUAL
endpoint (request in, folded JSON out), not the fold helper alone. Accepted as recorded
gaps (QA report, not blockers): the application-level wiring test (live proof instead,
R3-3), the hub-plus-HTTP tenant-isolation integration matrix, hub-level reconnect
ordering, mixed shared-sequence, malformed-field CLI tests.

Outcome: FIXED in e8c84af3. RepositoriesEndpointServeFoldTests hosts the production
GatewayEndpoints.Map over HTTP with a real PushedRepositoryStore (registered connection,
applied snapshot) and asserts on GET /repositories' served JSON: the pre-fix-Director
shape folds to a zero safe count and "verifying" worktrees; a verified repository serves
as pushed. Teeth proven by temporarily removing the endpoint's fold call - the
old-Director assertion went red (stale safe count of two served) and green again with the
fold restored. Every R3-1 through R3-6 test above was watched failing first. The listed
accepted gaps stay recorded for the QA report, not fixed.

## Status

- [x] Fixes implemented and committed per ruling, with tests: R3-1 44f6895d, R3-2 146fc55e,
      R3-3 VERIFIED (no change), R3-4 4810f469, R3-5 6f68fde7, R3-6 4b0da3c5, R3-7 e8c84af3
- [x] Core + Avalonia + Gateway suites green: Core 3441 passed / 8 skipped / 0 failed,
      Avalonia 290 passed / 0 failed, Gateway 3680 passed / 17 skipped / 0 failed
- [ ] Fourth inspection pass
- [ ] QA report to the owner
