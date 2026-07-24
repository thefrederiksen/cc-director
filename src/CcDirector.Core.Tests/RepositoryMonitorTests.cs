using System.Collections.Concurrent;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

public class RepositoryMonitorTests
{
    private static RepositoryStatus Status(string path) => new()
    {
        Path = path,
        Name = System.IO.Path.GetFileName(path),
        Provider = RepoProvider.GitHub,
        Branch = "main",
        IsClean = true,
        Success = true,
    };

    /// <summary>An explicit empty-session source: the monitor refuses to scan unwired (R2-8).</summary>
    private static Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>> NoSessions
        => _ => Task.FromResult<IReadOnlyList<LiveSessionRef>>(Array.Empty<LiveSessionRef>());

    private static RepositoryMonitor MonitorOver(IReadOnlyList<string> paths, Action? perCompute = null)
        => new(
            enumerate: _ => paths,
            compute: (p, _, _) => { perCompute?.Invoke(); return Task.FromResult(Status(p)); })
        { LiveSessionsProvider = NoSessions };

    [Fact]
    public async Task Rescan_StreamsEachRepository_AndBuildsModel()
    {
        var monitor = MonitorOver(new[] { "/r/a", "/r/b", "/r/c" });
        var upserts = new List<string>();
        monitor.Upserted += s => upserts.Add(s.Name);

        await monitor.RescanAsync(new[] { "/r" });

        Assert.Equal(new[] { "a", "b", "c" }, upserts);          // streamed one at a time, in order
        Assert.Equal(3, monitor.Snapshot().Count);
        Assert.False(monitor.IsScanning);
        Assert.Equal(3, monitor.ScanDone);
        Assert.Equal(3, monitor.ScanTotal);
    }

    [Fact]
    public async Task Rescan_ReportsProgress_AndCompletes()
    {
        var monitor = MonitorOver(new[] { "/r/a", "/r/b" });
        var progressDone = new List<int>();
        bool completed = false;
        monitor.ProgressChanged += () => progressDone.Add(monitor.ScanDone);
        monitor.ScanCompleted += () => completed = true;

        await monitor.RescanAsync(new[] { "/r" });

        Assert.Contains(1, progressDone);
        Assert.Contains(2, progressDone);
        Assert.True(completed);
    }

    [Fact]
    public async Task Rescan_Again_RemovesRepositoriesNoLongerFound()
    {
        var first = new[] { "/r/a", "/r/b", "/r/c" };
        var paths = new List<string>(first);
        var monitor = new RepositoryMonitor(
            enumerate: _ => paths.ToList(),
            compute: (p, _, _) => Task.FromResult(Status(p)))
        { LiveSessionsProvider = NoSessions };

        await monitor.RescanAsync(new[] { "/r" });
        Assert.Equal(3, monitor.Snapshot().Count);

        // "c" is gone on the second scan.
        paths.Remove("/r/c");
        var removed = new List<string>();
        monitor.Removed += s => removed.Add(s.Name);

        await monitor.RescanAsync(new[] { "/r" });

        Assert.Equal(new[] { "c" }, removed);
        Assert.Equal(2, monitor.Snapshot().Count);
        Assert.DoesNotContain(monitor.Snapshot(), s => s.Name == "c");
    }

    [Fact]
    public async Task Cache_WarmStart_ShowsLastRunInstantly_ThenReconciles()
    {
        var cachePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-monitor-cache-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // First monitor: scan finds a, b, c and persists the cache.
            var m1 = new RepositoryMonitor(
                enumerate: _ => new[] { "/r/a", "/r/b", "/r/c" },
                compute: (p, _, _) => Task.FromResult(Status(p)),
                cachePath: cachePath) { LiveSessionsProvider = NoSessions };
            await m1.RescanAsync(new[] { "/r" });
            Assert.True(File.Exists(cachePath));

            // Second monitor (next launch): warm-start shows all three BEFORE any scan.
            var m2 = new RepositoryMonitor(
                enumerate: _ => new[] { "/r/a", "/r/b" }, // "c" is gone now
                compute: (p, _, _) => Task.FromResult(Status(p)),
                cachePath: cachePath) { LiveSessionsProvider = NoSessions };
            m2.LoadCache();
            Assert.Equal(3, m2.Snapshot().Count); // instant content, no scan yet

            // The scan then reconciles - "c" drops.
            await m2.RescanAsync(new[] { "/r" });
            Assert.Equal(2, m2.Snapshot().Count);
            Assert.DoesNotContain(m2.Snapshot(), s => s.Name == "c");
        }
        finally
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
    }

    // ----- enrichment: provisional + dirty-since (the model-level rules) -----

    [Fact]
    public void Enrich_FreshScan_ClearsProvisional()
    {
        var fresh = Status("/r/a") with { Provisional = true };
        Assert.False(RepositoryMonitor.Enrich(fresh, null).Provisional);
    }

    [Fact]
    public void Enrich_TreeJustTurnedDirty_StampsDirtySinceNow()
    {
        var fresh = Status("/r/a") with { IsClean = false, UncommittedCount = 2 };
        var prevClean = Status("/r/a");

        var enriched = RepositoryMonitor.Enrich(fresh, prevClean);

        Assert.NotNull(enriched.DirtySinceUtc);
        Assert.True((DateTime.UtcNow - enriched.DirtySinceUtc!.Value).TotalMinutes < 1);
    }

    [Fact]
    public void Enrich_StillDirty_CarriesDirtySinceForward()
    {
        var origin = new DateTime(2026, 07, 01, 12, 0, 0, DateTimeKind.Utc);
        var fresh = Status("/r/a") with { IsClean = false, UncommittedCount = 5 };
        var prevDirty = Status("/r/a") with { IsClean = false, UncommittedCount = 2, DirtySinceUtc = origin };

        Assert.Equal(origin, RepositoryMonitor.Enrich(fresh, prevDirty).DirtySinceUtc);
    }

    [Fact]
    public void Enrich_BackToClean_ClearsDirtySince()
    {
        var fresh = Status("/r/a"); // clean
        var prevDirty = Status("/r/a") with { IsClean = false, DirtySinceUtc = DateTime.UtcNow.AddDays(-3) };

        Assert.Null(RepositoryMonitor.Enrich(fresh, prevDirty).DirtySinceUtc);
    }

    [Fact]
    public async Task LoadCache_MarksEntriesProvisional_AndScanClearsIt()
    {
        var cachePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-prov-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var m1 = new RepositoryMonitor(_ => new[] { "/r/a" }, (p, _, _) => Task.FromResult(Status(p)), cachePath) { LiveSessionsProvider = NoSessions };
            await m1.RescanAsync(new[] { "/r" });

            var m2 = new RepositoryMonitor(_ => new[] { "/r/a" }, (p, _, _) => Task.FromResult(Status(p)), cachePath) { LiveSessionsProvider = NoSessions };
            m2.LoadCache();
            Assert.True(Assert.Single(m2.Snapshot()).Provisional); // cached = verifying, never acted on

            await m2.RescanAsync(new[] { "/r" });
            Assert.False(Assert.Single(m2.Snapshot()).Provisional); // live scan confirmed it
        }
        finally
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
    }

    [Fact]
    public async Task RecomputeOne_NonRepoFolder_RemovesTheEntry()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-notrepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir); // exists, but has no .git
        try
        {
            var monitor = new RepositoryMonitor(_ => new[] { dir }, (p, _, _) => Task.FromResult(Status(p)))
            { LiveSessionsProvider = NoSessions };
            await monitor.RescanAsync(new[] { "/r" });
            Assert.Single(monitor.Snapshot());

            var removed = new List<string>();
            monitor.Removed += s => removed.Add(s.Path);
            await monitor.RecomputeOneAsync(dir);

            Assert.Empty(monitor.Snapshot());
            Assert.Single(removed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F4): a linked-worktree path (whose .git is a FILE) handed
    // to RecomputeOneAsync must recompute the PRIMARY repository entry - never store the
    // worktree path as a repository entry of its own. Uses real git so the canonicalization
    // (rev-parse --git-common-dir) is the production one.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task RecomputeOne_LinkedWorktreePath_RecomputesThePrimaryEntry()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-canon-" + Guid.NewGuid().ToString("N"));
        var primary = System.IO.Path.Combine(root, "primary");
        var wt = System.IO.Path.Combine(root, "wt-linked");
        Directory.CreateDirectory(primary);
        try
        {
            RunGit(primary, "-c", "init.defaultBranch=main", "init");
            RunGit(primary, "config", "user.email", "test@cc-director.local");
            RunGit(primary, "config", "user.name", "CC Director Test");
            RunGit(primary, "config", "commit.gpgsign", "false");
            File.WriteAllText(System.IO.Path.Combine(primary, "README.md"), "init\n");
            RunGit(primary, "add", "-A");
            RunGit(primary, "commit", "-m", "initial");
            RunGit(primary, "worktree", "add", wt);

            var computedPaths = new List<string>();
            var monitor = new RepositoryMonitor(
                enumerate: _ => new[] { primary },
                compute: (p, _, _) =>
                {
                    lock (computedPaths) computedPaths.Add(p);
                    return Task.FromResult(Status(p));
                }) { LiveSessionsProvider = NoSessions };
            await monitor.RescanAsync(new[] { root });

            await monitor.RecomputeOneAsync(wt); // the watcher hands over a linked-worktree path

            lock (computedPaths)
                Assert.All(computedPaths, p => Assert.Equal(
                    System.IO.Path.GetFullPath(primary), System.IO.Path.GetFullPath(p)));
            var entry = Assert.Single(monitor.Snapshot());
            Assert.Equal(System.IO.Path.GetFullPath(primary), System.IO.Path.GetFullPath(entry.Path));
        }
        finally
        {
            for (int i = 0; i < 3; i++)
            {
                try { Directory.Delete(root, recursive: true); break; }
                catch { Thread.Sleep(100); }
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F5): every compute consults the monitor's own live-session
    // provider, so a watcher-style recompute (which used to pass no sessions) can no longer
    // erase the in-use-by-session classification.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task RecomputeOne_ConsultsTheLiveSessionsProvider_PreservingInUse()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-sess-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            var monitor = new RepositoryMonitor(
                enumerate: _ => new[] { dir },
                compute: (p, sessions, _) => Task.FromResult(Status(p) with
                {
                    WorktreesInUse = sessions?.Count ?? 0,
                }));
            monitor.LiveSessionsProvider = _ => Task.FromResult<IReadOnlyList<LiveSessionRef>>(
                new[] { new LiveSessionRef { RepoPath = dir, Label = "Busy (#7)" } });

            await monitor.RescanAsync(new[] { "/r" });
            Assert.Equal(1, Assert.Single(monitor.Snapshot()).WorktreesInUse);

            // The watcher's path: no sessions argument exists any more - the provider is the source.
            await monitor.RecomputeOneAsync(dir);
            Assert.Equal(1, Assert.Single(monitor.Snapshot()).WorktreesInUse);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F6): a recompute requested while a full scan runs is
    // deferred until the scan completes, then runs - so the model ends holding the NEWEST
    // result, never the older of the two.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task RecomputeOne_DuringAScan_IsDeferred_AndRunsAfterTheScan()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-defer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            int computeCalls = 0;
            var scanComputeEntered = new TaskCompletionSource();
            var releaseScanCompute = new TaskCompletionSource();
            var monitor = new RepositoryMonitor(
                enumerate: _ => new[] { dir },
                compute: async (p, _, _) =>
                {
                    int call = Interlocked.Increment(ref computeCalls);
                    if (call == 1)
                    {
                        scanComputeEntered.SetResult();
                        await releaseScanCompute.Task;
                    }
                    return Status(p) with { UncommittedCount = call, IsClean = false };
                }) { LiveSessionsProvider = NoSessions };

            var scanTask = monitor.RescanAsync(new[] { "/r" });
            await scanComputeEntered.Task; // the scan is mid-compute and holds the model

            // The watcher fires now: the recompute must defer, not race the scan.
            await monitor.RecomputeOneAsync(dir);
            Assert.Equal(1, Volatile.Read(ref computeCalls)); // deferred - no second compute yet

            releaseScanCompute.SetResult();
            await scanTask;

            Assert.Equal(2, Volatile.Read(ref computeCalls)); // the deferred recompute ran after the scan
            Assert.Equal(2, Assert.Single(monitor.Snapshot()).UncommittedCount); // newest result wins
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F6): two recomputes for the same repository are single-
    // flight - the slower, OLDER compute can never publish after (and thereby overwrite) the
    // newer one.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task RecomputeOne_RacingRecomputes_NeverLeaveTheOlderResultLast()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            int computeCalls = 0;
            var firstComputeEntered = new TaskCompletionSource();
            var releaseFirstCompute = new TaskCompletionSource();
            var monitor = new RepositoryMonitor(
                enumerate: _ => new[] { dir },
                compute: async (p, _, _) =>
                {
                    int call = Interlocked.Increment(ref computeCalls);
                    if (call == 1)
                    {
                        firstComputeEntered.SetResult();
                        await releaseFirstCompute.Task; // the OLD compute is slow
                    }
                    return Status(p) with { UncommittedCount = call, IsClean = false };
                }) { LiveSessionsProvider = NoSessions };

            var oldRecompute = monitor.RecomputeOneAsync(dir);
            await firstComputeEntered.Task;
            var newRecompute = monitor.RecomputeOneAsync(dir); // must WAIT for the old one

            releaseFirstCompute.SetResult();
            await Task.WhenAll(oldRecompute, newRecompute);

            // Single-flight means the second compute ran after the first published, so the model
            // holds the newest result. Before the fix the fast second call published first and the
            // slow OLD result landed last.
            Assert.Equal(2, Assert.Single(monitor.Snapshot()).UncommittedCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-5, boundary ordering 1): a single-repository
    // recompute that started BEFORE a newer scan can only publish AFTER that scan removed the
    // repository from the model. Newest compute wins at the publish: the older recompute's
    // late publish is dropped - it must not resurrect the removed repository.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task RecomputeOne_StartedBeforeAScanThatRemovedTheRepository_CannotResurrectIt()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            var paths = new List<string> { dir };
            var recomputeEntered = new TaskCompletionSource();
            var releaseRecompute = new TaskCompletionSource();
            bool blockNextCompute = false;
            var monitor = new RepositoryMonitor(
                enumerate: _ => paths.ToList(),
                compute: async (p, _, _) =>
                {
                    if (blockNextCompute)
                    {
                        blockNextCompute = false;
                        recomputeEntered.SetResult();
                        await releaseRecompute.Task; // the OLD recompute is slow
                    }
                    return Status(p);
                }) { LiveSessionsProvider = NoSessions };

            await monitor.RescanAsync(new[] { "/r" }); // the model holds the repository
            Assert.Single(monitor.Snapshot());

            // The old recompute starts (it passed the IsScanning check - no scan is running yet)
            // and blocks inside its compute.
            blockNextCompute = true;
            var oldRecompute = monitor.RecomputeOneAsync(dir);
            await recomputeEntered.Task;

            // A NEWER scan runs with roots that no longer contain the repository and removes it.
            paths.Clear();
            await monitor.RescanAsync(new[] { "/r" });
            Assert.Empty(monitor.Snapshot());

            // The old recompute finally returns and tries to publish - it must be dropped.
            releaseRecompute.SetResult();
            await oldRecompute;

            Assert.Empty(monitor.Snapshot()); // not resurrected - the newer removal stands
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-2): a CANCELLED scan never publishes.
    // Cancellation can land in the narrow interval after a compute returns and before its
    // publish; re-checking only at the next loop iteration is too late. The token is
    // re-checked under the gate at the publish itself, so the cancelled scan's result never
    // reaches the model.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Rescan_CancelledAfterComputeButBeforePublish_NeverPublishes()
    {
        var computeEntered = new TaskCompletionSource();
        var releaseCompute = new TaskCompletionSource();
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { "/r/x" },
            compute: async (p, _, _) =>
            {
                computeEntered.SetResult();
                await releaseCompute.Task; // hold the compute so cancellation lands first
                return Status(p);
            }) { LiveSessionsProvider = NoSessions };

        using var cts = new CancellationTokenSource();
        var scanTask = monitor.RescanAsync(new[] { "/r" }, cts.Token);
        await computeEntered.Task;

        // The cancellation arrives while the compute is in flight; the compute itself does not
        // observe the token and returns a result anyway - the publish must still be suppressed.
        cts.Cancel();
        releaseCompute.SetResult();
        await scanTask; // a cancelled scan returns quietly - the newer owner has the model

        Assert.Empty(monitor.Snapshot()); // the cancelled scan published nothing
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-2): a SUPERSEDED scan whose compute returns
    // after the newer scan removed the repository must not republish it - even though the
    // superseded scan is no longer the owner of the model.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Rescan_Superseded_CannotRepublishARepositoryTheNewerScanRemoved()
    {
        var paths = new List<string> { "/r/x" };
        var oldComputeEntered = new TaskCompletionSource();
        var releaseOldCompute = new TaskCompletionSource();
        bool blockNextCompute = false;
        var monitor = new RepositoryMonitor(
            enumerate: _ => paths.ToList(),
            compute: async (p, _, _) =>
            {
                if (blockNextCompute)
                {
                    blockNextCompute = false;
                    oldComputeEntered.SetResult();
                    await releaseOldCompute.Task;
                }
                return Status(p);
            }) { LiveSessionsProvider = NoSessions };

        await monitor.RescanAsync(new[] { "/r" }); // the model holds /r/x
        Assert.Single(monitor.Snapshot());

        // The OLD scan blocks mid-compute for /r/x.
        blockNextCompute = true;
        var oldScan = monitor.RescanAsync(new[] { "/r" });
        await oldComputeEntered.Task;

        // The NEW scan supersedes it; its roots no longer contain /r/x, so it removes it.
        paths.Clear();
        await monitor.RescanAsync(new[] { "/r" });
        Assert.Empty(monitor.Snapshot());

        // The old scan's compute returns after cancellation - its publish must be suppressed.
        releaseOldCompute.SetResult();
        await oldScan;

        Assert.Empty(monitor.Snapshot()); // not resurrected
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-5, boundary ordering 2 / deferral semantics):
    // a recompute deferred during a scan keeps its ORIGINAL requester's token. When that
    // requester cancels before the scan completes, the drain SKIPS the request instead of
    // running it under the scan's token.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task RecomputeOne_DeferredThenCancelledByItsRequester_IsSkippedAtDrain()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-defertok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ".git"));
        try
        {
            int computeCalls = 0;
            var scanComputeEntered = new TaskCompletionSource();
            var releaseScanCompute = new TaskCompletionSource();
            var monitor = new RepositoryMonitor(
                enumerate: _ => new[] { dir },
                compute: async (p, _, _) =>
                {
                    int call = Interlocked.Increment(ref computeCalls);
                    if (call == 1)
                    {
                        scanComputeEntered.SetResult();
                        await releaseScanCompute.Task;
                    }
                    return Status(p);
                }) { LiveSessionsProvider = NoSessions };

            var scanTask = monitor.RescanAsync(new[] { "/r" });
            await scanComputeEntered.Task; // the scan is mid-compute

            // The watcher defers a recompute, then its requester gives up before the scan ends.
            using var requester = new CancellationTokenSource();
            await monitor.RecomputeOneAsync(dir, requester.Token); // deferred - returns at once
            requester.Cancel();

            releaseScanCompute.SetResult();
            await scanTask;

            // The drain must SKIP the cancelled request - only the scan's one compute ran.
            Assert.Equal(1, Volatile.Read(ref computeCalls));
            Assert.Single(monitor.Snapshot()); // the scan's result stands untouched
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-8): scanning without a live-session source is a
    // programming error and fails LOUDLY. An unwired monitor used to silently publish
    // session-blind safety classifications - an occupied worktree could be marked safe to reap.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task Monitor_WithoutALiveSessionsProvider_RefusesToScanOrRecompute()
    {
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { "/r/a" },
            compute: (p, _, _) => Task.FromResult(Status(p)));
        // No LiveSessionsProvider wired.

        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.RescanAsync(new[] { "/r" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.RecomputeOneAsync("/r/a"));
        Assert.Empty(monitor.Snapshot()); // nothing was published session-blind
    }

    private static string RunGit(string workingDir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderr}");
        return stdout;
    }

    [Fact]
    public async Task Rescan_Upsert_UpdatesExistingEntryInPlace()
    {
        var dirty = false;
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { "/r/a" },
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus
            {
                Path = p,
                Name = "a",
                IsClean = !dirty,
                UncommittedCount = dirty ? 5 : 0,
                Success = true,
            })) { LiveSessionsProvider = NoSessions };

        await monitor.RescanAsync(new[] { "/r" });
        Assert.True(monitor.Snapshot()[0].IsClean);

        dirty = true;
        await monitor.RescanAsync(new[] { "/r" });

        var only = Assert.Single(monitor.Snapshot());
        Assert.False(only.IsClean);
        Assert.Equal(5, only.UncommittedCount);
    }
}
