using CcDirector.Core.GatewayConnection;
using Xunit;

namespace CcDirector.Core.Tests.GatewayConnection;

/// <summary>
/// Proves the single, pure decision point of the Gateway Connection redesign (design spec section 4):
/// the resolver that reduces the two verification sources (the two-way handshake and the Gateway's
/// signed-in report) into ONE of six states, the panel step it opens on, and the paint state of the two
/// check lines. These tests pin every state and the load-bearing rules: green is earned only by a proven
/// handshake and a real device key; an Unavailable account while connected is "cannot tell yet", never a
/// false sign-out; and a mid-session Gateway drop reads as "was working, now unreachable", not "never set
/// up". The resolver has no UI and no I/O, so it is fully unit-tested here.
/// </summary>
public sealed class GatewayConnectionStateResolverTests
{
    // A fully-connected, signed-in snapshot the tests vary one field at a time from.
    private static GatewayConnectionInputs AllGreenInputs() => new(
        GatewayConfigured: true,
        Connection: GatewayConnectionVerification.Connected,
        FailedLeg: GatewayConnectionFailedLeg.None,
        WasEverConnected: true,
        DeviceKeyPresent: true,
        Account: GatewayAccountSignInState.SignedIn);

    [Fact]
    public void Resolve_NoAddressNeverConnected_IsNotConfigured_OpensOnConnect()
    {
        var inputs = new GatewayConnectionInputs(
            GatewayConfigured: false,
            Connection: GatewayConnectionVerification.Unknown,
            FailedLeg: GatewayConnectionFailedLeg.None,
            WasEverConnected: false,
            DeviceKeyPresent: false,
            Account: GatewayAccountSignInState.Unknown);

        var resolved = GatewayConnectionStateResolver.Resolve(inputs);

        Assert.Equal(GatewayConnectionState.NotConfigured, resolved.State);
        Assert.Equal(GatewayPanelStep.Connect, resolved.TargetStep);
        Assert.Equal(GatewayCheckState.Pending, resolved.ConnectedCheck);
        Assert.Equal(GatewayCheckState.Unknown, resolved.SignedInCheck);
    }

    [Fact]
    public void Resolve_AddressSetHandshakeInFlight_IsConnecting()
    {
        var inputs = AllGreenInputs() with
        {
            Connection = GatewayConnectionVerification.Verifying,
        };

        var resolved = GatewayConnectionStateResolver.Resolve(inputs);

        Assert.Equal(GatewayConnectionState.Connecting, resolved.State);
        Assert.Equal(GatewayPanelStep.Connect, resolved.TargetStep);
        Assert.Equal(GatewayCheckState.Working, resolved.ConnectedCheck);
    }

    [Fact]
    public void Resolve_AddressSetButNoHandshakeYet_IsConnecting()
    {
        // A configured Gateway with no handshake result yet is pending verification (yellow), not an error.
        var inputs = new GatewayConnectionInputs(
            GatewayConfigured: true,
            Connection: GatewayConnectionVerification.Unknown,
            FailedLeg: GatewayConnectionFailedLeg.None,
            WasEverConnected: false,
            DeviceKeyPresent: false,
            Account: GatewayAccountSignInState.Unknown);

        var resolved = GatewayConnectionStateResolver.Resolve(inputs);

        Assert.Equal(GatewayConnectionState.Connecting, resolved.State);
    }

    [Fact]
    public void Resolve_HandshakeFailedNeverConnectedThisRun_IsConnectFailed_OpensOnConnectRepair()
    {
        var inputs = new GatewayConnectionInputs(
            GatewayConfigured: true,
            Connection: GatewayConnectionVerification.Failed,
            FailedLeg: GatewayConnectionFailedLeg.Callback,
            WasEverConnected: false,
            DeviceKeyPresent: false,
            Account: GatewayAccountSignInState.Unknown);

        var resolved = GatewayConnectionStateResolver.Resolve(inputs);

        Assert.Equal(GatewayConnectionState.ConnectFailed, resolved.State);
        Assert.Equal(GatewayPanelStep.Connect, resolved.TargetStep);
        Assert.Equal(GatewayCheckState.Failed, resolved.ConnectedCheck);
    }

    [Fact]
    public void Resolve_HandshakeProvenButSignedOut_IsConnectedNotSignedIn_OpensOnSignIn()
    {
        var inputs = AllGreenInputs() with
        {
            Account = GatewayAccountSignInState.SignedOut,
        };

        var resolved = GatewayConnectionStateResolver.Resolve(inputs);

        Assert.Equal(GatewayConnectionState.ConnectedNotSignedIn, resolved.State);
        Assert.Equal(GatewayPanelStep.SignIn, resolved.TargetStep);
        Assert.Equal(GatewayCheckState.Passed, resolved.ConnectedCheck);
        Assert.Equal(GatewayCheckState.Pending, resolved.SignedInCheck);
    }

    [Fact]
    public void Resolve_HandshakeProvenSignedInButNoDeviceKey_IsConnectedNotSignedIn_NeverFalseGreen()
    {
        // Signed-in green requires BOTH a device key AND the Gateway reporting signed in (decision 3).
        // Account says signed in but this device is not paired -> not AllGreen.
        var inputs = AllGreenInputs() with
        {
            DeviceKeyPresent = false,
            Account = GatewayAccountSignInState.SignedIn,
        };

        var resolved = GatewayConnectionStateResolver.Resolve(inputs);

        Assert.Equal(GatewayConnectionState.ConnectedNotSignedIn, resolved.State);
        Assert.NotEqual(GatewayConnectionState.AllGreen, resolved.State);
        Assert.Equal(GatewayCheckState.Pending, resolved.SignedInCheck);
    }

    [Fact]
    public void Resolve_ConnectedButAccountUnavailable_IsNotAFalseSignedOut_MutedSignedInLine()
    {
        // The load-bearing rule: an Unavailable account while Connected is "cannot tell yet" (muted),
        // never the amber/red signed-out alarm (decision 3, and the AccountIndicator's rule).
        var inputs = AllGreenInputs() with
        {
            Account = GatewayAccountSignInState.Unavailable,
        };

        var resolved = GatewayConnectionStateResolver.Resolve(inputs);

        Assert.Equal(GatewayConnectionState.ConnectedNotSignedIn, resolved.State);
        Assert.Equal(GatewayCheckState.Passed, resolved.ConnectedCheck);
        Assert.Equal(GatewayCheckState.Unknown, resolved.SignedInCheck);
        Assert.NotEqual(GatewayCheckState.Failed, resolved.SignedInCheck);
        Assert.NotEqual(GatewayCheckState.Pending, resolved.SignedInCheck);
    }

    [Fact]
    public void Resolve_HandshakeProvenAndSignedIn_IsAllGreen_OpensOnDone()
    {
        var resolved = GatewayConnectionStateResolver.Resolve(AllGreenInputs());

        Assert.Equal(GatewayConnectionState.AllGreen, resolved.State);
        Assert.Equal(GatewayPanelStep.Done, resolved.TargetStep);
        Assert.Equal(GatewayCheckState.Passed, resolved.ConnectedCheck);
        Assert.Equal(GatewayCheckState.Passed, resolved.SignedInCheck);
    }

    [Fact]
    public void Resolve_WasConnectedThenHandshakeFails_IsWasConnectedNowUnreachable_OpensOnConnectRepair()
    {
        // A mid-session Gateway move: the handshake succeeded earlier this run, now it fails. This must
        // read as "was working, now unreachable" (repair), not "never set up".
        var inputs = AllGreenInputs() with
        {
            Connection = GatewayConnectionVerification.Failed,
            FailedLeg = GatewayConnectionFailedLeg.OutboundReach,
            WasEverConnected = true,
        };

        var resolved = GatewayConnectionStateResolver.Resolve(inputs);

        Assert.Equal(GatewayConnectionState.WasConnectedNowUnreachable, resolved.State);
        Assert.Equal(GatewayPanelStep.Connect, resolved.TargetStep);
        Assert.Equal(GatewayCheckState.Failed, resolved.ConnectedCheck);
    }

    [Fact]
    public void Resolve_WasEverConnected_OutranksConnectFailed_OnlyWhenFailed()
    {
        // Same failure, the only difference is whether the handshake ever succeeded this run.
        var everConnected = new GatewayConnectionInputs(
            GatewayConfigured: true,
            Connection: GatewayConnectionVerification.Failed,
            FailedLeg: GatewayConnectionFailedLeg.Callback,
            WasEverConnected: true,
            DeviceKeyPresent: true,
            Account: GatewayAccountSignInState.Unknown);
        var neverConnected = everConnected with { WasEverConnected = false };

        Assert.Equal(GatewayConnectionState.WasConnectedNowUnreachable,
            GatewayConnectionStateResolver.ResolveState(everConnected));
        Assert.Equal(GatewayConnectionState.ConnectFailed,
            GatewayConnectionStateResolver.ResolveState(neverConnected));
    }

    [Fact]
    public void Resolve_VerifyingAfterAMove_ShowsConnectingYellow_NotRed()
    {
        // A re-verify after having been connected shows yellow "Connecting" briefly, never red.
        var inputs = AllGreenInputs() with
        {
            Connection = GatewayConnectionVerification.Verifying,
            WasEverConnected = true,
        };

        var resolved = GatewayConnectionStateResolver.Resolve(inputs);

        Assert.Equal(GatewayConnectionState.Connecting, resolved.State);
        Assert.NotEqual(GatewayConnectionState.WasConnectedNowUnreachable, resolved.State);
    }

    [Theory]
    [InlineData(GatewayConnectionState.NotConfigured, GatewayPanelStep.Connect)]
    [InlineData(GatewayConnectionState.Connecting, GatewayPanelStep.Connect)]
    [InlineData(GatewayConnectionState.ConnectFailed, GatewayPanelStep.Connect)]
    [InlineData(GatewayConnectionState.WasConnectedNowUnreachable, GatewayPanelStep.Connect)]
    [InlineData(GatewayConnectionState.ConnectedNotSignedIn, GatewayPanelStep.SignIn)]
    [InlineData(GatewayConnectionState.AllGreen, GatewayPanelStep.Done)]
    public void StepFor_RoutesEachStateToItsStep(GatewayConnectionState state, GatewayPanelStep expected)
    {
        Assert.Equal(expected, GatewayConnectionStateResolver.StepFor(state));
    }
}
