# Worktree cleanup - known residuals (follow-up)

The worktree reaper / branch cleanup went through six rounds of independent adversarial inspection
(issue 516). Every concrete data-loss race and every finding through round 5 is fixed. Round 6
surfaced four remaining items that are all **rare transient-failure or abrupt-kill corners**: each
needs several unlikely events to coincide, on a worktree that is simultaneously clean, merged, and
idle past the ten-minute cooling-off. They are recorded here as deliberate follow-ups rather than
blocking the merge, because the current cleanup that ships is broken and this is already a large
safety improvement, and because fully closing them trades meaningful added complexity (and, for the
first item, blocking session launches) against vanishingly small marginal risk.

The reaper's defence is layered: fetch-then-fail-closed roster, ten-minute activity cooling-off, a
machine-wide-locked reservation lease read as late as possible before each removal, junction/alias
canonicalization, and a physical-delete step that respects git's own refusal. Each residual below is
one layer's transient-failure edge, not an unguarded path.

## R1 - a reservation WRITE that fails does not stop the session launch
`WorktreeReservationStore.Reserve` is best-effort and `SessionManager` launches the session even if
the write threw (an access-control or antivirus lock on the reservation directory at that instant).
If the Gateway roster has not yet propagated the new session and a reaper runs in that window, the
worktree is unprotected. Closing it fully means failing the session launch when its reservation
cannot be written - a user-facing behaviour change we chose not to make unilaterally. Mitigation in
place: the reservation directory is under the normal cc-director storage root, and a persistent
failure there is a misconfiguration that would surface in the logs.

## R2 - the Gateway session roster can serve a stale set fresh during a connection changeover
`PushedSessionStore`: when a replacement Director connection becomes active, a delta that arrives
before that connection's first full snapshot can mark the prior connection's incomplete session set
fresh (and cause the new snapshot to be rejected as a stale sequence). The reaper could then read a
roster that omits a live session. This is now backstopped by the reservation lease (the primary
guard); closing the roster path itself means requiring a full snapshot to re-establish freshness for
a new connection epoch.

## R3 - path canonicalization falls back to lexical form on a transient open failure
`WorktreeReaperService.NormalizePath` resolves junction/symlink aliases via `GetFinalPathNameByHandle`
but falls back to the lexical path if the handle cannot be opened at that instant. If one side of a
comparison canonicalizes and the other falls back (a transient access error on an aliased path that
clears before the session's process starts), the two identities may not match. Closing it means
aborting the destructive comparison when either side cannot be canonicalized.

## R4 - leftover persistence is best-effort and not cross-process serialized
`WorktreeLeftoverStore` writes atomically per store instance but two Director slots recording
leftovers concurrently can lose one record to a last-writer-wins overwrite, and `Add` does not
surface a write failure to its caller. A lost record means a locked, git-deregistered folder is never
retried and leaks (safe direction - it is never wrongly deleted, only left behind). Closing it means
cross-process serialization of the leftover store and surfacing write failures.

None of R1-R4 can cause the reaper to delete a worktree that carries a valid, readable live
reservation, or to delete a folder that does not carry the reaper's own leftover marker.
