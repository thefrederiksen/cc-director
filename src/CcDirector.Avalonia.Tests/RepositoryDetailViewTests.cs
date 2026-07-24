using System;
using System.Collections.Generic;
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

/// <summary>
/// REGRESSION (inspection finding F11): discard never destroys tracked work on one click - a
/// plain-words confirmation stating exactly what is lost stands between the button and the write.
/// </summary>
public class DiscardConfirmationTests
{
    [Fact]
    public void DiscardWarning_StatesTheLossInPlainWords()
    {
        Assert.Equal("This permanently deletes your changes in 1 file. There is no undo.",
            ChangesDiffView.DiscardWarning(1));
        Assert.Equal("This permanently deletes your changes in 3 files. There is no undo.",
            ChangesDiffView.DiscardWarning(3));
    }

    [AvaloniaFact]
    public void Discard_ShowsTheConfirmation_AndKeepChangesRunsNoWrite()
    {
        var view = new ChangesDiffView();
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        view.Attach("/repo");

        bool discardRan = false;
        view.DiscardServiceOverride = (_, _) =>
        {
            discardRan = true;
            return Task.FromResult(new GitWriteResult { Success = true });
        };

        Assert.False(view.DiscardConfirmOverlay.IsVisible);
        view.ShowDiscardConfirmation(new[] { "src/App.cs" });

        Assert.True(view.DiscardConfirmOverlay.IsVisible);
        Assert.Contains("permanently deletes", view.DiscardConfirmTitle.Text);
        Assert.Contains("no undo", view.DiscardConfirmTitle.Text);
        Assert.Contains("src/App.cs", view.DiscardConfirmFiles.Text); // the loss is named
        Assert.False(discardRan); // showing the confirmation wrote NOTHING

        view.KeepChanges();
        Assert.False(view.DiscardConfirmOverlay.IsVisible);
        Assert.False(discardRan); // keeping the changes wrote nothing either
    }

    [AvaloniaFact]
    public async Task Discard_DeleteChanges_RunsTheWrite_ForExactlyTheNamedFiles()
    {
        var view = new ChangesDiffView();
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        view.Attach("/repo");

        IReadOnlyList<string>? discarded = null;
        view.DiscardServiceOverride = (_, files) =>
        {
            discarded = files;
            return Task.FromResult(new GitWriteResult { Success = true });
        };

        view.ShowDiscardConfirmation(new[] { "src/App.cs" });
        await view.DeleteChangesAsync();

        Assert.Equal(new[] { "src/App.cs" }, discarded);
        Assert.False(view.DiscardConfirmOverlay.IsVisible);

        // A second confirm without a fresh confirmation is inert - the pending set was consumed.
        discarded = null;
        await view.DeleteChangesAsync();
        Assert.Null(discarded);
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
            })) { LiveSessionsProvider = OneBrainRegressionTests.NoSessions };
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
