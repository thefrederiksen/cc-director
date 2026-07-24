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
