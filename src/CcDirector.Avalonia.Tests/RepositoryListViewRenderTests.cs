using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Avalonia.Tests;

public class RepositoryListViewRenderTests
{
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
    public void RepositoryScreenWindow_Constructs_AndWiresNamedControls()
    {
        // Reproduces the "Repository status..." menu path. A hand-written InitializeComponent that
        // skipped the generated named-field hookup left ListView null and threw in this constructor.
        var store = new RootDirectoryStore(
            Path.Combine(Path.GetTempPath(), "ccd-rootstore-" + Guid.NewGuid().ToString("N") + ".json"));

        var window = new global::CcDirector.Avalonia.RepositoryScreenWindow(store, null);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(window);
    }
}
