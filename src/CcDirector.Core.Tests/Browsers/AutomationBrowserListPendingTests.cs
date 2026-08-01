using CcDirector.Core.Browsers;
using Xunit;

namespace CcDirector.Core.Tests.Browsers;

/// <summary>
/// Tests for the instant, unprobed list - the pass a surface paints BEFORE it knows which browsers are
/// running. Runs against an isolated CC_DIRECTOR_ROOT so it reads a throwaway registry.json, and opens
/// no sockets: the whole point of this path is that it touches nothing but local files.
/// </summary>
[Collection("CcStorageRoot")]
public sealed class AutomationBrowserListPendingTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private static readonly Func<int, bool> AllFree = _ => true;

    public AutomationBrowserListPendingTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-pendinglist-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void FirstPaint_ListsEveryBrowserAsChecking()
    {
        AutomationBrowserRegistry.Create("Center Consulting", BrowserKind.Chrome, AllFree);
        AutomationBrowserRegistry.Create("mindzie", BrowserKind.Edge, AllFree);

        var views = AutomationBrowserViewFold.ListPending();

        Assert.Equal(2, views.Count);
        Assert.All(views, v => Assert.Equal(AutomationBrowserStatus.Checking, v.Status));

        // Nothing about the row is deferred except whether it is running: the names, browsers and
        // ports are all present, which is what makes painting this first worth doing.
        Assert.Equal(new[] { "Center Consulting", "mindzie" }, views.Select(v => v.Name).ToArray());
        Assert.Equal(new[] { "Chrome", "Edge" }, views.Select(v => v.Browser).ToArray());
        Assert.All(views, v => Assert.True(v.Port > 0));
    }

    [Fact]
    public void ARefresh_KeepsTheLastKnownStatusInsteadOfBlinkingBackToChecking()
    {
        var created = AutomationBrowserRegistry.Create("Center Consulting", BrowserKind.Chrome, AllFree);
        var previous = new[] { AutomationBrowserViewFold.Fold(created, AutomationBrowserStatus.Ready, "a@b.com") };

        var views = AutomationBrowserViewFold.ListPending(previous);

        // The rail re-reads on a timer. Resetting an already-answered row to "Checking..." every tick
        // reads as instability, so a row we have an answer for keeps it while the fresh probe runs.
        var view = Assert.Single(views);
        Assert.Equal(AutomationBrowserStatus.Ready, view.Status);
    }

    [Fact]
    public void ARefresh_StillShowsARenameImmediately()
    {
        var created = AutomationBrowserRegistry.Create("Old Name", BrowserKind.Chrome, AllFree);
        var previous = new[] { AutomationBrowserViewFold.Fold(created, AutomationBrowserStatus.Ready, account: null) };
        AutomationBrowserRegistry.Rename(created.Id, "New Name");

        var view = Assert.Single(AutomationBrowserViewFold.ListPending(previous));

        // ONLY the status is carried over. Everything else is re-read, or a rename would appear to do
        // nothing until the probe came back.
        Assert.Equal("New Name", view.Name);
        Assert.Equal(AutomationBrowserStatus.Ready, view.Status);
    }

    [Fact]
    public void ABrowserAddedSinceTheLastPaint_IsCheckingNotStale()
    {
        var first = AutomationBrowserRegistry.Create("First", BrowserKind.Chrome, AllFree);
        var previous = new[] { AutomationBrowserViewFold.Fold(first, AutomationBrowserStatus.Ready, account: null) };
        AutomationBrowserRegistry.Create("Second", BrowserKind.Edge, AllFree);

        var views = AutomationBrowserViewFold.ListPending(previous);

        Assert.Equal(AutomationBrowserStatus.Ready, Assert.Single(views, v => v.Name == "First").Status);
        Assert.Equal(AutomationBrowserStatus.Checking, Assert.Single(views, v => v.Name == "Second").Status);
    }

    [Fact]
    public void APreviousListThatWasItselfStillChecking_DoesNotCarryCheckingForward()
    {
        // Carrying "Checking" forward would be carrying a non-answer, which is the same as having none.
        var created = AutomationBrowserRegistry.Create("Center Consulting", BrowserKind.Chrome, AllFree);
        var previous = new[] { AutomationBrowserViewFold.Fold(created, AutomationBrowserStatus.Checking, account: null) };

        var view = Assert.Single(AutomationBrowserViewFold.ListPending(previous));

        Assert.Equal(AutomationBrowserStatus.Checking, view.Status);
    }

    [Fact]
    public void AnEmptyRegistry_ListsNothingRatherThanThrowing()
    {
        Assert.Empty(AutomationBrowserViewFold.ListPending());
    }
}
