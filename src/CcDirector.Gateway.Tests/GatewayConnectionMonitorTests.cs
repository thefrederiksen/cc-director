using CcDirector.ControlApi;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Director-side home of the Gateway-connection truth: green is earned by a LIVE TUNNEL only;
/// NotConfigured is a legitimate gray, never red; a stale LastVerifiedAt survives later failures so the UI
/// can say "was connected until HH:mm".
///
/// Gateway Cleanup mission (tunnel-only): the two-way nonce handshake these tests were written against
/// (issues #223/#224) is gone - the Gateway deleted its half at the cut, so it could never pass again and its
/// only remaining effect was to paint a lie over a healthy connection. The behaviours it used to guard are
/// re-asserted here against the tunnel writers, which now earn the green.
/// </summary>
public sealed class GatewayConnectionMonitorTests
{
    [Fact]
    public void FreshMonitor_IsNotConfigured()
    {
        var m = new GatewayConnectionMonitor();
        Assert.Equal(GatewayConnectionStatus.NotConfigured, m.Status);
        Assert.Null(m.LastVerifiedAt);
        Assert.Null(m.FailureSummary);
    }

    [Fact]
    public void Reset_Configured_GoesConnecting()
    {
        var m = new GatewayConnectionMonitor();
        m.Reset(gatewayConfigured: true);
        Assert.Equal(GatewayConnectionStatus.Connecting, m.Status);
    }

    [Fact]
    public void Reset_NotConfigured_GoesGray_AndClearsHistory()
    {
        var m = new GatewayConnectionMonitor();
        m.Reset(gatewayConfigured: true);
        m.MarkTunnelConnected();

        m.Reset(gatewayConfigured: false);
        Assert.Equal(GatewayConnectionStatus.NotConfigured, m.Status);
        Assert.Null(m.LastVerifiedAt); // a connection to the old gateway says nothing now
    }

    [Fact]
    public void MarkTunnelConnected_GoesConnected_AndStamps()
    {
        var m = new GatewayConnectionMonitor();
        m.Reset(gatewayConfigured: true);

        m.MarkTunnelConnected();

        Assert.Equal(GatewayConnectionStatus.Connected, m.Status);
        Assert.NotNull(m.LastVerifiedAt);
        Assert.Null(m.FailureSummary);
    }

    [Fact]
    public void MarkTunnelConnected_NeverGoesGreenFromNotConfigured()
    {
        // Gray is sticky: a local-only Director with no gateway.url never lights up.
        var m = new GatewayConnectionMonitor();

        m.MarkTunnelConnected();

        Assert.Equal(GatewayConnectionStatus.NotConfigured, m.Status);
        Assert.Null(m.LastVerifiedAt);
    }

    [Fact]
    public void TunnelDrop_GoesConnecting_AndKeepsOlderConnectedStamp()
    {
        var m = new GatewayConnectionMonitor();
        m.Reset(gatewayConfigured: true);

        m.MarkTunnelConnected();
        var connectedAt = m.LastVerifiedAt;
        Assert.NotNull(connectedAt);

        m.MarkTunnelConnecting(); // the tunnel dropped and is redialing

        Assert.Equal(GatewayConnectionStatus.Connecting, m.Status);
        Assert.Equal(connectedAt, m.LastVerifiedAt); // "was connected until HH:mm" survives
    }

    [Fact]
    public void RegistrationFailureAfterConnected_GoesFailed_KeepsOlderConnectedStamp()
    {
        var m = new GatewayConnectionMonitor();
        m.Reset(gatewayConfigured: true);

        m.MarkTunnelConnected();
        var connectedAt = m.LastVerifiedAt;
        Assert.NotNull(connectedAt);

        m.ReportRegistrationFailure("Cannot reach the Gateway at http://gw:7878: connection refused");

        Assert.Equal(GatewayConnectionStatus.Failed, m.Status);
        Assert.Contains("connection refused", m.FailureSummary);
        Assert.Equal(connectedAt, m.LastVerifiedAt); // "was connected until HH:mm" survives
    }

    [Fact]
    public void ReportRegistrationFailure_GoesFailed_ButNeverFromNotConfigured()
    {
        var m = new GatewayConnectionMonitor();

        // Gray is sticky: a local-only Director must never show red.
        m.ReportRegistrationFailure("anything");
        Assert.Equal(GatewayConnectionStatus.NotConfigured, m.Status);

        m.Reset(gatewayConfigured: true);
        m.ReportRegistrationFailure("Cannot reach the Gateway at http://gw:7878: connection refused");
        Assert.Equal(GatewayConnectionStatus.Failed, m.Status);
        Assert.Contains("connection refused", m.FailureSummary);
    }

    [Fact]
    public void ReportTailnetIdentityFailure_GoesExplicitState_ButNeverFromNotConfigured()
    {
        // Issue #324: the identity failure is its OWN state (the remediation is local, not
        // a Gateway problem), and gray stays sticky for local-only Directors.
        var m = new GatewayConnectionMonitor();

        m.ReportTailnetIdentityFailure("anything");
        Assert.Equal(GatewayConnectionStatus.NotConfigured, m.Status);

        m.Reset(gatewayConfigured: true);
        m.ReportTailnetIdentityFailure("No tailnet identity: start Tailscale or set gateway.tailnetEndpoint.");
        Assert.Equal(GatewayConnectionStatus.NoTailnetIdentity, m.Status);
        Assert.Contains("start Tailscale", m.FailureSummary);
    }

    [Fact]
    public void ReportTailnetIdentityFailure_RepeatedIdenticalSummary_NoChurn()
    {
        var m = new GatewayConnectionMonitor();
        m.Reset(gatewayConfigured: true);
        var fired = 0;
        m.Changed += () => fired++;

        m.ReportTailnetIdentityFailure("reason A"); // 1
        m.ReportTailnetIdentityFailure("reason A"); // suppressed (heartbeat re-resolve repeats it)
        m.ReportTailnetIdentityFailure("reason B"); // 2

        Assert.Equal(2, fired);
        Assert.Equal(GatewayConnectionStatus.NoTailnetIdentity, m.Status);
    }

    [Fact]
    public void Changed_FiresOnTransitions_NotOnRepeatedIdenticalFailure()
    {
        var m = new GatewayConnectionMonitor();
        var fired = 0;
        m.Changed += () => fired++;

        m.Reset(gatewayConfigured: true);              // 1
        m.ReportRegistrationFailure("reason A");        // 2
        m.ReportRegistrationFailure("reason A");        // suppressed (no churn)
        m.ReportRegistrationFailure("reason B");        // 3
        m.MarkTunnelConnected();                        // 4
        m.MarkTunnelConnected();                        // suppressed (no churn)

        Assert.Equal(4, fired);
    }
}
