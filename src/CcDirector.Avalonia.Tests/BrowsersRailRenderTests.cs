using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CcDirector.Avalonia.Controls;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The pinned "Browser profiles" rail entry is ONE clickable row that navigates, exactly like the
/// pinned Repositories entry - never an expandable list, and never a place to start a browser.
///
/// It used to unfold into one two-line row per profile, each with its own Start link: a third of the
/// rail spent on a list, and a control panel in a navigation strip. These drive the REAL control in a
/// headless window, because the defect was a rendered one - a fold test cannot see how many rows and
/// buttons reach the screen.
/// </summary>
public class BrowsersRailRenderTests
{
    private static (BrowsersRailGroup Rail, Window Window) Show()
    {
        var rail = new BrowsersRailGroup();
        var window = new Window { Content = rail, Width = 260, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (rail, window);
    }

    [AvaloniaFact]
    public void TheRail_RendersExactlyOneClickableRow_AndNoList()
    {
        var (rail, _) = Show();

        // One button: the row itself. Two would mean an action link is back in the rail (Start / Sign
        // in / Attach / +), which is what moved to the settings screen.
        Assert.Single(rail.GetVisualDescendants().OfType<Button>());

        // No list control at all: there is no per-profile row to render, expanded or collapsed.
        Assert.Empty(rail.GetVisualDescendants().OfType<ItemsControl>());
    }

    [AvaloniaFact]
    public void ClickingTheRow_AsksToManage_AndNeverExpands()
    {
        var (rail, _) = Show();
        var row = rail.GetVisualDescendants().OfType<Button>().Single();

        var manageRequests = 0;
        rail.ManageRequested += (_, _) => manageRequests++;

        row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, manageRequests);
        Assert.Single(rail.GetVisualDescendants().OfType<Button>());

        // Clicked again it asks again - it does not toggle between "open settings" and "unfold here".
        row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, manageRequests);
        Assert.Single(rail.GetVisualDescendants().OfType<Button>());
    }

    [AvaloniaFact]
    public void TheRailIsTheRow_WithNothingStackedUnderIt()
    {
        // The complaint that started this was space, but measured height is the wrong check for it: the
        // old group's profile rows arrived asynchronously, so it too measured one row high the instant
        // it was shown, and a height assertion PASSES on the broken control. What is structurally true
        // instead is that this rail IS a row - not a header with a body panel stacked beneath it, which
        // is where the space went.
        var (rail, _) = Show();

        Assert.IsType<Button>(rail.Content);
    }
}
