FAIL

1. BLOCKER - The destructive refresh can succeed without refreshing the `origin/main` ref it later trusts (`src/CcDirector.Core/Git/WorktreeReaperService.cs:111`, `src/CcDirector.Core/Git/GitBranchService.cs:183`).

   Exact failing scenario: a repository has both `origin` and `upstream`, and the primary checkout's current branch is configured with `branch.<name>.remote=upstream`. The local `origin/main` still contains commit C, but the real origin has been force-pushed so C is no longer on main. A local feature branch/worktree still points at C. Both destructive paths run `git fetch --prune` without naming a remote; Git therefore fetches the current branch's configured `upstream`, returns success, and leaves `refs/remotes/origin/main` stale. `WorktreeInventoryService` / `GitBranchService.ListAsync` then prove containment against that stale `origin/main`. The reaper removes a worktree that is no longer merged, and branch deletion can delete the last local branch for C; a later real fetch of `origin` drops the stale remote-tracking ref and strands C.

   Why: aborting only when the fetch exits nonzero does not make the particular ref used by the proof fresh. The new regression tests inject a failed fetch in a one-remote repository, so they pass without exercising this successful-wrong-remote case. The reaper failed-fetch and branch stale-fetch findings are therefore not closed.

2. BLOCKER - The live-session roster still has a check/use race (`src/CcDirector.Core/Git/WorktreeReaperService.cs:147-176`).

   Exact failing scenario: the second `liveSessionsProvider` call returns no session for approved worktree W. Immediately after that response, another Director slot starts a session whose working directory is W (or its Gateway delta has not arrived yet). The reaper enters the destructive loop using the frozen `protectedSet`, sees W as safe, and removes it from under the now-live session. With multiple approved worktrees, the window also spans every earlier removal before a later W is reached.

   Why: re-reading the roster after inventory narrows the old window but does not bind session creation and removal atomically; the production session-start path takes no shared path reservation/lease with the reaper. There is no regression test in `WorktreeReaperServiceTests` that starts a session after the second roster response. The roster TOCTOU finding is not actually closed.

3. MAJOR - A readable corrupt live history file suppresses the valid backup and the next save can destroy that backup (`src/CcDirector.Gateway/Streaming/RepoHistoryStore.cs:268-313`).

   Exact failing scenario: `history.jsonl.bak` contains valid rows A/B/C, while the live file has valid A and C lines but B's line is truncated. `File.ReadAllLines` succeeds, `TryLoadFrom` skips B, and then returns `true`, so `Load` never reads the backup. The next accepted snapshot saves the incomplete in-memory set; `File.Replace` also replaces the good backup with the corrupt old live file. B is then permanently lost. An all-corrupt readable live file produces the same outcome for the entire history.

   Why: `TryLoadFrom` reports success based on file readability, not on whether parsing recovered a complete/usable file. The corrupt-middle-line test has no backup and only asserts that rows after the bad line survive, so it does not exercise the destructive backup replacement.

4. MAJOR - A failed sync sub-probe is still published as a successful zero (`src/CcDirector.Core/Git/GitSyncStatusProvider.cs:36-54`, accepted by `src/CcDirector.Core/Git/RepositoryStatusService.cs:85`).

   Exact failing scenario: `git status --branch --porcelain=v2` succeeds for a non-main branch and `origin/main` resolves, but `git rev-list --count HEAD..origin/main` exits nonzero or returns malformed output (for example during a transient object-store failure). `countOutput` is null/unparseable, yet `GetSyncStatusAsync` falls through to the parsed status with `Success=true` and `BehindMainCount=0`. `RepositoryStatusService` accepts it, and `RepositoryMonitor` publishes it as verified fleet/history state.

   Why: the new aggregate guard only works when a provider propagates failure. The regression test uses a non-repository, where the top-level status probe fails, and does not cover a later sync command failing. The failed-probe-as-zero finding is only partially closed.

Verified closed in the real production paths: a git-locked worktree that remains registered is not force-deleted; a missing/throwing roster aborts the reap; empty/all-provisional startup snapshots do not reconcile away verified history; history reconciliation uses the connection-bound Director id; watcher recomputes invalidate the status cache; and a suppressed save failure is retried on the next observation.
