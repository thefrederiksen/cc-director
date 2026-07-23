using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CcDirector.Avalonia.Controls;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Headless render guards: construct and lay out the Source Control container and its Worktrees
/// page on the real Avalonia headless platform. This catches runtime XAML load, resource, and
/// binding errors that the compiler does not - constructing the control runs InitializeComponent.
/// </summary>
public class SourceControlViewRenderTests
{
    [AvaloniaFact]
    public void SourceControlView_LoadsAndLaysOut_WithBothPages()
    {
        var view = new SourceControlView();
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Reaching here means the XAML for the container AND both hosted pages loaded and laid out.
        Assert.NotNull(view);
    }

    [AvaloniaFact]
    public void WorktreesView_LoadsWithZeroOrphanCount()
    {
        var view = new WorktreesView();
        var window = new Window { Content = view, Width = 700, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, view.OrphanedCount);
    }
}
