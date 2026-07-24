using System.Diagnostics;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

public class RepositoryWatcherSignalTests
{
    [Theory]
    [InlineData("HEAD", true)]
    [InlineData("packed-refs", true)]
    [InlineData(@"refs\heads\main", true)]
    [InlineData("refs/heads/feature/x", true)]
    [InlineData(@"logs\HEAD", true)]
    [InlineData(@"worktrees\wt1\HEAD", true)]
    [InlineData("index", false)]                    // our own status scans touch this - echo risk
    [InlineData(@"objects\ab\cdef0123", false)]     // object writes are covered by the reflog
    [InlineData(@"logs\refs\heads\main", false)]
    [InlineData("FETCH_HEAD", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSignal_FiltersToGitStateSignals(string? relative, bool expected)
        => Assert.Equal(expected, RepositoryWatcher.IsSignal(relative));
}

/// <summary>
/// Real-filesystem watcher tests: a change under one repository recomputes only that repository,
/// and a burst of changes collapses into one recompute.
/// </summary>
public sealed class RepositoryWatcherIntegrationTests : IDisposable
{
    private readonly string _root;

    public RepositoryWatcherIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        for (int i = 0; i < 3; i++)
        {
            try { Directory.Delete(_root, recursive: true); return; }
            catch { Thread.Sleep(100); }
        }
    }

    private string MakeRepo(string name)
    {
        var repo = Path.Combine(_root, name);
        Directory.CreateDirectory(repo);
        RunGit(repo, "-c", "init.defaultBranch=main", "init");
        RunGit(repo, "config", "user.email", "test@cc-director.local");
        RunGit(repo, "config", "user.name", "CC Director Test");
        RunGit(repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, "README.md"), "init\n");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-m", "initial");
        return repo;
    }

    [Fact]
    public async Task Commit_InOneRepo_RecomputesOnlyThatRepo()
    {
        var a = MakeRepo("alpha");
        var b = MakeRepo("beta");

        // A lightweight compute so the test observes the recompute plumbing, not git speed.
        var computed = new List<string>();
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { a, b },
            compute: (p, _, _) =>
            {
                lock (computed) computed.Add(Path.GetFileName(p));
                return Task.FromResult(new RepositoryStatus { Path = p, Name = Path.GetFileName(p), IsClean = true, Success = true });
            });
        await monitor.RescanAsync(new[] { _root });
        lock (computed) computed.Clear(); // ignore the initial scan

        using var watcher = new RepositoryWatcher(monitor);
        var recomputes = new List<string>();
        watcher.Recomputed += p => { lock (recomputes) recomputes.Add(Path.GetFileName(p)); };
        watcher.SyncWatches(new[] { _root }, new[] { a, b });

        // A commit in alpha updates HEAD/refs/logs under alpha/.git only.
        File.WriteAllText(Path.Combine(a, "change.txt"), "x\n");
        RunGit(a, "add", "-A");
        RunGit(a, "commit", "-m", "change");

        await WaitUntilAsync(() => { lock (recomputes) return recomputes.Count >= 1; }, TimeSpan.FromSeconds(15));

        lock (recomputes)
        {
            Assert.Contains("alpha", recomputes);
            Assert.DoesNotContain("beta", recomputes);
        }
        lock (computed)
        {
            Assert.Contains("alpha", computed);
            Assert.DoesNotContain("beta", computed);
        }
    }

    [Fact]
    public async Task BurstOfRefWrites_CollapsesIntoOneRecompute()
    {
        var a = MakeRepo("gamma");
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { a },
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus { Path = p, Name = Path.GetFileName(p), IsClean = true, Success = true }));
        await monitor.RescanAsync(new[] { _root });

        using var watcher = new RepositoryWatcher(monitor);
        int recomputes = 0;
        watcher.Recomputed += _ => Interlocked.Increment(ref recomputes);
        watcher.SyncWatches(new[] { _root }, new[] { a });

        // Ten rapid ref writes - the debounce must collapse them to one recompute.
        var refsDir = Path.Combine(a, ".git", "refs", "heads");
        for (int i = 0; i < 10; i++)
        {
            File.WriteAllText(Path.Combine(refsDir, $"burst-{i}"), "0123456789012345678901234567890123456789\n");
            await Task.Delay(50);
        }

        await WaitUntilAsync(() => Volatile.Read(ref recomputes) >= 1, TimeSpan.FromSeconds(15));
        // Allow one extra debounce window to elapse, then assert no pile-up followed.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.InRange(Volatile.Read(ref recomputes), 1, 2);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail($"condition not met within {timeout.TotalSeconds:0}s");
    }

    private static string RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderr}");
        return stdout;
    }
}
