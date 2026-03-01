using CcClick.Helpers;
using Xunit;

namespace CcClick.Tests;

/// <summary>
/// WindowFinder tests. Most methods require a live FlaUI AutomationBase and
/// desktop session, so only validation logic that can be exercised without
/// UI automation is tested here.
/// </summary>
public class WindowFinderTests
{
    [Fact]
    public void FindWindow_NullAutomation_ThrowsNullReferenceException()
    {
        // FindWindow calls FindWindows which calls automation.GetDesktop().
        // Passing null automation should fail immediately, confirming the
        // method does not silently swallow null arguments.
        Assert.ThrowsAny<NullReferenceException>(
            () => WindowFinder.FindWindow(null!, "Notepad"));
    }

    [Fact]
    public void FindWindows_NullAutomation_ThrowsNullReferenceException()
    {
        Assert.ThrowsAny<NullReferenceException>(
            () => WindowFinder.FindWindows(null!));
    }
}
