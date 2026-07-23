using System.Diagnostics;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Integration tests for the reaper against real git repositories (with a local bare origin).
/// Proves it removes exactly the safe set, leaves everything else untouched, honours the
/// live-session guard, and reports - rather than hides - a folder it could not delete.
/// </summary>
public sealed class WorktreeReaperServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _origin;
    private readonly string _primary;

    public WorktreeReaperServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-reaper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _origin = Path.Combine(_root, "origin.git");
        _primary = Path.Combine(_root, "primary");

        RunGit(_root, "-c", "init.defaultBranch=main", "init", "--bare", _origin);
        RunGit(_root, "-c", "init.defaultBranch=main", "clone", _origin, _primary);
        RunGit(_primary, "config", "user.email", "test@cc-director.local");
        RunGit(_primary, "config", "user.name", "CC Director Test");
        RunGit(_primary, "config", "commit.gpgsign", "false");

        WriteFile(_primary, "README.md", "initial\n");
        RunGit(_primary, "add", "-A");
        RunGit(_primary, "commit", "-m", "initial commit");
        RunGit(_primary, "branch", "-M", "main");
        RunGit(_primary, "push", "-u", "origin", "main");
    }

    public void Dispose()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { Directory.Delete(_root, recursive: true); return; }
            catch { Thread.Sleep(100); }
        }
    }

    /// <summary>Adds a clean, contained-in-main (safe) worktree and returns its path.</summary>
    private string AddSafeWorktree(string branch)
    {
        var wt = Path.Combine(_root, "wt-" + branch);
        RunGit(_primary, "worktree", "add", "-b", branch, wt, "main");
        WriteFile(wt, branch + ".txt", "work\n");
        RunGit(wt, "add", "-A");
        RunGit(wt, "commit", "-m", branch + " work");
        RunGit(wt, "push", "-u", "origin", branch);
        RunGit(_primary, "merge", "--ff-only", branch);
        RunGit(_primary, "push", "origin", "main");
        return wt;
    }

    /// <summary>Adds a stranded (unmerged, still-present-on-origin) worktree and returns its path.</summary>
    private string AddStrandedWorktree(string branch)
    {
        var wt = Path.Combine(_root, "wt-" + branch);
        RunGit(_primary, "worktree", "add", "-b", branch, wt, "main");
        WriteFile(wt, branch + ".txt", "unmerged\n");
        RunGit(wt, "add", "-A");
        RunGit(wt, "commit", "-m", branch + " unmerged");
        RunGit(wt, "push", "-u", "origin", branch);
        return wt;
    }

    [Fact]
    public async Task Reap_RemovesTheSafeWorktree_AndLeavesStrandedUntouched()
    {
        var safe = AddSafeWorktree("safe");
        var stranded = AddStrandedWorktree("stranded");

        var result = await new WorktreeReaperService().ReapAsync(_primary);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.RemovedCount);
        Assert.False(Directory.Exists(safe), "safe worktree folder should be gone");
        Assert.True(Directory.Exists(stranded), "stranded worktree must be untouched");

        // The badge clears: a fresh inventory reports zero safe-to-reap.
        var after = await new WorktreeInventoryService().GetInventoryAsync(_primary);
        Assert.Equal(0, after.SafeToReapCount);
    }

    [Fact]
    public async Task Reap_NeverRemovesAWorktreeInUseByALiveSession_EvenWhenSafe()
    {
        var safe = AddSafeWorktree("busy");

        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { safe };
        var result = await new WorktreeReaperService().ReapAsync(_primary, protectedPaths);

        Assert.Equal(0, result.RemovedCount);
        Assert.Contains(safe, result.Skipped);
        Assert.True(Directory.Exists(safe), "a live session's worktree must never be removed");
    }

    [Fact]
    public async Task Reap_ProtectedPathMatchIsCaseAndTrailingSlashInsensitive()
    {
        var safe = AddSafeWorktree("case");

        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            safe.ToUpperInvariant() + Path.DirectorySeparatorChar,
        };
        var result = await new WorktreeReaperService().ReapAsync(_primary, protectedPaths);

        Assert.Equal(0, result.RemovedCount);
        Assert.True(Directory.Exists(safe));
    }

    [Fact]
    public async Task Reap_NeverRemovesThePrimaryCheckout()
    {
        AddSafeWorktree("x");

        await new WorktreeReaperService().ReapAsync(_primary);

        Assert.True(Directory.Exists(_primary), "the primary checkout must always survive");
        Assert.True(Directory.Exists(Path.Combine(_primary, ".git")));
    }

    [Fact]
    public async Task Reap_LockedFolder_ReportsLeftover_DoesNotClaimSuccess()
    {
        var safe = AddSafeWorktree("locked");

        // Simulate a locked build output: an ignored file held open with no delete share.
        WriteFile(safe, ".gitignore", "locked/\n");
        RunGit(safe, "add", ".gitignore");
        RunGit(safe, "commit", "-m", "ignore locked dir");
        RunGit(safe, "push", "origin", "locked");
        RunGit(_primary, "merge", "--ff-only", "locked");
        RunGit(_primary, "push", "origin", "main");

        var lockedDir = Path.Combine(safe, "locked");
        Directory.CreateDirectory(lockedDir);
        var lockedFile = Path.Combine(lockedDir, "held.bin");
        File.WriteAllText(lockedFile, "output\n");

        using (var _ = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await new WorktreeReaperService().ReapAsync(_primary);

            Assert.False(result.Success, "a folder that could not be deleted must not be reported as success");
            Assert.Contains(safe, result.Leftovers);
            Assert.True(Directory.Exists(safe), "the locked folder must still be present and reported");
            var outcome = Assert.Single(result.Outcomes);
            Assert.False(outcome.Removed);
            Assert.Equal(safe, outcome.Leftover);
        }
    }

    [Fact]
    public async Task Reap_EmptyRepo_NoWorktrees_IsANoOpSuccess()
    {
        var result = await new WorktreeReaperService().ReapAsync(_primary);

        Assert.True(result.Success);
        Assert.Equal(0, result.RemovedCount);
        Assert.Empty(result.Leftovers);
    }

    // ----- helpers -----

    private static void WriteFile(string repo, string relPath, string content)
    {
        var full = Path.Combine(repo, relPath);
        var dir = Path.GetDirectoryName(full);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
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
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {p.ExitCode}): {stderr}");
        return stdout;
    }
}
