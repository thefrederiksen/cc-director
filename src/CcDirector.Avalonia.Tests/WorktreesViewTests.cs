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

    private static WorktreeInfo InUse(string branch, params string[] sessions) => new()
    {
        Path = $"/repo/{branch}",
        Branch = branch,
        Safety = WorktreeSafety.InUseBySession,
        Reason = WorktreeSafetyReason.LiveSessionOpen,
        Explanation = "Safe to remove, but a session is still open in it.",
        IsClean = true,
        OpenSessions = sessions,
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

    [Fact]
    public void FormatActivity_Null_ReturnsEmpty()
    {
        Assert.Equal("", WorktreesView.FormatActivity(null));
    }

    [Fact]
    public void FormatActivity_KnownUtc_FormatsAsLocalDateTime()
    {
        var utc = new DateTime(2026, 07, 23, 10, 30, 0, DateTimeKind.Utc);
        var expectedLocal = utc.ToLocalTime();

        var text = WorktreesView.FormatActivity(utc);

        Assert.StartsWith("Last activity: ", text);
        Assert.Contains(expectedLocal.ToString("yyyy-MM-dd HH:mm"), text);
    }

    [Fact]
    public void BuildSafeRows_IncludesTimestamp_WhenActivityKnown()
    {
        var w = new WorktreeInfo
        {
            Path = "/repo/feat-a",
            Branch = "feat-a",
            Safety = WorktreeSafety.SafeToReap,
            Reason = WorktreeSafetyReason.OriginBranchGone,
            Explanation = "Origin branch deleted after merge.",
            IsClean = true,
            LastActivityUtc = new DateTime(2026, 07, 23, 9, 0, 0, DateTimeKind.Utc),
        };
        var row = Assert.Single(WorktreesView.BuildSafeRows(Inventory(w)));
        Assert.True(row.HasTimestamp);
        Assert.StartsWith("Last activity: ", row.Timestamp);
    }

    [Fact]
    public void BuildSafeRows_NoTimestamp_WhenActivityUnknown()
    {
        var row = Assert.Single(WorktreesView.BuildSafeRows(Inventory(Safe("feat-a"))));
        Assert.False(row.HasTimestamp);
        Assert.Equal("", row.Timestamp);
    }

    // --- In-use-by-a-session (third case) rendering ---

    [Fact]
    public void BuildInUseRows_ContainsInUseWorktrees_WithSessionNames()
    {
        var inv = Inventory(Primary(), Safe("free"), InUse("busy", "ERP KB Builder (#109)"));

        var row = Assert.Single(WorktreesView.BuildInUseRows(inv));
        Assert.Equal("busy", row.Title);
        Assert.Contains("ERP KB Builder (#109)", row.Detail);
        Assert.True(row.HasDetail);
    }

    [Fact]
    public void BuildSafeRows_ExcludesInUseWorktrees()
    {
        var inv = Inventory(Safe("free"), InUse("busy", "Sess (#1)"));

        var safe = WorktreesView.BuildSafeRows(inv);
        Assert.Single(safe);
        Assert.Equal("free", safe[0].Title);
        Assert.DoesNotContain(safe, r => r.Title == "busy");
        Assert.Equal(1, inv.SafeToReapCount); // only "free"; the in-use "busy" is excluded
    }

    [Fact]
    public void SessionsFor_MultipleSessions_JoinsWithComma()
    {
        var w = InUse("busy", "A (#1)", "B (#2)");
        Assert.Equal("Open sessions: A (#1), B (#2)", WorktreesView.SessionsFor(w));
    }

    [Fact]
    public void SessionsFor_SingleSession_UsesSingular()
    {
        var w = InUse("busy", "A (#1)");
        Assert.Equal("Open session: A (#1)", WorktreesView.SessionsFor(w));
    }

    [Fact]
    public void BuildReport_IncludesInUseGroup()
    {
        var inv = Inventory(Safe("free"), InUse("busy", "Sess (#7)"));
        var report = WorktreesView.BuildReport(inv);
        Assert.Contains("IN USE BY AN OPEN SESSION", report);
        Assert.Contains("busy", report);
        Assert.Contains("sess (#7)", report); // lower-cased in the report
        Assert.Contains("In use by a session: 1", report);
    }
}
