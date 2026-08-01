using CcDirector.Avalonia.Controls;
using CcDirector.Core.Browsers;
using Xunit;

namespace CcDirector.Avalonia.Tests.Controls;

/// <summary>
/// Proves the one-time sign-in dialog names an account route only for browsers that HAVE one. The
/// dialog is the single instruction a person gets about what to sign in to, and naming the wrong
/// provider sends them hunting for a profile sync their browser does not offer.
/// </summary>
public sealed class BrowserSignInFlowTests
{
    [Fact]
    public void ChromeAndEdge_AreNamedByTheirOwnAccountProvider()
    {
        Assert.Equal("a Google account", BrowserSignInFlow.AccountRoute(nameof(BrowserKind.Chrome)));
        Assert.Equal("a Microsoft account", BrowserSignInFlow.AccountRoute(nameof(BrowserKind.Edge)));
    }

    [Fact]
    public void BraveAndOpera_AreNotOfferedAnAccountRouteAtAll()
    {
        // Neither has an account that carries a signed-in profile across, so the dialog must fall to
        // the per-site instruction rather than naming Google's or Microsoft's.
        Assert.Null(BrowserSignInFlow.AccountRoute(nameof(BrowserKind.Brave)));
        Assert.Null(BrowserSignInFlow.AccountRoute(nameof(BrowserKind.Opera)));
    }

    [Fact]
    public void TheBrowserNameIsMatchedCaseInsensitively()
    {
        // The view carries whatever text the fold produced; a case difference must not silently drop
        // Chrome into the accountless branch.
        Assert.Equal("a Google account", BrowserSignInFlow.AccountRoute("chrome"));
        Assert.Equal("a Microsoft account", BrowserSignInFlow.AccountRoute("EDGE"));
    }
}
