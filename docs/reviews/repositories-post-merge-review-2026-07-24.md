# Repositories post-merge review — 2026-07-24

Commit reviewed: `c93f9b952107835502cb4e87b0dccf74ef335187` (`origin/main`)

Verdict: **BLOCKER — the shipped cleanup surfaces can lose work.** A deleted remote branch is treated as proof of merge even when its commits are unmerged, and the worktree reaper recursively deletes a directory after `git worktree remove` refuses it.

## Review scope and method

This review read the shipped source directly from a detached worktree at `origin/main`. It traced:

- repository discovery, status computation, monitor ordering, cache behavior, and watcher lifecycle;
- worktree safety and removal, branch safety and deletion, diff/status parsing, pull requests, and history;
- the Repositories list/detail/diff/branches/worktrees/recommendations screens and hand-off flow;
- Director relay and push wiring;
- Gateway hub binding, repository cache, tenant-scoped endpoints, repository history, and DTO folds;
- `cc-devthrottle repo` / `worktree` commands and action discovery;
- the relevant unit/integration tests.

The mission and inspection documents were used only as a map. Findings below are based on executable code and tests.

## Findings

### R1 — BLOCKER — Deleting an upstream ref makes unmerged local commits “safe to delete”

`ConfiguredUpstreamProbe` defines `UpstreamGone` as an empty successful `git ls-remote` result (`src/CcDirector.Core/Git/ConfiguredUpstreamProbe.cs:48-61`). `BranchSafetyEvaluator` treats that fact alone as sufficient proof of merge (`src/CcDirector.Core/Git/GitBranchService.cs:48-53`), and `GitBranchService.ListAsync` passes it through even when `git cherry` reports unique commits (`src/CcDirector.Core/Git/GitBranchService.cs:119-138`).

The shipped regression test proves the unsafe case rather than preventing it. `Branch_WhoseConfiguredUpstreamWasDeleted_IsSafe` creates and commits work, pushes the branch, deletes the remote ref without merging anything, and asserts that the local branch is safe (`src/CcDirector.Core.Tests/GitBranchServiceTests.cs:262-276`). That test passed against `c93f9b95`.

The Repositories UI exposes both a one-click per-branch delete and a one-click batch delete of every such branch (`src/CcDirector.Avalonia/Controls/RepositoryDetailView.axaml.cs:254-281`), with no confirmation. Deletion removes the local ref with `update-ref -d`; once the remote ref is already gone, the commits become reachable only through recovery mechanisms and can later be pruned.

The same false proof feeds `WorktreeSafetyEvaluator`, so a clean worktree on an unmerged branch whose remote ref was manually deleted is also labelled safe to reap (`src/CcDirector.Core/Git/WorktreeInventoryService.cs:108-128`, `src/CcDirector.Core/Git/WorktreeSafetyEvaluator.cs:52-54`).

### R2 — BLOCKER — The reaper recursively deletes a worktree after Git refuses to remove it

`WorktreeReaperService.RemoveOneAsync` checks `git status`, invokes `git worktree remove`, ignores the command result, and, if the directory remains, calls `Directory.Delete(path, recursive: true)` (`src/CcDirector.Core/Git/WorktreeReaperService.cs:132-170`).

There is a destructive TOCTOU window between the status check and the remove. If another process creates or modifies a file in that interval, Git correctly refuses to remove the now-dirty worktree; the code interprets the remaining directory as a locked-file cleanup case and recursively deletes it anyway. The same fallback runs for every other Git refusal, not only the narrow locked-output case described in the comment.

The existing test covers a locked ignored file and verifies that a leftover is reported (`src/CcDirector.Core.Tests/WorktreeReaperServiceTests.cs:131-158`). It does not cover a failed `git worktree remove` caused by newly dirty content, a concurrent writer, submodule state, an administrative error, or cancellation. The locked-file test passed against `c93f9b95`; it confirms that the physical-delete fallback is active.

### R3 — MAJOR — “Clean” worktrees may contain ignored, irreplaceable data that the reaper deletes

Both the inventory and the last-moment recheck use `git status --porcelain` without `--ignored` (`src/CcDirector.Core/Git/WorktreeInventoryService.cs:83-88`, `src/CcDirector.Core/Git/WorktreeReaperService.cs:136-142`). Ignored files therefore do not make a worktree dirty. Local databases, `.env` files, generated-but-costly assets, and other ignored data can be deleted while the UI says the tree has “nothing” to lose.

The shipped locked-folder test explicitly creates an ignored file and expects the reaper to attempt to delete it (`src/CcDirector.Core.Tests/WorktreeReaperServiceTests.cs:135-155`). The file survives only because the test holds a no-delete-share handle. An unlocked ignored file is not protected.

This contradicts the model documentation that `IsClean` means “no content at all” and the recommendation copy that removal “deletes only leftover copies” (`src/CcDirector.Core/Git/WorktreeModels.cs:74-75`, `src/CcDirector.Core/Git/RecommendationEngine.cs:66-68`).

### R4 — MAJOR — Live-session protection fails open when the Gateway cannot supply other Director slots

At act time the UI refreshes live sessions and passes their paths to the reaper, but the reaper deliberately recomputes inventory with `liveSessions: null` and relies entirely on that external protected set (`src/CcDirector.Avalonia/Controls/WorktreesView.axaml.cs:317-325`, `src/CcDirector.Core/Git/WorktreeReaperService.cs:89-105`).

The fleet provider catches a Gateway failure and falls back to only this Director’s `_sessions` collection (`src/CcDirector.Avalonia/MainWindow.axaml.cs:4282-4319`). A session in another Director slot on the same machine is therefore omitted precisely during a Gateway outage. `WorktreesView` also converts any provider failure into an empty set and continues (`src/CcDirector.Avalonia/Controls/WorktreesView.axaml.cs:380-391`).

A clean worktree occupied by another local slot can consequently be removed from underneath that session. If that session writes during the R2 window, its new work can be recursively deleted.

### R5 — MAJOR — The “always-current” monitor never observes ordinary edits, staging, or session occupancy changes

The repository watcher explicitly does not watch working-tree files or `.git/index` (`src/CcDirector.Core/Git/RepositoryWatcher.cs:11-17`, `src/CcDirector.Core/Git/RepositoryWatcher.cs:120-130`). A tracked edit, new untracked file, `git add`, or mixed reset therefore does not recompute repository dirtiness. The only production `RecomputeOneAsync` caller is the watcher; session create/remove events do not trigger a repository recompute, and there is no periodic full-rescan backstop.

As a result:

- `IsClean`, `UncommittedCount`, and `DirtySinceUtc` can remain stale indefinitely;
- a newly started session does not move a worktree from `safe-to-reap` to `in-use`;
- a stopped session does not release an `in-use` worktree;
- the Repositories badge, recommendations, Gateway snapshot, history, and CLI can all disagree with current disk/session state.

The watcher integration tests exercise commits and ref writes only (`src/CcDirector.Core.Tests/RepositoryWatcherTests.cs:67-136`). There is no test for a plain working-tree edit, index-only change, or session lifecycle edge. This is also why the recommendation claim that “nothing is using them” is not enforced by the monitor model (`src/CcDirector.Core/Git/RecommendationEngine.cs:66-68`).

### R6 — MAJOR — Watcher overflow and several ordinary repository-creation paths have no recovery

`CreateGitWatcher` creates a recursive `FileSystemWatcher` over the entire `.git` directory and filters paths only after events reach managed code (`src/CcDirector.Core/Git/RepositoryWatcher.cs:100-117`). Object/index churn still consumes the native watcher buffer. No `Error` handler is registered, so an `InternalBufferOverflowException` produces no rescan, watcher recreation, or degraded-state signal. Lost events leave the model stale until a manual rescan.

The root watcher is non-recursive and listens only for direct child directory create/delete/rename (`src/CcDirector.Core/Git/RepositoryWatcher.cs:81-97`). Running `git init` inside an already-existing child directory does not create a root-level directory event, so the new repository is never discovered. A repository cloned slowly enough that the two-second create debounce fires before `.git` exists has the same failure mode.

Transiently unavailable roots are also treated as authoritative emptiness: `ScanLocalRepos` swallows enumeration errors and returns a partial/empty list (`src/CcDirector.Core/Git/RemoteRepoProvider.cs:86-115`), reconciliation removes unseen rows, and `SyncWatches` drops watchers for non-existing roots. There is no automatic mechanism that notices the root returning.

### R7 — MAJOR — A stale “present” observation can resurrect a repository after a newer removal

`RecomputeOneAsync` stamps and checks presence before acquiring the per-repository semaphore (`src/CcDirector.Core/Git/RepositoryMonitor.cs:438-504`). After it eventually obtains the semaphore, it discards the presence observation’s stamp, allocates a newer compute stamp, and publishes under that newer stamp (`src/CcDirector.Core/Git/RepositoryMonitor.cs:505-520`).

The failing interleaving is:

1. recompute A observes the repository present and waits behind another compute;
2. the directory is deleted;
3. recompute B observes absence and publishes a tombstone;
4. A acquires the semaphore, receives a stamp newer than B, computes the now-missing path, and publishes a failure/stale row over B’s tombstone.

Absence does not take the semaphore, and presence is not rechecked after waiting. The “newest observation wins” comments cover absence ordering but not this inverse race. The resurrected row is non-provisional; `RepositoriesView.OpenDetail` checks only null/provisional, not `Success`, before exposing the stage/commit/discard/branch-delete surface (`src/CcDirector.Avalonia/Controls/RepositoriesView.axaml.cs:94-115`).

### R8 — MAJOR — A background scan performs one unbounded network call per branch-bearing worktree

Although `RepositoryStatusService` says whole-machine scans do not fetch and use last-known remote refs (`src/CcDirector.Core/Git/RepositoryStatusService.cs:5-9`), every non-primary branch worktree calls `ConfiguredUpstreamProbe`, which runs `git ls-remote` against the configured remote (`src/CcDirector.Core/Git/WorktreeInventoryService.cs:108-115`, `src/CcDirector.Core/Git/ConfiguredUpstreamProbe.cs:48-55`). `fetchPrune:false` does not prevent these calls.

The calls are sequential, have no timeout, and do not disable interactive credential prompting. A fleet with many worktrees performs many remote round trips on startup and on recompute. An offline or credential-blocked remote can hold the scan open indefinitely, preventing `ScanCompleted`, watcher synchronization, cache reconciliation, and fresh Gateway pushes.

The production `GhMergedPullRequestProbe` is not wired, so the expensive remote-ref probe is also the only non-patch-equivalence signal the shipped cleanup path actually uses.

### R9 — MAJOR — Cancelling a Git command abandons the child process instead of terminating it

`GitCommandRunner` awaits `Process.WaitForExitAsync(ct)` and disposes only the `Process` wrapper when cancellation throws (`src/CcDirector.Core/Git/GitCommandRunner.cs:53-63`). It never kills or drains the child.

Superseded scans can therefore leave `git ls-remote`, status, or other Git processes running. More seriously, `WorktreeReaperService` passes its caller token into `git worktree remove`: cancellation can make the service return failure while the child continues the destructive operation in the background. The caller cannot tell whether the mutation happened. `PullRequestService.RunCliAsync` has the same process-lifetime pattern for `gh` and `az` (`src/CcDirector.Core/Git/PullRequestService.cs:167-193`).

### R10 — MAJOR — Raw remote URLs, including embedded credentials, are pushed to and served by the Gateway

The monitor reads `git remote get-url origin` verbatim, the Director mapper copies it into `RepoStatusDto.RemoteUrl`, and `/repositories` returns it unchanged (`src/CcDirector.Core/Git/RepositoryStatusService.cs:230-233`, `src/CcDirector.ControlApi/RepositoryDtoMapper.cs:19-43`, `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:768-795`).

Git supports URLs containing user names, PATs, or passwords (`https://user:token@host/...`). This feature moves such a secret from local Git configuration into the hosted Gateway’s memory and every authorized repository-list response. There is no credential stripping or safe-display projection at the new trust boundary.

### R11 — MAJOR — The pushed repository cache is unbounded and never forgets departed Directors

`PushedRepositoryStore` retains a tenant/director `Entry` and its last DTO graph forever. `UnregisterConnection` only nulls `ActiveConnectionId`; there is no `Forget`/remove path (`src/CcDirector.Gateway/Streaming/PushedRepositoryStore.cs:34-73`, `src/CcDirector.Gateway/Streaming/PushedRepositoryStore.cs:118-120`). `GatewayHost` forgets only the roster cache when a Director is removed (`src/CcDirector.Gateway/GatewayHost.cs:693-698`).

`DirectorHub.Hello` accepts any non-empty Director id with no length or cardinality bound (`src/CcDirector.Gateway/Streaming/DirectorHub.cs:120-135`), while the shared hub allows messages up to 32 MB (`src/CcDirector.Gateway.Contracts/DirectorUpStreamMessages.cs:105-129`). An authenticated tenant can repeatedly claim new ids and push large snapshots; stale data stops serving after the age window but remains resident. Legitimate Director churn also leaks entries. In hosted mode this is a cross-tenant resource-isolation problem: one tenant’s allocations consume process memory shared by all tenants.

### R12 — MAJOR — Repository history has unbounded retention, global synchronous write amplification, and non-atomic durability

`RepoHistoryStore` keeps all dates in one process-wide dictionary and one JSONL file; there is no retention or compaction (`src/CcDirector.Gateway/Streaming/RepoHistoryStore.cs:55-66`, `src/CcDirector.Gateway/Streaming/RepoHistoryStore.cs:185-224`). Every accepted repository push enters the one global lock and rewrites the entire file (`src/CcDirector.Gateway/Streaming/RepoHistoryStore.cs:68-110`, `src/CcDirector.Gateway/Streaming/RepoHistoryStore.cs:211-223`). Periodic reseeds and three-second model-change pushes therefore turn growth in historical rows into increasing CPU, allocation, disk I/O, and hub latency for every tenant.

Writes use `File.WriteAllLines` directly on the live file. A crash or failed write can truncate the only copy. Load catches the first malformed-line exception after partially populating `_rows`; the next successful observation rewrites only that partial prefix, permanently discarding valid rows that followed the bad line. Both save and load failures are logged and otherwise treated as success/empty history.

Because `PushRepoSnapshot` hands raw DTOs to history (`src/CcDirector.Gateway/Streaming/DirectorHub.cs:75-83`) and history keys on payload-controlled `DirectorId` and `Path`, one authenticated tenant can add large numbers of unique rows and drive shared disk/lock pressure.

### R13 — MAJOR — The advertised authoritative PR signal is dead in production

Both `WorktreeInventoryService` and `GitBranchService` default to `NullMergedPullRequestProbe` (`src/CcDirector.Core/Git/WorktreeInventoryService.cs:13-20`, `src/CcDirector.Core/Git/GitBranchService.cs:69-76`). There is a `GhMergedPullRequestProbe` implementation, but no production call site constructs or injects it.

Consequently:

- `PullRequestMerged` is always false in worktree and branch safety;
- `HasOpenPullRequest` is always false in worktree rows;
- squash-merge proof does not work through the claimed authoritative path;
- Azure DevOps has no equivalent merged-PR probe at all.

Tests of the pure evaluator and injected seams do not prove production wiring. The dead C1 path forces shipped cleanup to rely on the unsafe upstream-deleted assumption in R1 for squash/deleted-branch cases.

### R14 — MINOR — Daily history retains absent repositories and intentionally double-counts overlapping Directors

`ObserveSnapshot` only upserts rows present in a full snapshot; it never removes today’s rows for the same tenant/director that are absent from the new authoritative snapshot (`src/CcDirector.Gateway/Streaming/RepoHistoryStore.cs:68-110`). A repository removed earlier in the day remains in `DirtyOverThreshold` and weekly totals.

The identity also includes `DirectorId`, so two Director slots on the same machine reporting the same physical path become two daily rows whose worktree/disk/dirty counts are summed. The test explicitly asserts this additive result (`src/CcDirector.Gateway.Tests/RepoHistoryStoreTests.cs:153-179`). Since multiple local Director slots can scan the same registered roots, the “fleet totals” can be multiples of physical reality.

### R15 — MINOR — Repository push debounce and watcher debounce leak lifecycle state

`ControlApiHost.WireRepositoryPush` installs anonymous monitor handlers that cannot be removed, mutates `_repoPushDebounce` without synchronization, and never disposes the final timer in `StopAsync` (`src/CcDirector.ControlApi/ControlApiHost.cs:817-843`, `src/CcDirector.ControlApi/ControlApiHost.cs:1039-1101`). Concurrent monitor events can create multiple timers and lose references to some of them.

`RepositoryWatcher.Schedule` cancels but never disposes superseded or completed `CancellationTokenSource` instances. Its `_disposed` check is outside `_gate`, so `Schedule` can pass the check, wait for `Dispose`, and then insert a new pending debounce after disposal; that recompute can run after shutdown (`src/CcDirector.Core/Git/RepositoryWatcher.cs:133-190`).

### R16 — MINOR — The new CLI commands are absent from agent action discovery and have no shipped tests

`repo list` and `worktree list` are registered Typer commands, but `_ACTIONS` contains no `repo-list` or `worktree-list` entries (`tools/cc-devthrottle/src/cli.py:20-52`, `tools/cc-devthrottle/src/cli.py:590-673`). Agents that use `cc-devthrottle actions --json` as the supported discovery surface do not discover the new repository facts.

No Python tests were added for `repo_ops.py`. DTO casing, error-envelope handling, filters, JSON output, and command registration are therefore unverified.

### R17 — MINOR — Diff actions cannot reliably address all valid Git paths, and failures are invisible in the UI

The diff screen obtains paths from the existing line-based porcelain-v1 parser. That parser trims paths, interprets any literal `" -> "` as a rename, and does not decode Git’s quoted path syntax (`src/CcDirector.Core/Git/GitStatusProvider.cs:193-255`). Valid filenames containing whitespace, quotes, escapes, newlines, or the arrow delimiter can be displayed or passed to stage/unstage/discard incorrectly.

The new destructive discard action uses those parsed paths (`src/CcDirector.Avalonia/Controls/ChangesDiffView.axaml.cs:249-303`). Stage, unstage, discard, and commit failures are only logged; the user is not shown that the requested action failed (`src/CcDirector.Avalonia/Controls/ChangesDiffView.axaml.cs:301-340`). `RefreshAsync` also ignores `GitStatusResult.Success`, so a status failure renders as an empty “No changes” view (`src/CcDirector.Avalonia/Controls/ChangesDiffView.axaml.cs:89-127`).

### R18 — NOTE — Direct tenant keys are used correctly, but feature-specific hosted boundary coverage is incomplete

Direct inspection found no cross-tenant repository read through the new REST paths:

- `/repositories`, `/worktrees`, and `/reports/repositories-weekly` resolve the request tenant and return 403 when hosted binding fails (`src/CcDirector.Gateway/Api/GatewayEndpoints.cs:768-845`);
- `PushedRepositoryStore` is partitioned by `TenantId` before Director id;
- `RepoHistoryStore` stamps the hub-bound tenant and filters reads by it;
- `DirectorHub` derives the connection tenant from the authenticated boundary and never from repository payload data (`src/CcDirector.Gateway/Streaming/DirectorHub.cs:139-170`).

Coverage does not exercise that whole boundary end to end for this feature. Store tests prove direct tenant partitioning, while the repository endpoint tests run the Local/no-boundary configuration. There is no feature-specific hosted two-tenant HTTP test or authenticated hub test proving that a Tenant A connection cannot affect Tenant B repository cache/history. The global memory/disk pressure in R11/R12 remains a tenant-isolation concern even though row reads are keyed correctly.

## Validation performed

- Confirmed the review worktree was detached exactly at `c93f9b952107835502cb4e87b0dccf74ef335187`.
- Inspected the complete squash diff and the shipped source around every acting or persistence path.
- Ran `GitBranchServiceTests.Branch_WhoseConfiguredUpstreamWasDeleted_IsSafe`; it passed, confirming that unmerged work becomes “safe” after only remote-ref deletion.
- Ran `WorktreeReaperServiceTests.Reap_LockedFolder_ReportsLeftover_DoesNotClaimSuccess`; it passed, confirming the physical recursive-delete fallback is exercised for ignored content.
- Attempted broader filtered Core and Gateway suites. They exceeded the review time boxes in this busy checkout and were terminated; no pass/fail conclusion is inferred from those incomplete runs.

No source fixes or commits were made.
