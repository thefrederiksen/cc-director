using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Onboarding;
using Xunit;

namespace CcDirector.Core.Tests.Onboarding;

/// <summary>
/// Tests for <see cref="CodeFolderScout"/> with real temporary directories - the scout's job is
/// classifying real filesystem shapes, so that is what the tests build.
/// </summary>
public sealed class CodeFolderScoutTests : IDisposable
{
    private readonly string _root;

    public CodeFolderScoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "scout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string MakeRepo(string parent, string name)
    {
        var repo = Path.Combine(parent, name);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        return repo;
    }

    [Theory]
    [InlineData("Repos", true)]
    [InlineData("repos", true)]
    [InlineData("ReposFred", true)]
    [InlineData("repositories", true)]
    [InlineData("Projects", true)]
    [InlineData("Project", true)]
    [InlineData("Code", true)]
    [InlineData("dev", true)]
    [InlineData("src", true)]
    [InlineData("git", true)]
    [InlineData("source", true)]
    [InlineData("Windows", false)]
    [InlineData("Program Files", false)]
    [InlineData("Users", false)]
    [InlineData("Games", false)]
    [InlineData("Decode", false)]
    public void NameSuggestsCode_ClassifiesFolderNames(string name, bool expected)
    {
        Assert.Equal(expected, CodeFolderScout.NameSuggestsCode(name));
    }

    [Fact]
    public void CountRepos_CountsImmediateChildrenWithGitDirectories_Only()
    {
        MakeRepo(_root, "alpha");
        MakeRepo(_root, "beta");
        Directory.CreateDirectory(Path.Combine(_root, "not-a-repo"));
        // A repo nested two levels down must NOT count - the monitor lists one level only, and the
        // wizard must promise exactly what the monitor will deliver.
        MakeRepo(Path.Combine(_root, "not-a-repo"), "nested");

        Assert.Equal(2, CodeFolderScout.CountRepos(_root));
    }

    [Fact]
    public void CountRepos_MissingFolder_ReturnsZero()
    {
        Assert.Equal(0, CodeFolderScout.CountRepos(Path.Combine(_root, "gone")));
    }

    [Fact]
    public void IsItselfARepository_GitDirectoryOrWorktreeFile()
    {
        var repo = MakeRepo(_root, "checkout");
        Assert.True(CodeFolderScout.IsItselfARepository(repo));

        var worktree = Path.Combine(_root, "wt");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: elsewhere");
        Assert.True(CodeFolderScout.IsItselfARepository(worktree));

        Assert.False(CodeFolderScout.IsItselfARepository(Path.Combine(_root, "not-a-repo-here")));
        Directory.CreateDirectory(Path.Combine(_root, "plain"));
        Assert.False(CodeFolderScout.IsItselfARepository(Path.Combine(_root, "plain")));
    }

    [Fact]
    public void ResolveBrowsedFolder_BaseFolderWithRepos_IsKeptAsIs()
    {
        MakeRepo(_root, "alpha");
        Assert.Equal(Path.GetFullPath(_root), CodeFolderScout.ResolveBrowsedFolder(_root));
    }

    [Fact]
    public void ResolveBrowsedFolder_SingleRepository_ResolvesToItsParent()
    {
        // The user browsed INTO one of their repos. The roots list holds base folders and the
        // monitor lists children, so registering the repo itself would make it invisible - the
        // parent is the folder they meant.
        var repo = MakeRepo(_root, "myproject");
        Assert.Equal(Path.GetFullPath(_root), CodeFolderScout.ResolveBrowsedFolder(repo));
    }

    [Fact]
    public void ResolveBrowsedFolder_RepoThatAlsoContainsChildRepos_IsKeptAsIs()
    {
        // A monorepo-style folder that is a checkout AND holds child checkouts: the user's pick
        // wins - it verifiably yields repositories as a base folder.
        var repo = MakeRepo(_root, "mono");
        MakeRepo(repo, "sub");
        Assert.Equal(Path.GetFullPath(repo), CodeFolderScout.ResolveBrowsedFolder(repo));
    }

    [Fact]
    public async Task ScanAsync_StreamsOnlyCandidatesThatHoldRepositories()
    {
        // CandidateRoots() reads the real machine, so this asserts the CONTRACT of what it streams:
        // every suggestion must exist and verifiably hold at least one repository by the one-level rule.
        var seen = new List<CodeFolderSuggestion>();
        var progress = new SyncProgress(seen);

        await CodeFolderScout.ScanAsync(progress, CancellationToken.None);

        foreach (var s in seen)
        {
            Assert.True(Directory.Exists(s.Path));
            Assert.True(s.RepoCount > 0);
            Assert.Equal(CodeFolderScout.CountRepos(s.Path), s.RepoCount);
        }
    }

    [Fact]
    public async Task ScanAsync_Cancellation_StopsTheSweep()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CodeFolderScout.ScanAsync(new SyncProgress(new List<CodeFolderSuggestion>()), cts.Token));
    }

    /// <summary>Synchronous IProgress - Progress&lt;T&gt; posts to the thread pool, which would race the assertions.</summary>
    private sealed class SyncProgress : IProgress<CodeFolderSuggestion>
    {
        private readonly List<CodeFolderSuggestion> _sink;
        public SyncProgress(List<CodeFolderSuggestion> sink) => _sink = sink;
        public void Report(CodeFolderSuggestion value)
        {
            lock (_sink) _sink.Add(value);
        }
    }
}
