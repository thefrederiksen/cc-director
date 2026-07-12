using CcDirector.Core.GatewayConnection;
using Xunit;

namespace CcDirector.Core.Tests.GatewayConnection;

/// <summary>
/// Proves the single status box's presenter (design spec section 6): the pure mapping that turns a live
/// verification snapshot into the box's four visual states and its two check lines. These tests pin the
/// four looks and the load-bearing line-text rules the Architect confirmed for Phase 3: amber covers BOTH
/// not-configured (line 1 "Connect to Gateway") and connected-not-signed-in (line 1 "Connected", line 2
/// "Sign in"); yellow is "Connecting..."; green shows the account email on line 2; and red names the
/// failing leg on line 1 for both the first-time failure and the was-working-now-unreachable case. The
/// presenter has no UI and no I/O, so it is fully unit-tested here.
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
    public void Describe_NotConfigured_IsAmber_BothLinesShowNextAction()
    {
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
        Assert.Equal("Connect to Gateway", content.Connected.Text);
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
        Assert.Equal("Connected", content.Connected.Text);
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
        Assert.Equal("Connecting...", content.Connected.Text);
    }

    [Fact]
    public void Describe_AllGreen_IsGreen_Line2ShowsEmail()
    {
        var content = GatewayStatusBoxPresenter.Describe(
            AllGreenInputs(), gatewayHost: "SOREN_NORTH", accountEmail: "soren@centerconsulting.com");

        Assert.Equal(GatewayStatusBoxVisual.Green, content.Visual);
        Assert.Equal(GatewayCheckState.Passed, content.Connected.Marker);
        Assert.Equal("Connected", content.Connected.Text);
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

    [Fact]
    public void Describe_ConnectFailedCallback_IsRed_NamesTheCallbackLeg()
    {
        // A first-time connect failure on the callback leg: red, and the failing leg named (decision 11).
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
        Assert.Equal("Gateway cannot reach this Director back", content.Connected.Text);
    }

    [Fact]
    public void Describe_WasConnectedNowUnreachable_IsRed_NamesOutboundLeg()
    {
        // Mid-session Gateway drop: was working this run, now the handshake fails on the outbound leg.
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
        Assert.Equal("Cannot reach the Gateway", content.Connected.Text);
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
