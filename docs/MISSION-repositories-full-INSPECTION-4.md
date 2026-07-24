# Inspection round 4 - findings and Architect rulings

Mission: repositories-full. Branch: mission/repositories-full (round-3 fixes through efdf9baf).
Inspector: the same independent reviewer, judging the round-3 fix diff. Verdict: BLOCK.

CLOSED this round: R3-1 (create-only restore), R3-3 (startup wiring), R3-4 (tombstone basics),
and the endpoint-level compatibility test. Remaining: the delete's cancellation boundary and
exception safety, and four precise monitor interleavings. Rulings below are binding; the
Manager verifies each first and records the outcome under each item.

Conduct: unchanged - fix and regression test are one commit, plain English, no attribution,
ASCII only, no fallback programming.

## R4-1 (MAJOR) The destructive act and its compensation must be one non-cancellable unit

Finding: the caller's token is passed into the delete command's own process wait, so
cancellation can land after git mutated the ref but before the successful result returns -
and compensation then never runs. Separately, the post-delete commands are sequential
awaits with no surrounding recovery structure: any exception inside compensation escapes
and skips the restore and the config-absence check.

Ruling: the caller's cancellation applies BEFORE the destructive act only. The token is
checked immediately before the delete is issued; the delete command itself and everything
after it run on CancellationToken.None. The whole post-delete phase is wrapped so that ANY
exception inside compensation still attempts the create-only restore before the exception
propagates (restore-on-the-way-out; the restore itself already cannot overwrite a
recreated branch). Tests: cancellation raced into the delete's process wait never skips
compensation; a worktree-listing exception still restores the ref.

Outcome: FIXED in d0998b5e. Verified first: the caller's token was passed into the delete
command and the runner's process wait, and no recovery structure wrapped the compensation.
The token is now checked immediately before the delete; the delete command and every
compensation command run on CancellationToken.None; the whole post-delete phase is wrapped
so any exception inside compensation attempts the create-only restore on the way out, with
the ORIGINAL exception always propagating (the restore attempt is logged, never allowed to
replace it). Both tests watched failing first: cancellation raced into the delete's process
wait escaped as OperationCanceledException with no compensation (new seam
CancelDuringProcessWaitGitRunner runs the child's mutation to completion, then cancels,
then surfaces the wait's cancellation - the window the plain InterleavingGitRunner cannot
reach), and a thrown worktree listing left the ref deleted with a broken worktree HEAD.

## R4-2 (MINOR) A failed ref probe is treated as confirmed absence during config cleanup

Finding: any unsuccessful rev-parse - not only the missing-ref exit - is read as "the ref
is absent", allowing cleanup of a live branch's tracking configuration on a transient
failure.

Ruling: discriminate the exit. "git rev-parse --verify --quiet" exits 1 for a missing ref;
only that exact outcome permits the config cleanup. Any other failure skips cleanup (the
stale section is inert; a wrong cleanup is not). Test: a probe failing with a non-missing
exit leaves the config section alone.

Outcome: FIXED in 69fae0a2. Verified first: any unsuccessful probe fell through to the
cleanup. Only exit 1 now permits it; any other failure logs the probe's exit and leaves the
section alone. Test watched failing first: a probe returning exit 128 (new seam
CannedResultGitRunner) had the live branch's config section removed. The existing exit-1
test still proves cleanup runs on the genuine missing-ref outcome - the other failure
direction stays guarded.

## R4-3 (MAJOR) Scan absence does not tombstone an unpublished in-flight compute

Finding: compute-start stamps live in a local variable until publication, and
reconciliation only visits model rows - a rowless compute older than the scan is invisible
and can publish stale state after reconciliation.

Ruling: in-flight computes are registered: every compute records its start stamp in a
pending map (under the gate) when it begins and clears it when it publishes or abandons.
Scan reconciliation writes its absence tombstone for every key the scan did not see that
has EITHER a model row OR a pending compute. The older pending compute's publish is then
outranked by the tombstone and dropped. Test: the inspector's exact interleaving - rowless
compute starts, scan observes the path absent, compute publishes after reconciliation -
ends with the repository absent.

Outcome: FIXED in 5dcb2374. Verified first: stamps lived in a local variable until
publication and reconciliation visited only model rows. Every compute (scan and single
recompute) now registers its start stamp in a pending map in the same gated region that
hands the stamp out, cleared on publish or abandon in the finally; reconciliation
tombstones every unseen key with a row OR a pending compute; the stamp-eviction sweep also
keeps any stamp whose key has a registered pending compute (direct in-flight proof across
the documented single-flight semaphore crossover). Test watched failing first: the
inspector's exact interleaving ended with the vanished repository resurrected.

## R4-4 (MAJOR) Scan lifecycle: ownership is a stale snapshot and enumeration is unguarded

Finding: (a) the owner check, the IsScanning clear, and the drain are not one atomic
region - a replacement scan can take ownership between them, and because the new scan only
sets IsScanning after enumeration, the old drain can run recomputes concurrently with the
new scan; (b) ScanCompleted is raised later with no ownership re-check; (c) _enumerate
runs before the try/finally, so an enumeration fault while superseded strands IsScanning
and the deferred queue forever.

Ruling: three structural changes. (1) A scan takes ownership, sets IsScanning=true, and
captures its start stamp in ONE gated region at the very start, BEFORE enumeration; from
that point everything runs inside try/finally, so an enumeration fault cleans up. (2) The
finally's owner check, IsScanning clear, and completion decision happen under one gate
acquisition; ScanCompleted and the completion log are raised only when that same gated
decision said owner. (3) Drained deferred recomputes go through the normal deferral check
themselves - if a newer scan has meanwhile started (IsScanning is true again), they
re-defer to that scan instead of running concurrently with it. Tests: the old-drain-during-
new-enumeration case ends with the recompute deferred, not concurrent; an enumeration
fault in a superseded scan leaves IsScanning false and the deferred queue drained by
SOMETHING (the thrower or the successor - prove which).

Outcome: FIXED in 1a1d7325. Verified first: enumeration ran before the try/finally, the
scanning flag was set only after enumeration, the exit's owner check and the drain were
separate gate acquisitions, and ScanCompleted fired with no ownership recheck. All three
structural changes landed: one gated region takes ownership + IsScanning + start stamp
before enumeration with everything after it in try/finally; the exit's owner check, flag
clear, and completion decision are one gate acquisition, with the completion log and
ScanCompleted raised only per that decision; drained deferred recomputes go back through
the deferral check and re-defer to a newer scan. A superseded scan also no longer clobbers
its replacement's progress counters. Both tests watched failing first: the drained deferred
recompute ran concurrently inside the replacement's enumeration, and the enumeration fault
stranded IsScanning true with the deferred request never run. The fault test proves WHICH
scan drains: the faulting successor, which owned the monitor at its exit. The round-3
scan-start-stamp test reached its window through a recompute during enumeration - that
route now correctly defers, so the test was reworked to the still-reachable window (a
recompute parked on the single-flight semaphore before the scan started, stamped after);
its assertions are unchanged.

## R4-5 (MAJOR) Gone-path absence is stamped after its filesystem observation

Finding: the gone path observes the filesystem first and takes its stamp later, inside the
gate - a newer add can start, publish, and then be removed by the older observation, which
received the higher stamp. Same observation-time principle as the scan fix, unapplied here.

Ruling: the gone path takes its stamp BEFORE touching the filesystem. Under the gate it
applies the removal only if no newer publish stamp exists for the key; a newer publish
means the absence observation is stale, and the gone path yields (the newer state stands;
if the repository is truly gone a later observation will remove it). Test: gone
observation racing a newer add leaves the newly added repository in the model.

Outcome: FIXED in 3e366acd. Verified first: the filesystem was observed before the gate
and the stamp taken inside it afterward. The gone path now takes its observation stamp
before touching the filesystem and, under the gate, applies the removal only when no newer
publish stamp exists - otherwise it logs and yields. The filesystem observation moved
behind a new injectable isRepository seam (constructor parameter, default unchanged) so the
test can hold the observation open at its exact point in time. Test watched failing first
against the seam-only intermediate stage with the ordering unfixed: the gone observation
racing a newer add removed the newly added repository from the model.

## R4-6 Test gaps for the above

Ruling: each ruling lands with its regression test watched failing first, using seams that
can actually reach the windows (the inspector noted the existing InterleavingGitRunner
cannot cancel between the child's mutation and the wait's return - build the seam that
can). Anything from the round-4 list not covered by these rulings stays recorded as an
accepted gap in the QA report, not silently dropped.

Outcome: DONE across d0998b5e, 69fae0a2, 5dcb2374, 1a1d7325, 3e366acd - every ruling's
test was watched failing first (R4-5 against the seam-only intermediate stage, since its
seam and fix live in the same file). The four surviving failure modes from the round-4
review's test-gap list are each now covered: the cancellation window inside the delete's
process wait (CancelDuringProcessWaitGitRunner), a thrown compensation command
(ThrowingGitRunner), the ownership-snapshot/drain race (the drain-re-defers test reaches
the replacement mid-enumeration), and the rowless older compute kept alive through
reconciliation.

Accepted gaps, recorded for the QA report and not silently dropped:
- The residual millisecond window between the exit-1 absence probe and the config-section
  removal (a just-recreated branch can lose its tracking config - annoying, never
  destructive to commits). Accepted and documented in code since round 2; the inspector
  noted R4-2 as distinct from this window, and the ruling left it standing.
- Carried from round 3 (unchanged): the application-level startup-wiring test (live
  Director proof stands in its place), the hub-plus-HTTP tenant-isolation integration
  matrix, hub-level reconnect ordering, mixed shared-sequence, and malformed-field CLI
  tests.

## Status

- [x] Fixes implemented and committed per ruling, with tests: R4-1 d0998b5e, R4-2 69fae0a2,
      R4-3 5dcb2374, R4-4 1a1d7325, R4-5 3e366acd, R4-6 covered across those five plus the
      accepted gaps recorded above
- [x] Core + Avalonia + Gateway suites green: Core 3448 passed / 8 skipped / 0 failed,
      Avalonia 290 passed / 0 failed, Gateway 3680 passed / 17 skipped / 0 failed
- [ ] Fifth inspection pass
- [ ] QA report to the owner
