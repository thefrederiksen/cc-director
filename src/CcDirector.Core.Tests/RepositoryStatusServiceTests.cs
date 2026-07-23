using System.Diagnostics;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

public class RepositoryStatusClassifyTests
{
    [Theory]
    [InlineData("https://github.com/foo/bar.git", RepoProvider.GitHub, "foo")]
    [InlineData("git@github.com:foo/bar.git", RepoProvider.GitHub, "foo")]
    [InlineData("https://github.com/thefrederiksen/devthrottle", RepoProvider.GitHub, "thefrederiksen")]
    [InlineData("https://mindzie@dev.azure.com/mindzie/mindzieStudio1/_git/mindzieWeb", RepoProvider.AzureDevOps, "mindzie")]
    [InlineData("git@ssh.dev.azure.com:v3/mindzie/mindzieStudio1/mindzieWeb", RepoProvider.AzureDevOps, "mindzie")]
    [InlineData("https://gitlab.com/foo/bar.git", RepoProvider.Other, null)]
    [InlineData("", RepoProvider.None, null)]
    [InlineData(null, RepoProvider.None, null)]
    public void ClassifyRemote_MapsUrlToProviderAndOrg(string? url, RepoProvider provider, string? org)
    {
        var (p, o) = RepositoryStatusService.ClassifyRemote(url);
        Assert.Equal(provider, p);
        Assert.Equal(org, o);
    }
}

/// <summary>
/// Integration tests over real repositories: a repo's status folds in its remote provider, its
/// uncommitted count, and its worktree summary.
/// </summary>
public sealed class RepositoryStatusServiceTests : IDisposable
{
    private readonly string _root;

    public RepositoryStatusServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-repostatus-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task GetStatusAsync_CleanRepo_WithSafeWorktree_SummarisesWorktrees()
    {
        var (repo, _) = MakeRepoWithBareOrigin("app");

        // A merged, clean linked worktree -> safe to reap.
        var wt = Path.Combine(_root, "app-safe");
        RunGit(repo, "worktree", "add", "-b", "done", wt, "main");
        WriteFile(wt, "x.txt", "x\n");
        RunGit(wt, "add", "-A");
        RunGit(wt, "commit", "-m", "done work");
        RunGit(wt, "push", "-u", "origin", "done");
        RunGit(repo, "merge", "--ff-only", "done");
        RunGit(repo, "push", "origin", "main");

        var status = await new RepositoryStatusService().GetStatusAsync(repo, fetchPrune: false);

        Assert.True(status.Success, status.Error);
        Assert.Equal("main", status.Branch);
        Assert.True(status.IsClean);
        Assert.Equal(0, status.UncommittedCount);
        Assert.Equal(1, status.WorktreeCount);
        Assert.Equal(1, status.WorktreesSafeToReap);
    }

    [Fact]
    public async Task GetStatusAsync_GitHubRemote_DirtyTree_ReportsProviderAndUncommitted()
    {
        var repo = Path.Combine(_root, "widget");
        Directory.CreateDirectory(repo);
        RunGit(repo, "-c", "init.defaultBranch=main", "init");
        ConfigureIdentity(repo);
        RunGit(repo, "remote", "add", "origin", "https://github.com/acme/widget.git");
        WriteFile(repo, "README.md", "hi\n");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-m", "initial");
        // Leave an uncommitted change.
        WriteFile(repo, "dirty.txt", "wip\n");

        var status = await new RepositoryStatusService().GetStatusAsync(repo, fetchPrune: false);

        Assert.True(status.Success, status.Error);
        Assert.Equal(RepoProvider.GitHub, status.Provider);
        Assert.Equal("acme", status.Org);
        Assert.False(status.IsClean);
        Assert.True(status.UncommittedCount >= 1);
    }

    [Fact]
    public async Task ScanAsync_ReturnsReposUnderRoot_ExcludesWorktrees()
    {
        MakeRepoWithBareOrigin("one");
        var (two, _) = MakeRepoWithBareOrigin("two");
        // A worktree of "two" living under the root has a .git FILE, not a folder - not a repository.
        RunGit(two, "worktree", "add", "--detach", Path.Combine(_root, "two-wt"));

        var repos = await new RepositoryStatusService().ScanAsync(new[] { _root });

        Assert.Equal(2, repos.Count);
        Assert.Contains(repos, r => r.Name == "one");
        Assert.Contains(repos, r => r.Name == "two");
        Assert.DoesNotContain(repos, r => r.Name == "two-wt");
    }

    // ----- helpers -----

    private (string Repo, string Origin) MakeRepoWithBareOrigin(string name)
    {
        var origin = Path.Combine(_root, name + ".git");
        var repo = Path.Combine(_root, name);
        RunGit(_root, "-c", "init.defaultBranch=main", "init", "--bare", origin);
        RunGit(_root, "-c", "init.defaultBranch=main", "clone", origin, repo);
        ConfigureIdentity(repo);
        WriteFile(repo, "README.md", "init\n");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-m", "initial");
        RunGit(repo, "branch", "-M", "main");
        RunGit(repo, "push", "-u", "origin", "main");
        return (repo, origin);
    }

    private static void ConfigureIdentity(string repo)
    {
        RunGit(repo, "config", "user.email", "test@cc-director.local");
        RunGit(repo, "config", "user.name", "CC Director Test");
        RunGit(repo, "config", "commit.gpgsign", "false");
    }

    private static void WriteFile(string repo, string rel, string content)
    {
        var full = Path.Combine(repo, rel);
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
        var outp = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {err}");
        return outp;
    }
}
