# Independent inspection - round 6 - repositories worktree cleanup safety (issue 516)

You are an independent, adversarial code inspector. Try to BREAK the safety of the worktree reaper /
branch cleanup, not to praise it. A concrete failing scenario is everything; "looks fine" is nothing.

## Where the work is
- Worktree (checked out, build here): `D:\ReposFred\devthrottle-repo-cleanup-516`
- Branch: `fix/repositories-cleanup-safety-516`; base: `origin/main` (merge base 9fde3414).
- Read the change set: `git -C D:\ReposFred\devthrottle-repo-cleanup-516 diff origin/main...HEAD`

## Context - round 5 found 7 issues; THESE are the round-6 targets. Verify each fix holds and cannot be bypassed.
1. **Reservation as a true cross-process lease** (`WorktreeReservationStore.cs`,
   `WorktreeReaperService.cs`, `Sessions/SessionManager.cs`). A session reserves its worktree BEFORE
   its process starts; a machine-wide lock file (`EnterCriticalSection`) serialises the reserve-write
   against the reaper's per-worktree reservation-read + `git worktree remove`; the reaper re-reads
   reservations inside the lock. Claim: no check-to-remove gap. TRY TO BREAK: any interleaving where a
   started session's worktree is still removed; a lock that is not actually held across the removal; a
   deadlock or a lock never released; the reserve happening after the process is observable.
2. **Reservation reads fail closed** (`WorktreeReservationStore.LiveReservedPaths`,
   owner-liveness probe). Gone=prune, Unknown(uninspectable)=keep, reused-pid=prune; enumeration
   failure throws -> reaper aborts. TRY TO BREAK: a path where protection is dropped on uncertainty; a
   live owner wrongly pruned; an enumeration/parse failure read as "no reservations".
3. **Session roster fresh-on-supersede** (`Gateway/Streaming/PushedSessionStore.cs`). A new active
   connection no longer serves the prior connection's roster as fresh (ReceivedAtUtc reset on
   supersede). TRY TO BREAK: any interleave serving a stale/incomplete session set as fresh+Online.
4. **Leftover retry safety** (`WorktreeLeftoverStore.cs`, `RetryPersistedLeftovers`). Repo-scoped;
   skips+drops if git registers the path again; skips if a reservation covers it; records BEFORE the
   physical delete; atomic writes. TRY TO BREAK: a retry that deletes a live worktree or unrelated
   data; a path re-registered but still deleted; a lost record; a leak.
5. **Junction/alias canonicalization** (`WorktreeReaperService.NormalizePath` /
   `GetFinalPathNameByHandle`). Both the git path and the session/reservation path resolve to the real
   path. TRY TO BREAK: an alias form that still bypasses (UNC, nested junctions, a path that fails to
   open so it falls back to lexical while the other side canonicalizes -> mismatch).
6. **Non-cancellable destructive git + cancel preserves outcomes**
   (`WorktreeReaperService.RemoveOneAsync`, the loop). `git worktree remove` and its cleanup use
   CancellationToken.None; cancel between worktrees returns the accumulated outcomes. TRY TO BREAK: a
   cancellation path that still tears a removal or discards a completed delete.

## Also re-verify (do not assume prior rounds hold)
`ReapAsync` fail-closed ordering (fetch -> roster -> per-worktree roster + reservation), `GitBranchService`
branch-delete safety, `ProcessRunner`, `RepoHistoryStore`, `GatewayClient` version-skew throw.

## Rules
- Fail closed; no fallback programming. Every finding = concrete inputs/state -> wrong outcome, with
  file:line, ranked BLOCKER/MAJOR/MINOR. If you cannot find a concrete break, say PASS. No style nits.

## Output
Write your verdict to `docs/reviews/codex-inspection-round6.md`. First line exactly `PASS` or `FAIL`,
then numbered findings (or "Verified closed" notes). Do not edit any other file. Do not commit.
