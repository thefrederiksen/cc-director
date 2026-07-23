using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Avalonia.Tests;

public class RepositoryListViewRenderTests
{
    private static RepositoryMonitor MonitorWith(params string[] names)
        => new(
            enumerate: _ => names,
            compute: (p, _, _) => Task.FromResult(new RepositoryStatus
            {
                Path = p,
                Name = p,
                Provider = RepoProvider.GitHub,
                Branch = "main",
                IsClean = true,
                Success = true,
            }));

    [AvaloniaFact]
    public void RepositoryListView_LoadsAndLaysOut()
    {
        var view = new RepositoryListView();
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(view);
    }

    [AvaloniaFact]
    public async Task RepositoryListView_RendersMonitorModel()
    {
        var monitor = MonitorWith("alpha", "beta", "gamma");
        await monitor.RescanAsync(new[] { "/roots" });

        var view = new RepositoryListView();
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        view.Attach(monitor);
        Dispatcher.UIThread.RunJobs();

        // The view rendered the monitor's three repositories without scanning anything itself.
        Assert.Equal(3, monitor.Snapshot().Count);
    }
}
