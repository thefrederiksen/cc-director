using CcDirector.Core.Browsers;
using Xunit;

namespace CcDirector.Core.Tests.Browsers;

/// <summary>
/// Proves the sign-in landing page is decided for EVERY browser we can drive. The sign-in flow opens
/// this URL in the browser the human is about to sign in to by hand, so an unmapped browser is not a
/// cosmetic gap - it is the one screen the whole feature depends on, opened on the wrong page or not
/// at all.
/// </summary>
public sealed class AutomationBrowserServiceAccountUrlTests
{
    [Fact]
    public void EveryBrowserKind_HasASignInLandingPage()
    {
        foreach (var kind in Enum.GetValues<BrowserKind>())
        {
            var url = AutomationBrowserService.AccountUrl(kind);
            Assert.StartsWith("https://", url);
        }
    }

    [Fact]
    public void ChromeAndEdge_LandOnTheirOwnAccountProvider()
    {
        // These two DO have a browser account that carries a profile across, and landing on the right
        // provider is the shortcut the flow is offering. Sending an Edge user to Google's page is a
        // dead end they cannot sign in from.
        Assert.Equal("https://accounts.google.com/", AutomationBrowserService.AccountUrl(BrowserKind.Chrome));
        Assert.Equal("https://login.live.com/", AutomationBrowserService.AccountUrl(BrowserKind.Edge));
    }

    [Fact]
    public void BraveAndOpera_DoNotLandOnAnotherBrowsersAccountPage()
    {
        // Neither has a carry-everything account, so neither may be pointed at Google's or Microsoft's
        // sign-in - that would send someone hunting for a profile sync their browser does not have.
        var accountPages = new[] { "https://accounts.google.com/", "https://login.live.com/" };

        Assert.DoesNotContain(AutomationBrowserService.AccountUrl(BrowserKind.Brave), accountPages);
        Assert.DoesNotContain(AutomationBrowserService.AccountUrl(BrowserKind.Opera), accountPages);
    }
}
