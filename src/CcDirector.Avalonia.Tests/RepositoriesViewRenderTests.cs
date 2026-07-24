using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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
            cachePath: cachePath);
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
    // REGRESSION (inspection finding F12): leaving the detail page through ANY path - the rail
    // buttons included - releases its monitor subscriptions, not just the back button.
    // ---------------------------------------------------------------------------------------
    [AvaloniaFact]
    public async Task RailNavigation_AwayFromDetail_DetachesTheDetailPage()
    {
        var monitor = new RepositoryMonitor(
            enumerate: _ => new[] { "/repo" },
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus { Path = p, Name = "repo", IsClean = true, Success = true }));
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

    [AvaloniaFact]
    public void RepositoriesView_LoadsAndAttachesToMonitorAndRoots()
    {
        var monitor = new RepositoryMonitor(
            enumerate: _ => Array.Empty<string>(),
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus { Path = p, Name = p, Success = true }));
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
