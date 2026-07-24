# Independent inspection - round 5 - repositories worktree cleanup safety (issue 516)

You are an independent, adversarial code inspector. Your job is to try to BREAK the safety of the
worktree reaper / branch cleanup feature, not to praise it. Assume the previous author is
overconfident. A "looks fine" is worthless; a concrete failing scenario is everything.

## Where the work is
- Worktree (already checked out, build here): `D:\ReposFred\devthrottle-repo-cleanup-516`
- Branch: `fix/repositories-cleanup-safety-516`
- Base to diff against: `origin/main` (merge base 9fde3414).
- Read the full change set with: `git -C D:\ReposFred\devthrottle-repo-cleanup-516 diff origin/main...HEAD`

## Context - what shipped and what four prior rounds already fixed
This branch fixes an independent post-merge review of the git-worktree reaper (deletes clean +
merged worktrees) and branch cleanup. Rounds 1-3 fixed: fail-closed live-session roster (a roster
that cannot be confirmed aborts the reap), fetch-origin-by-name before trusting merge signals,
per-worktree roster re-read inside the destructive loop, mid-loop outcome preservation, subdirectory
session protection, version-skew envelope fail-closed, and the F02 branch-delete policy split.

Round 4 fixed THREE findings - THESE are your primary targets, verify each did NOT regress and
cannot be bypassed:

1. **Session reservation lease** (`src/CcDirector.Core/Git/WorktreeReservationStore.cs`,
   wired in `WorktreeReaperService.cs` and `Sessions/SessionManager.cs`). A session writes a
   machine-local reservation on its working directory AT LAUNCH (owner Director pid + start time).
   The reaper reads live reservations and refuses to remove a worktree a live reservation covers
   (including a session in a subdirectory). Stale reservations (owning Director gone) are pruned.
   The claim: this closes the check-to-remove race the Gateway roster alone cannot, because git
   deletes tracked files and DEREGISTERS a worktree before Windows refuses the final root delete -
   so an OS handle does NOT protect the contents. TRY TO BREAK THIS: a launch/reap ordering where a
   session's worktree is still removed; a reservation that is not written before the worktree is
   reapable; a stale-prune that wrongly drops a LIVE reservation (pid reuse, start-time skew); a
   path-normalization or subdirectory mismatch that lets a covered worktree through.

2. **Session roster connection epochs** (`src/CcDirector.Gateway/Streaming/PushedSessionStore.cs`).
   The session roster is a destructive authority, so a superseded Director connection re-sending
   Hello must not reclaim ownership and push a stale session set that omits a live session. Epoch
   discipline mirrors PushedRepositoryStore. TRY TO BREAK THIS: any interleaving of
   RegisterConnection / ApplySnapshot / UnregisterConnection across two connections for one Director
   id where a stale snapshot becomes the served roster while the Director still reads fresh/Online.

3. **Leftover retry** (`WorktreeLeftoverStore.cs`, retry pass in `WorktreeReaperService.cs`). A
   locked-file leftover git already deregistered is persisted and retried on later reaps. TRY TO
   BREAK THIS: a retry that deletes a folder it should not (a folder a live session re-entered; a
   path that no longer means what it did); a leak that still occurs; a crash/abort that loses the
   record or double-frees.

## Also re-verify (do not assume prior rounds hold)
The whole diff, especially: `WorktreeReaperService.ReapAsync` fail-closed ordering, `GitBranchService`
branch-delete safety, `ProcessRunner` concurrent drain / kill-on-cancel, `RepoHistoryStore` atomic
write + recovery, `GatewayClient` version-skew throw, `MainWindow` authoritative roster.

## Rules
- No fallback programming; fixes must FAIL CLOSED (when unsure, protect / do nothing destructive).
- Every finding MUST be a concrete failing scenario: exact inputs/state -> exact wrong outcome, with
  file:line. Rank BLOCKER / MAJOR / MINOR.
- If you cannot find a concrete break, say PASS. Do not invent style nits.

## Output
Write your verdict to `docs/reviews/codex-inspection-round5.md` in this worktree. First line MUST be
exactly `PASS` or `FAIL`. Then the numbered findings (or "Verified closed" notes). Do not edit any
other file. Do not commit.
