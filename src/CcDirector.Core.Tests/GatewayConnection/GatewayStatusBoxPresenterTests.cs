using CcDirector.Core.GatewayConnection;
using Xunit;

namespace CcDirector.Core.Tests.GatewayConnection;

/// <summary>
/// Proves the single status box's presenter (design spec section 6): the pure mapping that turns a live
/// verification snapshot into the box's four visual states and its two check lines. These tests pin the
/// four looks and the Connected-line rule: the marker carries the verdict (green check / amber ring / red
/// cross) and the line names WHICH gateway - the host in every state where a gateway is configured
/// (connected, connecting, AND failed, so the person can see which gateway is unreachable), and empty when
/// no gateway is selected yet. Line 2 still shows the account identity or the "Sign in" nudge. The named
/// failing leg moved off the box into the tooltip and the panel's repair banner. The presenter has no UI
/// and no I/O, so it is fully unit-tested here.
/// </summary>
public sealed class GatewayStatusBoxPresenterTests
{
    // A fully-connected, signed-in snapshot the tests vary one field at a time from.
    private static GatewayConnectionInputs AllGreenInputs() => new(
        GatewayConfigured: true,
        Connection: GatewayConnectionVerification.Connected,
        FailedLeg: GatewayConnectionFailedLeg.None,
        WasEverConnected: true,
        DeviceKeyPresent: true,
        Account: GatewayAccountSignInState.SignedIn);

    // ---- The four visual states -----------------------------------------------------------------

    [Fact]
    public void Describe_NotConfigured_IsAmber_ConnectedLineEmptySignInNudge()
    {
        // Brand new: no gateway selected yet. Line 1 carries no verdict it cannot have - it is left empty
        // (the surface hides the row), so the box does not pretend a connection state. Line 2 still nudges.
        var inputs = new GatewayConnectionInputs(
            GatewayConfigured: false,
            Connection: GatewayConnectionVerification.Unknown,
            FailedLeg: GatewayConnectionFailedLeg.None,
            WasEverConnected: false,
            DeviceKeyPresent: false,
            Account: GatewayAccountSignInState.Unknown);

        var content = GatewayStatusBoxPresenter.Describe(inputs, gatewayHost: null, accountEmail: null);

        Assert.Equal(GatewayStatusBoxVisual.Amber, content.Visual);
        Assert.Equal(GatewayCheckState.Pending, content.Connected.Marker);
        Assert.Equal(string.Empty, content.Connected.Text);
        Assert.Equal("Sign in", content.SignedIn.Text);
    }

    [Fact]
    public void Describe_ConnectedNotSignedIn_IsAmber_Line1ConnectedLine2SignIn()
    {
        // Connected handshake proven, but no device key / signed out at the Gateway.
        var inputs = AllGreenInputs() with
        {
            DeviceKeyPresent = false,
            Account = GatewayAccountSignInState.SignedOut,
        };

        var content = GatewayStatusBoxPresenter.Describe(inputs, gatewayHost: "SOREN_NORTH", accountEmail: null);

        Assert.Equal(GatewayStatusBoxVisual.Amber, content.Visual);
        Assert.Equal(GatewayCheckState.Passed, content.Connected.Marker);
        // Line 1 names WHICH gateway; the green checkmarker carries "connected".
        Assert.Equal("SOREN_NORTH", content.Connected.Text);
        Assert.Equal(GatewayCheckState.Pending, content.SignedIn.Marker);
        Assert.Equal("Sign in", content.SignedIn.Text);
    }

    [Fact]
    public void Describe_Connecting_IsYellow_ConnectedLineIsConnecting()
    {
        var inputs = AllGreenInputs() with
        {
            Connection = GatewayConnectionVerification.Verifying,
            DeviceKeyPresent = false,
            Account = GatewayAccountSignInState.Unknown,
        };

        var content = GatewayStatusBoxPresenter.Describe(inputs, gatewayHost: "SOREN_NORTH", accountEmail: null);

        Assert.Equal(GatewayStatusBoxVisual.Yellow, content.Visual);
        Assert.Equal(GatewayCheckState.Working, content.Connected.Marker);
        // The amber working-ring carries "connecting"; the line names the gateway.
        Assert.Equal("SOREN_NORTH", content.Connected.Text);
    }

    [Fact]
    public void Describe_AllGreen_IsGreen_Line2ShowsEmail()
    {
        var content = GatewayStatusBoxPresenter.Describe(
            AllGreenInputs(), gatewayHost: "SOREN_NORTH", accountEmail: "soren@centerconsulting.com");

        Assert.Equal(GatewayStatusBoxVisual.Green, content.Visual);
        Assert.Equal(GatewayCheckState.Passed, content.Connected.Marker);
        Assert.Equal("SOREN_NORTH", content.Connected.Text);
        Assert.Equal(GatewayCheckState.Passed, content.SignedIn.Marker);
        Assert.Equal("Signed in: soren@centerconsulting.com", content.SignedIn.Text);
    }

    [Fact]
    public void Describe_AllGreenWithoutEmail_StillGreen_LineNeverEmpty()
    {
        var content = GatewayStatusBoxPresenter.Describe(
            AllGreenInputs(), gatewayHost: "SOREN_NORTH", accountEmail: null);

        Assert.Equal(GatewayStatusBoxVisual.Green, content.Visual);
        Assert.Equal("Signed in", content.SignedIn.Text);
    }

    // Both red states show WHICH gateway is unreachable on line 1 - the named failing leg moved off the
    // line into the tooltip and the panel's repair banner; the red cross marker carries "failed".

    [Fact]
    public void Describe_ConnectFailed_IsRed_ConnectedLineShowsGatewayHost()
    {
        // A first-time connect failure: red, line 1 names the (unreachable) gateway, not the leg.
        var inputs = AllGreenInputs() with
        {
            Connection = GatewayConnectionVerification.Failed,
            FailedLeg = GatewayConnectionFailedLeg.Callback,
            WasEverConnected = false,
            DeviceKeyPresent = false,
            Account = GatewayAccountSignInState.Unavailable,
        };

        var content = GatewayStatusBoxPresenter.Describe(inputs, gatewayHost: "SOREN_NORTH", accountEmail: null);

        Assert.Equal(GatewayStatusBoxVisual.Red, content.Visual);
        Assert.Equal(GatewayCheckState.Failed, content.Connected.Marker);
        Assert.Equal("SOREN_NORTH", content.Connected.Text);
    }

    [Fact]
    public void Describe_WasConnectedNowUnreachable_IsRed_ConnectedLineShowsGatewayHost()
    {
        // Mid-session Gateway drop: was working this run, now unreachable. Same line-1 rule: name the gateway.
        var inputs = AllGreenInputs() with
        {
            Connection = GatewayConnectionVerification.Failed,
            FailedLeg = GatewayConnectionFailedLeg.OutboundReach,
            WasEverConnected = true,
            Account = GatewayAccountSignInState.Unavailable,
        };

        var content = GatewayStatusBoxPresenter.Describe(inputs, gatewayHost: "SOREN_NORTH", accountEmail: null);

        Assert.Equal(GatewayStatusBoxVisual.Red, content.Visual);
        Assert.Equal(GatewayCheckState.Failed, content.Connected.Marker);
        Assert.Equal("SOREN_NORTH", content.Connected.Text);
    }

    // ---- Load-bearing rules ---------------------------------------------------------------------

    [Fact]
    public void Describe_ConnectedButAccountUnavailable_SignedInLineIsMuted_NeverFalseSignOut()
    {
        // Handshake proven but the account read could not be completed: the signed-in line must be the
        // muted "cannot tell yet" marker, never a red/amber false sign-out alarm (decision 3).
        var inputs = AllGreenInputs() with
        {
            DeviceKeyPresent = false,
            Account = GatewayAccountSignInState.Unavailable,
        };

        var content = GatewayStatusBoxPresenter.Describe(inputs, gatewayHost: "SOREN_NORTH", accountEmail: null);

        Assert.Equal(GatewayStatusBoxVisual.Amber, content.Visual);
        Assert.Equal(GatewayCheckState.Passed, content.Connected.Marker);
        Assert.Equal(GatewayCheckState.Unknown, content.SignedIn.Marker);
    }

    [Fact]
    public void VisualFor_MapsAllSixResolverStatesToFourVisualStates()
    {
        Assert.Equal(GatewayStatusBoxVisual.Amber, GatewayStatusBoxPresenter.VisualFor(GatewayConnectionState.NotConfigured));
        Assert.Equal(GatewayStatusBoxVisual.Yellow, GatewayStatusBoxPresenter.VisualFor(GatewayConnectionState.Connecting));
        Assert.Equal(GatewayStatusBoxVisual.Red, GatewayStatusBoxPresenter.VisualFor(GatewayConnectionState.ConnectFailed));
        Assert.Equal(GatewayStatusBoxVisual.Amber, GatewayStatusBoxPresenter.VisualFor(GatewayConnectionState.ConnectedNotSignedIn));
        Assert.Equal(GatewayStatusBoxVisual.Green, GatewayStatusBoxPresenter.VisualFor(GatewayConnectionState.AllGreen));
        Assert.Equal(GatewayStatusBoxVisual.Red, GatewayStatusBoxPresenter.VisualFor(GatewayConnectionState.WasConnectedNowUnreachable));
    }
}
