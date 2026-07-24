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

    private static RepositoryMonitor MonitorOver(IReadOnlyList<string> paths, Action? perCompute = null)
        => new(
            enumerate: _ => paths,
            compute: (p, _, _) => { perCompute?.Invoke(); return Task.FromResult(Status(p)); });

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
            compute: (p, _, _) => Task.FromResult(Status(p)));

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
                cachePath: cachePath);
            await m1.RescanAsync(new[] { "/r" });
            Assert.True(File.Exists(cachePath));

            // Second monitor (next launch): warm-start shows all three BEFORE any scan.
            var m2 = new RepositoryMonitor(
                enumerate: _ => new[] { "/r/a", "/r/b" }, // "c" is gone now
                compute: (p, _, _) => Task.FromResult(Status(p)),
                cachePath: cachePath);
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
            }));

        await monitor.RescanAsync(new[] { "/r" });
        Assert.True(monitor.Snapshot()[0].IsClean);

        dirty = true;
        await monitor.RescanAsync(new[] { "/r" });

        var only = Assert.Single(monitor.Snapshot());
        Assert.False(only.IsClean);
        Assert.Equal(5, only.UncommittedCount);
    }
}
