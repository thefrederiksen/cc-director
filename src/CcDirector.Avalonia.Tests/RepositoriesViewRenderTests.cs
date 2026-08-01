using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Avalonia.Tests;

public class RepositoriesViewRenderTests
{
    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F1): a provisional (warm-start, still verifying) entry
    // must never open the detail screen - detail exposes stage, commit, discard, and branch
    // delete against a cached path that has not been re-verified. A verified entry opens fine.
    // ---------------------------------------------------------------------------------------
    [AvaloniaFact]
    public async Task OpenDetail_ProvisionalEntry_IsRefused_VerifiedEntryOpens()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "ccd-provcache-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(cachePath, System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new RepositoryStatus { Path = "/repo/cached", Name = "cached", Success = true },
        }));
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { "/repo/cached" },
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus { Path = p, Name = "cached", Success = true }),
            cachePath: cachePath) { LiveSessionsProvider = OneBrainRegressionTests.NoSessions };
        monitor.LoadCache(); // entry is now provisional - no scan has re-verified it

        var store = new RootDirectoryStore(
            Path.Combine(Path.GetTempPath(), "ccd-provroots-" + Guid.NewGuid().ToString("N") + ".json"));
        var view = new RepositoriesView();
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        view.Attach(monitor, store, () => { });
        Dispatcher.UIThread.RunJobs();

        view.OpenDetail("/repo/cached");
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.DetailPage.IsVisible); // refused while verifying

        // After the scan re-verifies the entry, the same open succeeds.
        await monitor.RescanAsync(new[] { "/roots" });
        view.OpenDetail("/repo/cached");
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.DetailPage.IsVisible);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-6): the detail-screen guard fails CLOSED. An
    // UNKNOWN path (FindForPath returns null - a stale recommendation, a queued navigation
    // after the monitor removed the entry) must be refused exactly like a provisional one:
    // only a positively known, verified entry may reach the destructive detail surface.
    // ---------------------------------------------------------------------------------------
    [AvaloniaFact]
    public async Task OpenDetail_UnknownPath_IsRefused_FailClosed()
    {
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { "/repo/known" },
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus { Path = p, Name = "known", Success = true }))
        { LiveSessionsProvider = OneBrainRegressionTests.NoSessions };
        await monitor.RescanAsync(new[] { "/roots" });

        var store = new RootDirectoryStore(
            Path.Combine(Path.GetTempPath(), "ccd-unknownroots-" + Guid.NewGuid().ToString("N") + ".json"));
        var view = new RepositoriesView();
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        view.Attach(monitor, store, () => { });
        Dispatcher.UIThread.RunJobs();

        view.OpenDetail("/repo/not-in-the-model"); // the monitor knows nothing about this path
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.DetailPage.IsVisible); // refused - unknown is not verified
        Assert.False(view.DetailPage.IsAttached);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F12): leaving the detail page through ANY path - the rail
    // buttons included - releases its monitor subscriptions, not just the back button.
    // ---------------------------------------------------------------------------------------
    [AvaloniaFact]
    public async Task RailNavigation_AwayFromDetail_DetachesTheDetailPage()
    {
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { "/repo" },
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus { Path = p, Name = "repo", IsClean = true, Success = true }))
        { LiveSessionsProvider = OneBrainRegressionTests.NoSessions };
        await monitor.RescanAsync(new[] { "/roots" });

        var store = new RootDirectoryStore(
            Path.Combine(Path.GetTempPath(), "ccd-railroots-" + Guid.NewGuid().ToString("N") + ".json"));
        var view = new RepositoriesView();
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        view.Attach(monitor, store, () => { });
        Dispatcher.UIThread.RunJobs();

        view.OpenDetail("/repo");
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.DetailPage.IsVisible);
        Assert.True(view.DetailPage.IsAttached);

        view.ShowPage("repos"); // what the rail's Repositories button invokes
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.DetailPage.IsVisible);
        Assert.False(view.DetailPage.IsAttached); // hidden AND detached - no live subscriptions remain
    }

    // ---------------------------------------------------------------------------------------
    // The hand-off is the clipboard. Recommendations offer Copy (per card) and Copy all - and
    // no longer offer to pick an agent, because the product never could: the old "Hand to an
    // agent" button always spawned a NEW session rather than choosing one.
    // ---------------------------------------------------------------------------------------
    [AvaloniaFact]
    public async Task Recommendations_OfferCopy_AndNoAgentHandOff()
    {
        // A safe-to-reap worktree recommends immediately. (An aged-dirty repo would not: the monitor
        // stamps dirty-since at the first scan, so nothing is old enough on scan one.)
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { "/repo/wt" },
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus
            {
                Path = p, Name = "wt", Branch = "main", IsClean = true, Success = true,
                WorktreeCount = 1, WorktreesSafeToReap = 1,
                Worktrees = new[]
                {
                    new WorktreeInfo { Path = "/wt/a", Branch = "feat/a", Safety = WorktreeSafety.SafeToReap, SizeBytes = 1_048_576 },
                },
            }))
        { LiveSessionsProvider = OneBrainRegressionTests.NoSessions };
        await monitor.RescanAsync(new[] { "/roots" });

        var store = new RootDirectoryStore(
            Path.Combine(Path.GetTempPath(), "ccd-copyroots-" + Guid.NewGuid().ToString("N") + ".json"));
        var view = new RepositoriesView();
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        view.Attach(monitor, store, () => { });
        view.ShowPage("reco");
        Dispatcher.UIThread.RunJobs();

        var buttons = view.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).ToList();
        Assert.Contains("Copy", buttons);       // per-card
        Assert.Contains("Copy all", buttons);   // page header
        Assert.Contains("Show me", buttons);    // the card really rendered
        Assert.DoesNotContain("Hand to an agent", buttons);
    }

    /// <summary>With nothing to recommend there is nothing to copy, so the button is not offered.</summary>
    [AvaloniaFact]
    public async Task CopyAll_IsHidden_WhenThereAreNoRecommendations()
    {
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { "/repo/tidy" },
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus
            {
                Path = p, Name = "tidy", Branch = "main", IsClean = true, Success = true,
            }))
        { LiveSessionsProvider = OneBrainRegressionTests.NoSessions };
        await monitor.RescanAsync(new[] { "/roots" });

        var store = new RootDirectoryStore(
            Path.Combine(Path.GetTempPath(), "ccd-copyroots2-" + Guid.NewGuid().ToString("N") + ".json"));
        var view = new RepositoriesView();
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        view.Attach(monitor, store, () => { });
        view.ShowPage("reco");
        Dispatcher.UIThread.RunJobs();

        var copyAll = view.GetVisualDescendants().OfType<Button>()
            .First(b => (b.Content as string) == "Copy all");
        Assert.False(copyAll.IsVisible);
    }

    [AvaloniaFact]
    public void RepositoriesView_LoadsAndAttachesToMonitorAndRoots()
    {
        var monitor = new RepositoryMonitor(
            enumerate: _ => Array.Empty<string>(),
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus { Path = p, Name = p, Success = true }))
        { LiveSessionsProvider = OneBrainRegressionTests.NoSessions };
        var store = new RootDirectoryStore(
            Path.Combine(Path.GetTempPath(), "ccd-reposview-" + Guid.NewGuid().ToString("N") + ".json"));

        var view = new RepositoriesView();
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        view.Attach(monitor, store, () => { });
        // Attaching again is idempotent (opening the pinned view repeatedly).
        view.Attach(monitor, store, () => { });
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(view);
    }
}
