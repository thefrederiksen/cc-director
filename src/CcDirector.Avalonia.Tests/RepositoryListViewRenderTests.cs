using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CcDirector.Avalonia.Controls;
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
}
