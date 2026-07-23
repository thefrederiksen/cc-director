using System.Collections.Generic;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Tests the Worktrees page's pure display builders - the row lists and the copy-to-clipboard
/// report - without constructing any UI. The safety verdict itself is decided (and tested) in
/// Core; here we only prove the view renders that verdict faithfully.
/// </summary>
public class WorktreesViewTests
{
    private static WorktreeInfo Safe(string branch, string reason = "Origin branch deleted after merge.") => new()
    {
        Path = $"/repo/{branch}",
        Branch = branch,
        Safety = WorktreeSafety.SafeToReap,
        Reason = WorktreeSafetyReason.OriginBranchGone,
        Explanation = reason,
        IsClean = true,
    };

    private static WorktreeInfo Stranded(string branch, int ahead = 1, int behind = 0, bool clean = true, int dirty = 0, bool openPr = false) => new()
    {
        Path = $"/repo/{branch}",
        Branch = branch,
        Safety = WorktreeSafety.NeedsAttention,
        Reason = clean ? WorktreeSafetyReason.NotProvenMerged : WorktreeSafetyReason.UncommittedChanges,
        Explanation = clean ? "Commits not proven to be in origin/main." : "Uncommitted or untracked content present.",
        IsClean = clean,
        DirtyFileCount = dirty,
        AheadOfMain = ahead,
        BehindMain = behind,
        HasOpenPullRequest = openPr,
    };

    private static WorktreeInfo Primary() => new()
    {
        Path = "/repo",
        Branch = "main",
        IsPrimary = true,
        Safety = WorktreeSafety.NeedsAttention,
        Reason = WorktreeSafetyReason.PrimaryCheckout,
        Explanation = "Primary checkout - never removed.",
        IsClean = true,
    };

    private static WorktreeInventory Inventory(params WorktreeInfo[] worktrees) =>
        new() { RepositoryPath = "/repo", Worktrees = worktrees, Success = true };

    [Fact]
    public void BuildSafeRows_ContainsOnlySafeWorktrees()
    {
        var inv = Inventory(Primary(), Safe("feat-a"), Stranded("feat-b"));

        var rows = WorktreesView.BuildSafeRows(inv);

        var row = Assert.Single(rows);
        Assert.Equal("feat-a", row.Title);
        Assert.Equal("/repo/feat-a", row.Path);
        Assert.Equal("Origin branch deleted after merge.", row.Reason);
    }

    [Fact]
    public void BuildNeedsRows_ExcludesPrimaryAndSafe()
    {
        var inv = Inventory(Primary(), Safe("feat-a"), Stranded("feat-b"));

        var rows = WorktreesView.BuildNeedsRows(inv);

        var row = Assert.Single(rows);
        Assert.Equal("feat-b", row.Title);
        Assert.DoesNotContain(rows, r => r.Title == "main");
    }

    [Fact]
    public void DetailFor_DirtyWorktree_ReportsUncommittedCount()
    {
        var w = Stranded("dirty", clean: false, dirty: 3);
        Assert.Equal("3 uncommitted file(s)", WorktreesView.DetailFor(w));
    }

    [Fact]
    public void DetailFor_CleanAheadBehindWithOpenPr_ComposesAllParts()
    {
        var w = Stranded("busy", ahead: 2, behind: 5, openPr: true);
        Assert.Equal("ahead 2, behind 5, open pull request", WorktreesView.DetailFor(w));
    }

    [Fact]
    public void DetailFor_DetachedTitle_UsesDetachedLabel()
    {
        var w = new WorktreeInfo
        {
            Path = "/repo/det",
            Branch = null,
            IsDetachedHead = true,
            Safety = WorktreeSafety.SafeToReap,
            Explanation = "Detached HEAD is contained in origin/main.",
            IsClean = true,
        };
        var rows = WorktreesView.BuildSafeRows(Inventory(w));
        Assert.Equal("(detached HEAD)", Assert.Single(rows).Title);
    }

    [Fact]
    public void BuildReport_ListsBothGroups_AndCounts()
    {
        var inv = Inventory(Primary(), Safe("feat-a"), Stranded("feat-b", ahead: 1));

        var report = WorktreesView.BuildReport(inv);

        Assert.Contains("Safe to reap: 1", report);
        Assert.Contains("Needs attention: 1", report);
        Assert.Contains("SAFE TO REAP", report);
        Assert.Contains("feat-a", report);
        Assert.Contains("NEEDS ATTENTION", report);
        Assert.Contains("feat-b", report);
        Assert.Contains("Worktree report for /repo", report);
        // The primary checkout is neither safe nor needs-attention, so its own path line is absent.
        Assert.DoesNotContain("path:   /repo\r\n", report);
        Assert.DoesNotContain("path:   /repo\n", report);
    }

    [Fact]
    public void BuildReport_EmptyGroups_ShowNone()
    {
        var report = WorktreesView.BuildReport(Inventory(Primary()));
        Assert.Contains("Safe to reap: 0", report);
        Assert.Contains("Needs attention: 0", report);
        Assert.Contains("(none)", report);
    }
}
