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
    public void Fold_Checking_IsGreyAndOffersNoAction()
    {
        var view = AutomationBrowserViewFold.Fold(Browser(), AutomationBrowserStatus.Checking, account: null);

        Assert.Equal("Checking...", view.StatusLabel);
        Assert.Equal("grey", view.DotColor);
        Assert.Equal("Chrome - checking...", view.Subtitle);

        // The empty action is the point, not an oversight: every action a surface could offer depends
        // on whether the browser is running, and "Start" on a browser that turns out to be running is
        // the wrong button. A surface reads empty as "offer nothing yet".
        Assert.Equal("", view.ActionLabel);
    }

    [Fact]
    public void Fold_Checking_StillCarriesEverythingThatCostsNoProbe()
    {
        // The whole reason this status exists: a surface can paint the row in full - name, browser,
        // port, account, attach command - and wait only for the one fact that is slow.
        var view = AutomationBrowserViewFold.Fold(Browser(), AutomationBrowserStatus.Checking, "soren@centerconsulting.com");

        Assert.Equal("center-consulting", view.Id);
        Assert.Equal("Center Consulting", view.Name);
        Assert.Equal("Chrome", view.Browser);
        Assert.Equal(9310, view.Port);
        Assert.Equal("soren@centerconsulting.com", view.Account);
        Assert.Equal("Chrome - soren@centerconsulting.com", view.Subtitle);
        Assert.Contains("center-consulting", view.AttachCommand);
    }

    [Fact]
    public void EveryStatus_FoldsWithoutThrowing()
    {
        // The three display switches throw on an unhandled status, so a status added without a display
        // decision takes the whole list down at render time rather than at build time. This is the
        // build-time catch.
        foreach (var status in Enum.GetValues<AutomationBrowserStatus>())
        {
            var view = AutomationBrowserViewFold.Fold(Browser(), status, account: null);
            Assert.False(string.IsNullOrWhiteSpace(view.StatusLabel));
            Assert.False(string.IsNullOrWhiteSpace(view.DotColor));
            Assert.False(string.IsNullOrWhiteSpace(view.Subtitle));
        }
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

    // ---- The pinned rail row ----------------------------------------------------------------------
    //
    // One clickable row in the left rail, never a list: it carries the profile count and a dot when any
    // profile is up, and nothing else. These prove the two facts it states are true.

    private static AutomationBrowserView View(string id, AutomationBrowserStatus status) =>
        AutomationBrowserViewFold.Fold(
            new AutomationBrowser(
                Id: id,
                Name: id,
                Kind: BrowserKind.Chrome,
                UserDataDir: $@"C:\data\browsers\{id}",
                Port: 9310,
                CreatedUtc: new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc),
                LastSignedInUtc: null),
            status,
            account: null);

    [Fact]
    public void FoldRail_CountsProfilesAndSaysHowManyAreRunning()
    {
        var rail = AutomationBrowserViewFold.FoldRail(
            new[]
            {
                View("a", AutomationBrowserStatus.Ready),
                View("b", AutomationBrowserStatus.Stopped),
                View("c", AutomationBrowserStatus.NeedsSignIn),
                View("d", AutomationBrowserStatus.Stopped),
            },
            harnessInstalled: true);

        Assert.True(rail.ShowCount);
        Assert.Equal("4", rail.CountText);
        // Running means the debug port answered. A browser that is up but never signed in is running.
        Assert.Equal("4 browser profiles, 2 running. Click to manage them in Settings.", rail.ToolTip);
        Assert.Equal("green", rail.RunningDotColor);
        Assert.False(rail.ShowSetup);
    }

    [Fact]
    public void FoldRail_NoneRunning_SaysSoAndShowsNoDot()
    {
        var rail = AutomationBrowserViewFold.FoldRail(
            new[] { View("a", AutomationBrowserStatus.Stopped), View("b", AutomationBrowserStatus.Stopped) },
            harnessInstalled: true);

        Assert.Equal("2 browser profiles, none running. Click to manage them in Settings.", rail.ToolTip);
        Assert.Null(rail.RunningDotColor);
        Assert.Equal("2", rail.CountText);
    }

    [Fact]
    public void FoldRail_OneProfile_ReadsSingular()
    {
        var rail = AutomationBrowserViewFold.FoldRail(
            new[] { View("a", AutomationBrowserStatus.Ready) },
            harnessInstalled: true);

        Assert.Equal("1 browser profile, 1 running. Click to manage them in Settings.", rail.ToolTip);
    }

    [Fact]
    public void FoldRail_WhileAnyProfileIsStillBeingProbed_SaysNothingAboutRunning()
    {
        // The row repaints on a timer, so "none running" stated during the probe window would be a
        // falsehood shown repeatedly. Unknown is said by not saying it - the count is still true.
        var rail = AutomationBrowserViewFold.FoldRail(
            new[] { View("a", AutomationBrowserStatus.Checking), View("b", AutomationBrowserStatus.Stopped) },
            harnessInstalled: true);

        Assert.Equal("2 browser profiles. Click to manage them in Settings.", rail.ToolTip);
        Assert.Null(rail.RunningDotColor);
    }

    [Fact]
    public void FoldRail_ProbedProfileRunningWhileAnotherIsStillChecking_StillShowsTheDot()
    {
        // The dot is an existence claim ("at least one is up"), which a single probed Ready already
        // settles - unlike the running COUNT, which the unprobed profile could still change.
        var rail = AutomationBrowserViewFold.FoldRail(
            new[] { View("a", AutomationBrowserStatus.Ready), View("b", AutomationBrowserStatus.Checking) },
            harnessInstalled: true);

        Assert.Equal("green", rail.RunningDotColor);
        Assert.Equal("2 browser profiles. Click to manage them in Settings.", rail.ToolTip);
    }

    [Fact]
    public void FoldRail_HarnessNotInstalled_WearsTheSetupNudgeAndNoCount()
    {
        // The row must stay visible and say what is missing: the feature advertises itself rather than
        // hiding, and clicking it lands on the screen where the install runs.
        var rail = AutomationBrowserViewFold.FoldRail(
            new[] { View("a", AutomationBrowserStatus.Stopped) },
            harnessInstalled: false);

        Assert.True(rail.ShowSetup);
        Assert.False(rail.ShowCount);
        Assert.Null(rail.RunningDotColor);
        Assert.Contains("not set up yet", rail.ToolTip);
    }

    [Fact]
    public void FoldRail_NoProfilesYet_InvitesTheFirstOne()
    {
        var rail = AutomationBrowserViewFold.FoldRail(Array.Empty<AutomationBrowserView>(), harnessInstalled: true);

        Assert.False(rail.ShowSetup);
        Assert.False(rail.ShowCount);
        Assert.Null(rail.RunningDotColor);
        Assert.Equal("No browser profiles yet. Click to add one your agents can drive.", rail.ToolTip);
    }

    [Fact]
    public void FoldRail_DotColorIsAPaletteNameTheDesktopKnows()
    {
        // The surface maps this name to a brush; an unknown name renders the magenta BROKEN sentinel.
        var rail = AutomationBrowserViewFold.FoldRail(new[] { View("a", AutomationBrowserStatus.Ready) }, harnessInstalled: true);

        Assert.Equal(DotColorOf(AutomationBrowserStatus.Ready), rail.RunningDotColor);
    }

    private static string DotColorOf(AutomationBrowserStatus status) => AutomationBrowserViewFold.DotColor(status);

    [Fact]
    public void FoldRail_NullList_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AutomationBrowserViewFold.FoldRail(null!, harnessInstalled: true));
    }
}
