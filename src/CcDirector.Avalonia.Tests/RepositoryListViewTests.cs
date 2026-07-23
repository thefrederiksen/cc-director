using System.Collections.Generic;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Avalonia.Tests;

public class RepositoryListViewTests
{
    private static RepositoryStatus Repo(
        string name,
        RepoProvider provider = RepoProvider.GitHub,
        string? org = "acme",
        bool clean = true,
        int uncommitted = 0,
        int ahead = 0,
        int behind = 0,
        int behindMain = 0,
        bool detached = false,
        int worktrees = 0,
        int safe = 0,
        int inUse = 0,
        int attn = 0) => new()
    {
        Path = $"/repo/{name}",
        Name = name,
        Provider = provider,
        Org = org,
        IsClean = clean,
        UncommittedCount = uncommitted,
        AheadCount = ahead,
        BehindCount = behind,
        BehindMainCount = behindMain,
        IsDetachedHead = detached,
        WorktreeCount = worktrees,
        WorktreesSafeToReap = safe,
        WorktreesInUse = inUse,
        WorktreesNeedAttention = attn,
        Success = true,
    };

    [Theory]
    [InlineData(RepoProvider.GitHub, "GitHub")]
    [InlineData(RepoProvider.AzureDevOps, "Azure DevOps")]
    [InlineData(RepoProvider.Other, "Git")]
    [InlineData(RepoProvider.None, "local")]
    public void ProviderLabel_Maps(RepoProvider p, string expected) =>
        Assert.Equal(expected, RepositoryListView.ProviderLabel(p));

    [Fact]
    public void WhereText_CleanVsDirty()
    {
        Assert.Equal("clean", RepositoryListView.WhereText(Repo("a", clean: true)));
        Assert.Equal("3 uncommitted", RepositoryListView.WhereText(Repo("a", clean: false, uncommitted: 3)));
    }

    [Fact]
    public void SyncText_Cases()
    {
        Assert.Equal("up to date", RepositoryListView.SyncText(Repo("a")));
        Assert.Equal("ahead 2", RepositoryListView.SyncText(Repo("a", ahead: 2)));
        Assert.Equal("behind 5", RepositoryListView.SyncText(Repo("a", behind: 5)));
        Assert.Equal("ahead 1, behind main 3", RepositoryListView.SyncText(Repo("a", ahead: 1, behindMain: 3)));
        Assert.Equal("detached HEAD", RepositoryListView.SyncText(Repo("a", detached: true)));
    }

    [Fact]
    public void WorktreeText_Cases()
    {
        Assert.Equal("no worktrees", RepositoryListView.WorktreeText(Repo("a")));
        Assert.Equal("1 worktree · 1 safe", RepositoryListView.WorktreeText(Repo("a", worktrees: 1, safe: 1)));
        Assert.Equal("3 worktrees · 1 safe · 1 in use · 1 attention",
            RepositoryListView.WorktreeText(Repo("a", worktrees: 3, safe: 1, inUse: 1, attn: 1)));
    }

    [Fact]
    public void BuildRows_OrdersSafeReapThenDirtyThenName()
    {
        var rows = RepositoryListView.BuildRows(new[]
        {
            Repo("zeta"),
            Repo("dirty", clean: false, uncommitted: 2),
            Repo("reapable", worktrees: 2, safe: 2),
            Repo("alpha"),
        });

        Assert.Equal("reapable", rows[0].Name);  // has safe-to-reap worktrees
        Assert.Equal("dirty", rows[1].Name);     // then uncommitted work
        Assert.Equal("alpha", rows[2].Name);     // then alphabetical
        Assert.Equal("zeta", rows[3].Name);
    }

    [Fact]
    public void BuildRows_CarriesDisplayFields()
    {
        var row = Assert.Single(RepositoryListView.BuildRows(new[]
        {
            Repo("widget", provider: RepoProvider.AzureDevOps, org: "mindzie", worktrees: 2, safe: 1, inUse: 1),
        }));
        Assert.Equal("widget", row.Name);
        Assert.Equal("Azure DevOps", row.Provider);
        Assert.True(row.HasProvider);
        Assert.Contains("mindzie", row.SubPath);
        Assert.Equal("2 worktrees · 1 safe · 1 in use", row.Worktrees);
    }

    [Fact]
    public void BuildSummary_CountsReposDirtyAndReapable()
    {
        var summary = RepositoryListView.BuildSummary(new[]
        {
            Repo("a"),
            Repo("b", clean: false, uncommitted: 1),
            Repo("c", worktrees: 3, safe: 2),
        });
        Assert.Equal("3 on disk · 1 with uncommitted work · 2 worktrees to reap", summary);
    }
}
