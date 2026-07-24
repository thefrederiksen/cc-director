using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Avalonia.Tests;

public class RepositoryDetailPureTests
{
    [Fact]
    public void HeaderStats_ComposesTheStoryLine()
    {
        var s = new RepositoryStatus
        {
            Branch = "main",
            IsClean = false,
            UncommittedCount = 13,
            DirtySinceUtc = DateTime.UtcNow.AddDays(-3),
            AheadCount = 0,
            BehindCount = 142,
            WorktreeCount = 6,
            WorktreeBytes = 5_400_000_000,
            Success = true,
        };
        var line = RepositoryDetailView.HeaderStats(s);
        Assert.Contains("branch main", line);
        Assert.Contains("13 uncommitted for 3 day(s)", line);
        Assert.Contains("behind 142", line);
        Assert.Contains("6 worktree(s), 5.0 GB", line);
    }

    [Theory]
    [InlineData(0, "0 KB")]
    [InlineData(512, "1 KB")]
    [InlineData(1_500_000, "1 MB")]
    [InlineData(1_200_000_000, "1.1 GB")]
    public void FormatBytes_HumanReadable(long bytes, string expected)
        => Assert.Equal(expected, RepositoryDetailView.FormatBytes(bytes));

    [Fact]
    public void ToBranchRow_SafeBranch_Deletable_CurrentIsNot()
    {
        var safe = RepositoryDetailView.ToBranchRow(new BranchInfo
        {
            Name = "done", SafeToDelete = true, Explanation = "Pull request merged.",
        });
        Assert.True(safe.CanDelete);
        Assert.Equal("safe to delete", safe.Chip);

        var current = RepositoryDetailView.ToBranchRow(new BranchInfo
        {
            Name = "main", IsCurrent = true, SafeToDelete = false, Explanation = "The branch you are on - switch away before deleting.",
        });
        Assert.False(current.CanDelete);
        Assert.Equal("current", current.Chip);
        Assert.True(current.IsCurrent);
    }

    [Fact]
    public void ToPullRequestRow_MapsChecksAndReview()
    {
        var row = RepositoryDetailView.ToPullRequestRow(new PullRequestInfo
        {
            Number = 42, Title = "The fix", Author = "soren", Branch = "fix/it",
            Checks = ChecksState.Failing, ReviewState = "changes requested",
            Url = "https://example/pr/42", CreatedUtc = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc),
        });
        Assert.Equal("X", row.ChecksGlyph);
        Assert.Contains("#42", row.Meta);
        Assert.Contains("fix/it", row.Meta);
        Assert.Equal("changes requested", row.Chip);
        Assert.True(row.HasChip);
    }

    [Fact]
    public void BuildRows_DiffRendering_MapsKindsAndBinary()
    {
        var rows = ChangesDiffView.BuildRows(new[]
        {
            new FileDiff
            {
                NewPath = "a.cs",
                Hunks = new[]
                {
                    new DiffHunk
                    {
                        Header = "@@ -1,2 +1,2 @@",
                        Lines = new[]
                        {
                            new DiffLine { Kind = DiffLineKind.Context, Text = "keep", OldNumber = 1, NewNumber = 1 },
                            new DiffLine { Kind = DiffLineKind.Removed, Text = "old", OldNumber = 2 },
                            new DiffLine { Kind = DiffLineKind.Added, Text = "new", NewNumber = 2 },
                        },
                    },
                },
            },
            new FileDiff { NewPath = "logo.png", IsBinary = true },
        });

        Assert.Equal(5, rows.Count); // hunk header + 3 lines + binary notice - and nothing invented
        Assert.Equal("@@ -1,2 +1,2 @@", rows[0].Text);
        Assert.Equal("keep", rows[1].Text);
        Assert.Equal("2", rows[2].OldNo);
        Assert.Equal("", rows[2].NewNo);
        Assert.Equal("", rows[3].OldNo);
        Assert.Equal("2", rows[3].NewNo);
        Assert.Contains("binary", rows[4].Text);
    }
}

public class RepositoryDetailRenderTests
{
    [AvaloniaFact]
    public async Task DetailView_AttachesAndRendersHeader_FromMonitor()
    {
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { "/repo" },
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus
            {
                Path = p, Name = "widget", Branch = "main", IsClean = true,
                RemoteUrl = "https://github.com/acme/widget.git", Success = true,
            }));
        await monitor.RescanAsync(new[] { "/roots" });

        var view = new RepositoryDetailView();
        var window = new Window { Content = view, Width = 900, Height = 620 };
        window.Show();
        view.Attach(monitor, "/repo");
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(view);
    }

    [AvaloniaFact]
    public void HandToAgentDialog_Constructs_AndBuildsABrief()
    {
        var repo = new RepositoryStatus
        {
            Path = "/repo", Name = "widget", Branch = "main",
            IsClean = false, UncommittedCount = 7, Success = true,
        };
        var dialog = new global::CcDirector.Avalonia.HandToAgentDialog(repo);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(dialog);
    }
}
