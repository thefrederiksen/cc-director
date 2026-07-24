using CcDirector.Core.Browsers;
using Xunit;

namespace CcDirector.Core.Tests.Browsers;

/// <summary>
/// Proves the pure display fold for automation browsers - the ONE place that decides the status label,
/// the status dot color, the subtitle, the offered action, and the attach command. The Control API
/// serializes this fold for the CLI and the Director's rail and Settings tab render it in-process, so
/// these mappings are the contract every surface shows verbatim.
/// </summary>
public sealed class AutomationBrowserViewFoldTests
{
    private static AutomationBrowser Browser(BrowserKind kind = BrowserKind.Chrome) => new(
        Id: "center-consulting",
        Name: "Center Consulting",
        Kind: kind,
        UserDataDir: @"C:\data\browsers\center-consulting",
        Port: 9310,
        CreatedUtc: new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc),
        LastSignedInUtc: null);

    // ---- The three per-status verdicts (label, dot, action) --------------------------------------

    [Fact]
    public void Fold_Ready_IsGreenAttach()
    {
        var view = AutomationBrowserViewFold.Fold(Browser(), AutomationBrowserStatus.Ready, "soren@centerconsulting.com");

        Assert.Equal("Ready", view.StatusLabel);
        Assert.Equal("green", view.DotColor);
        Assert.Equal("Attach", view.ActionLabel);
        Assert.Equal("Chrome - soren@centerconsulting.com", view.Subtitle);
    }

    [Fact]
    public void Fold_NeedsSignIn_IsYellowSignIn()
    {
        var view = AutomationBrowserViewFold.Fold(Browser(), AutomationBrowserStatus.NeedsSignIn, account: null);

        Assert.Equal("Needs sign-in", view.StatusLabel);
        Assert.Equal("yellow", view.DotColor);
        Assert.Equal("Sign in", view.ActionLabel);
        Assert.Equal("Chrome - not signed in yet", view.Subtitle);
    }

    [Fact]
    public void Fold_Stopped_IsGreyStart()
    {
        var view = AutomationBrowserViewFold.Fold(Browser(BrowserKind.Edge), AutomationBrowserStatus.Stopped, account: null);

        Assert.Equal("Stopped", view.StatusLabel);
        Assert.Equal("grey", view.DotColor);
        Assert.Equal("Start", view.ActionLabel);
        Assert.Equal("Edge - stopped", view.Subtitle);
    }

    // ---- Subtitle rules ---------------------------------------------------------------------------

    [Fact]
    public void Subtitle_AccountWinsOverStateWords_InEveryStatus()
    {
        // The signed-in account is the most useful fact; when known it is shown regardless of status
        // (a stopped browser that HAS a login still names whose login it holds).
        Assert.Equal("Chrome - a@b.com", AutomationBrowserViewFold.Subtitle(BrowserKind.Chrome, AutomationBrowserStatus.Stopped, "a@b.com"));
        Assert.Equal("Chrome - a@b.com", AutomationBrowserViewFold.Subtitle(BrowserKind.Chrome, AutomationBrowserStatus.NeedsSignIn, "a@b.com"));
        Assert.Equal("Chrome - a@b.com", AutomationBrowserViewFold.Subtitle(BrowserKind.Chrome, AutomationBrowserStatus.Ready, "a@b.com"));
    }

    [Fact]
    public void Subtitle_ReadyWithUnreadableAccount_SaysSignedIn()
    {
        // Ready means the human confirmed sign-in; an unreadable profile must not demote that to a
        // "not signed in" wording.
        Assert.Equal("Chrome - signed in", AutomationBrowserViewFold.Subtitle(BrowserKind.Chrome, AutomationBrowserStatus.Ready, account: null));
    }

    // ---- Attach command ---------------------------------------------------------------------------

    [Fact]
    public void Fold_AttachCommand_UsesTheSlugId_NotTheFreeTextName()
    {
        // The id is shell-safe by construction; the free-text name is not.
        var view = AutomationBrowserViewFold.Fold(Browser(), AutomationBrowserStatus.Ready, account: null);

        Assert.Equal("eval \"$(cc-devthrottle browser attach 'center-consulting')\"", view.AttachCommand);
        Assert.Equal("center-consulting", view.BuName);
        Assert.Equal("http://127.0.0.1:9310", view.BuCdpUrl);
    }

    [Fact]
    public void AttachCommand_EmptyId_Throws()
    {
        Assert.Throws<ArgumentException>(() => AutomationBrowserViewFold.AttachCommand(""));
    }

    // ---- Carried-through identity fields ----------------------------------------------------------

    [Fact]
    public void Fold_CarriesTheBrowserIdentityVerbatim()
    {
        var browser = Browser();
        var view = AutomationBrowserViewFold.Fold(browser, AutomationBrowserStatus.Stopped, account: null);

        Assert.Equal(browser.Id, view.Id);
        Assert.Equal(browser.Name, view.Name);
        Assert.Equal("Chrome", view.Browser);
        Assert.Equal(browser.Port, view.Port);
        Assert.Equal(browser.UserDataDir, view.UserDataDir);
        Assert.Equal(browser.CreatedUtc, view.CreatedUtc);
        Assert.Null(view.LastSignedInUtc);
        Assert.Equal(AutomationBrowserStatus.Stopped, view.Status);
    }

    // ---- Guard rails ------------------------------------------------------------------------------

    [Fact]
    public void Fold_UnknownStatus_ThrowsInsteadOfInventingAVerdict()
    {
        var bogus = (AutomationBrowserStatus)99;
        Assert.Throws<ArgumentOutOfRangeException>(() => AutomationBrowserViewFold.Fold(Browser(), bogus, account: null));
    }

    [Fact]
    public void Fold_NullBrowser_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AutomationBrowserViewFold.Fold(null!, AutomationBrowserStatus.Ready, account: null));
    }
}
