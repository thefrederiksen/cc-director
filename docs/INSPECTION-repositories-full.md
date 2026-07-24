# Inspection: repositories-full

## BLOCKER — Provisional repository rows still expose destructive actions

**File:** `src/CcDirector.Avalonia/Controls/RepositoryListView.axaml.cs:61-70`, `src/CcDirector.Avalonia/Controls/RepositoriesView.axaml.cs:86-94`, `src/CcDirector.Avalonia/Controls/RepositoryDetailView.axaml.cs:176-190`, `src/CcDirector.Avalonia/Controls/ChangesDiffView.axaml.cs:228-240`

**What is wrong:** A warm-start row is dimmed but remains clickable. Opening it immediately attaches the cached path to the detail view and its Changes panel, where stage, commit, and permanent discard actions are enabled from a fresh status read. Branch deletion is also reachable. There is no provisional check in the row click, `OpenDetail`, `RepositoryDetailView.Attach`, or `ChangesDiffView`.

**How verified:** `RepoRow_PointerPressed` invokes `RepoOpenRequested` for every non-empty path without checking `row.Verifying`; `OpenDetail` unconditionally attaches that path; `ShowTab("Changes")` is the default; and `DiscardButton_Click` calls `GitWriteService.DiscardAsync`. The only provisional action guard in the diff is inside `WorktreesView`, so the repeated claim that provisional entries are “never acted on” is false. A cached path that has been repurposed for another repository can therefore receive destructive actions before the monitor verifies its identity.

## BLOCKER — The C2 upstream test can still produce a false “origin gone” verdict

**File:** `src/CcDirector.Core/Git/WorktreeInventoryService.cs:108-116`, `src/CcDirector.Core/Git/GitBranchService.cs:103-112`

**What is wrong:** Both implementations treat any non-empty `branch.<name>.merge` as proof that the same-named branch once existed on `origin`, then ignore the configured merge ref and query `ls-remote origin <local-name>`. A valid local branch can track a differently named upstream ref, or a ref on a remote other than `origin`. In those cases the same-named origin branch is absent even though the configured upstream has not gone, and `originGone` becomes true.

**How verified:** Both hunks reduce the config result to `hadUpstream` and then run `ls-remote --heads origin entry.Branch/name`. Neither reads `branch.<name>.remote`, and neither uses the value of `branch.<name>.merge`. `BranchSafetyEvaluator` accepts `originGone` as a sufficient signal even when `containedInMain` is false, and the worktree evaluator receives the same false-positive signal. Delete-time/reap-time recomputation repeats the identical flawed test, so live re-verification does not close this data-loss path.

## MAJOR — Branch deletion has a force-delete time-of-check/time-of-use gap

**File:** `src/CcDirector.Core/Git/GitBranchService.cs:151-165`

**What is wrong:** `DeleteIfSafeAsync` derives safety through multiple subprocesses, returns to managed code, and later executes `git branch -D`. A concurrent process can advance or retarget the branch after the verdict but before the force-delete command. `-D` does not re-check whether the branch’s current tip is merged.

**How verified:** The method awaits `ListAsync`, tests the returned `SafeToDelete` boolean, and then launches a separate unconditional force-delete command. No object ID from the inspected tip is carried into the destructive operation. The batch path does call `DeleteIfSafeAsync` per branch, but every call retains this gap.

## MAJOR — The reaper can be invoked with a linked-worktree path

**File:** `src/CcDirector.Avalonia/Controls/WorktreesView.axaml.cs:190-214`, `src/CcDirector.Avalonia/Controls/WorktreesView.axaml.cs:294-318`, `src/CcDirector.Core/Git/RepositoryMonitor.cs:212-239`

**What is wrong:** Both refresh and reap fall back from `_repoEntryPath` to the session’s `_repoPath`, which the class explicitly says may be a linked worktree. `RecomputeOneAsync` accepts a `.git` file as sufficient repository identity and stores the result under the supplied path, so refreshing while the owning entry is unresolved can make the worktree path the monitor entry. The same fallback is also reachable if the owning entry disappears while the reap confirmation overlay is open.

**How verified:** `RefreshAsync` selects `_repoEntryPath ?? _repoPath`; `RunReapAsync` repeats that expression and passes it directly to `_reaper.ReapAsync`; and `RecomputeOneAsync` treats either a `.git` directory or `.git` file as a repo without canonicalizing to the primary checkout. The regression test for sessions inside worktrees checks only the rendered count and never captures the actual reaper argument.

## MAJOR — Watcher recomputes discard live-session occupancy

**File:** `src/CcDirector.Core/Git/RepositoryWatcher.cs:165-174`, `src/CcDirector.Core/Git/RepositoryMonitor.cs:212-239`, `src/CcDirector.Core/Git/RepositoryStatusService.cs:84-114`

**What is wrong:** Watcher-triggered recomputation always calls `RecomputeOneAsync(repoPath)` without live sessions. That new status replaces the monitor’s prior status, including `InUseBySession` worktree classifications computed during a UI refresh/full scan. An actively used worktree can consequently be published, recommended, and pushed as safe-to-reap until another session-aware scan happens.

**How verified:** `RepositoryWatcher` has no live-session provider and omits the optional `sessions` argument. `RecomputeOneAsync` passes that null value into `_compute` and overwrites `_byPath[key]`. `RepositoryStatusService` derives the worktree counts and full worktree records from the resulting inventory. The watcher tests inject a lightweight compute delegate that ignores sessions, so they cannot detect this state regression.

## MAJOR — Full scans and watcher recomputes can concurrently overwrite the same repository

**File:** `src/CcDirector.Core/Git/RepositoryMonitor.cs:167-178`, `src/CcDirector.Core/Git/RepositoryMonitor.cs:212-251`, `src/CcDirector.Core/Git/RepositoryWatcher.cs:165-174`

**What is wrong:** `RecomputeOneAsync` is not coordinated with `IsScanning`, the full-scan cancellation source, or any per-repository single-flight guard. Existing watchers remain active during later full rescans, so both paths can run the expensive git computation for one repository concurrently and publish in completion order. An older or session-less result can overwrite a newer result.

**How verified:** The full-scan and single-recompute paths each call `_compute` outside `_gate` and later assign `_byPath[key]` under only a short dictionary lock. The added recompute path never checks scan state or participates in scan cancellation. The tests trigger watcher changes only after the initial scan has completed and contain no in-flight rescan case.

## MAJOR — Worktree size measurement is an unbounded, non-cancellable scan bottleneck

**File:** `src/CcDirector.Core/Git/RepositoryStatusService.cs:84-91`, `src/CcDirector.Core/Git/RepositoryStatusService.cs:126-164`

**What is wrong:** Every cache miss performs a synchronous recursive walk of every file in a worktree, with no file-count limit, byte budget, time budget, or cancellation check. Initial discovery of the stated hundreds of large worktrees can keep the monitor in “verifying” for a prolonged period, and concurrent watcher/full-scan computations can duplicate those walks.

**How verified:** `MeasureWorktreeBytes` directly enumerates `Directory.EnumerateFiles(... RecurseSubdirectories = true)` and calls `FileInfo.Length` for every result. It takes no cancellation token and is invoked serially inside the worktree projection before a `RepositoryStatus` is returned. No size-measurement test exercises a large tree, cancellation, or scan latency.

## MINOR — The process-wide size cache grows forever

**File:** `src/CcDirector.Core/Git/RepositoryStatusService.cs:126-163`

**What is wrong:** The static `ConcurrentDictionary` retains one normalized path entry for every worktree ever measured. Reaped, moved, and deleted worktrees are never evicted, so a long-running Director accumulates stale keys and measurements indefinitely.

**How verified:** The only operations shown are `TryGetValue` and assignment. No removal, capacity bound, expiration, or reconciliation with the current inventory exists. Reparse points are skipped by the enumeration options, but that does not address cache lifetime.

## MAJOR — An old SignalR connection can retake repository-store ownership

**File:** `src/CcDirector.Gateway/Streaming/PushedRepositoryStore.cs:37-52`

**What is wrong:** A push from any connection ID different from the currently stored one always wins. After a new connection replaces an old connection, a late push from the still-draining old connection is “new” relative to the current entry and is accepted, switching ownership back. The two connections can then alternate accepted stale snapshots.

**How verified:** Sequence rejection is applied only when `sameConnection` is true; every different connection overwrites `ConnectionId`, `LastSequence`, and the repositories. The test covers `conn1 -> conn2` but not `conn1 -> conn2 -> conn1`, and the store does not consult the stream registry’s current connection ownership.

## MAJOR — Provisional status is erased from the fleet worktree surface

**File:** `src/CcDirector.ControlApi/RepositoryDtoMapper.cs:58-78`, `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:786-811`, `src/CcDirector.Gateway.Contracts/RepositoryDtos.cs:66-83`, `tools/cc-devthrottle/src/repo_ops.py:86-122`

**What is wrong:** A provisional repository snapshot is accepted and its worktrees are flattened without either filtering them or carrying a provisional bit. The CLI then prints `safe-to-reap` and includes the bytes in “reclaimable” totals exactly as if the verdict were live. Consumers of `/worktrees` cannot distinguish cached safety testimony from verified state.

**How verified:** `RepoStatusDto` has `Provisional`, but `FleetWorktreeDto` does not. Both flattening implementations copy each worktree regardless of `r.Provisional`. The CLI reads only `state` and `sizeBytes`. History explicitly filters provisional rows, demonstrating that the pushed stream can contain them, while the fleet worktree path has no equivalent guard.

## MAJOR — The new Discard action permanently destroys tracked work with one click

**File:** `src/CcDirector.Avalonia/Controls/ChangesDiffView.axaml:70-76`, `src/CcDirector.Avalonia/Controls/ChangesDiffView.axaml.cs:228-250`

**What is wrong:** Selecting an unstaged tracked file exposes “Discard changes,” and clicking it immediately invokes the destructive write service. There is no confirmation, displayed loss summary, approval step, or recovery information.

**How verified:** `DiscardButton_Click` directly delegates to `_write.DiscardAsync` through `WriteActionAsync`; the only preconditions are a non-null repository and selection. The test suite checks rendered diff rows but contains no discard-action test.

## MINOR — Rail navigation hides repository detail without detaching it

**File:** `src/CcDirector.Avalonia/Controls/RepositoriesView.axaml.cs:96-125`, `src/CcDirector.Avalonia/Controls/RepositoryDetailView.axaml.cs:100-125`

**What is wrong:** Clicking Local repositories, Root folders, or Recommendations while detail is open merely sets `DetailPage.IsVisible = false`. The detail remains subscribed to `RepositoryMonitor.Upserted`; if the Worktrees tab was opened, its three monitor subscriptions also remain active. Hidden controls continue receiving and dispatching updates until the back button or a later attach happens.

**How verified:** Only `CloseDetail` calls `DetailPage.Detach`. `ShowPage`, used by all rail buttons, does not. `RepositoryDetailView.Attach` subscribes to `Upserted`, and `WorktreesView.Attach` subscribes to `Upserted`, `Removed`, and `ProgressChanged`.

## MAJOR — Repository history conflates distinct repositories and Directors

**File:** `src/CcDirector.Gateway/Streaming/RepoHistoryStore.cs:8-21`, `src/CcDirector.Gateway/Streaming/RepoHistoryStore.cs:61-93`, `src/CcDirector.Gateway/Streaming/RepoHistoryStore.cs:145`

**What is wrong:** The persistence key is tenant/date/machine/repository **name** only. It omits Director ID, repository path, provider, and remote URL. Two distinct repositories with the same leaf name on one machine, or two Directors reporting the same machine/name, overwrite each other, corrupting weekly totals and dirty callouts.

**How verified:** `RepoDailySnapshot` does not store Director ID or path even though the incoming DTO contains both, and `Key` concatenates only `Tenant`, `Date`, `MachineName`, and `Name`. The tenant partition itself is sourced from the bound hub connection and filtered on reads, but the tests use unique names and do not exercise an identity collision.

## NOTE — The principal safety and compatibility claims are not exercised by the added tests

**File:** `src/CcDirector.Avalonia.Tests/OneBrainRegressionTests.cs:12-19`, `src/CcDirector.Avalonia.Tests/RepositoryDetailViewTests.cs:126-152`, `src/CcDirector.Core.Tests/GitBranchServiceTests.cs:55-201`, `src/CcDirector.Core.Tests/RepositoryWatcherTests.cs:20-164`, `src/CcDirector.Gateway.Tests/PushedRepositoryStoreTests.cs:1-93`, `src/CcDirector.Gateway.Tests/RepoHistoryStoreTests.cs:1-108`

**What is wrong:** The comments claim more coverage than the tests provide. The unexercised top claims are: provisional entries cannot be acted on; reaping from a session in a linked worktree passes the owning primary repository path; the branch service rejects a never-pushed or differently named upstream under C2; real repository recomputation cannot feed watcher signals back into itself; watcher activity during a full scan is serialized safely; monitor events and detach paths are UI-thread/lifetime safe; size walks are bounded, cancellable, reparse-safe, and cache-stable; tenant isolation holds through the actual hub plus all three HTTP endpoints rather than only direct store calls; a shared repo/session sequence remains acceptable to `PushedSessionStore`; and CLI rendering tolerates missing or malformed fields.

**How verified:** The one-brain tests assert counts only and contain no provisional or reap invocation despite their class comment. The detail render tests end at `Assert.NotNull`. The C2 regression exists only for `WorktreeInventoryService`. Watcher tests replace the real git computation with a trivial delegate and run after scanning. Gateway tests call stores directly, with no bound connection/request endpoint tests. No test in the diff interleaves repository and session pushes, measures a real large worktree, exercises navigation detach, or calls either destructive UI action.

Verdict: BLOCK — multiple live-reverification, provisional-state, watcher-consistency, and persistence defects can misclassify or destroy repository work.
