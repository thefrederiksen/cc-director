using System.Diagnostics;
using CcDirector.Gateway.Tray;
using Xunit;

namespace CcDirector.Gateway.Tests.Tray;

/// <summary>
/// Tests the Gateway tray flyout cache (issue #855): the pure, thread-safe holder the tray controller's
/// background heartbeats fill so the left-click flyout open path never does a synchronous registry read
/// or tailscale CLI probe. Proves the "..." placeholder behavior before the first heartbeat resolves a
/// value, and that resolved values (including a null front-door URL when Tailscale is unavailable) are
/// surfaced as-is - all without an Avalonia UI thread.
/// </summary>
public sealed class GatewayTrayFlyoutCacheTests
{
    [Fact]
    public void DirectorCountDisplay_BeforeFirstHeartbeat_ReturnsPlaceholder()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();

        // Act
        var display = cache.DirectorCountDisplay;

        // Assert - never resolved yet, so the flyout shows the benign placeholder, not "0"
        Assert.Equal(GatewayTrayFlyoutCache.Placeholder, display);
    }

    [Fact]
    public void DirectorCountDisplay_AfterSetDirectorCount_ReturnsCount()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();

        // Act
        cache.SetDirectorCount(3);

        // Assert
        Assert.Equal("3", cache.DirectorCountDisplay);
    }

    [Fact]
    public void DirectorCountDisplay_AfterSetZero_ReturnsZeroNotPlaceholder()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();

        // Act - a resolved count of zero is a real value, distinct from "not yet resolved"
        cache.SetDirectorCount(0);

        // Assert
        Assert.Equal("0", cache.DirectorCountDisplay);
    }

    [Fact]
    public void DirectorCountDisplay_AfterSecondSet_ReflectsLatestValue()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();
        cache.SetDirectorCount(2);

        // Act - a later heartbeat updates the cache (device joined)
        cache.SetDirectorCount(5);

        // Assert
        Assert.Equal("5", cache.DirectorCountDisplay);
    }

    [Fact]
    public void FrontDoorBaseUrl_BeforeFirstHeartbeat_IsNull()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();

        // Act + Assert - unresolved is null, which the Open Cockpit action treats as "refuse"
        Assert.Null(cache.FrontDoorBaseUrl);
    }

    [Fact]
    public void FrontDoorBaseUrl_AfterSet_ReturnsUrl()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();

        // Act
        cache.SetFrontDoorBaseUrl("https://machine-a.tail0123.ts.net");

        // Assert
        Assert.Equal("https://machine-a.tail0123.ts.net", cache.FrontDoorBaseUrl);
    }

    [Fact]
    public void FrontDoorBaseUrl_SetNullWhenTailscaleUnavailable_StaysNull()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();
        cache.SetFrontDoorBaseUrl("https://machine-a.tail0123.ts.net");

        // Act - a later heartbeat finds Tailscale unavailable
        cache.SetFrontDoorBaseUrl(null);

        // Assert
        Assert.Null(cache.FrontDoorBaseUrl);
    }

    [Fact]
    public void CockpitStatusDisplay_BeforeFirstHeartbeat_ReturnsPlaceholder()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();

        // Act + Assert - never probed yet, so the flyout shows the benign placeholder
        Assert.Equal(GatewayTrayFlyoutCache.Placeholder, cache.CockpitStatusDisplay);
    }

    [Fact]
    public void CockpitStatusDisplay_AfterReachableProbe_SaysReachableWithPort()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();

        // Act
        cache.SetCockpitStatus(reachable: true, port: 7470);

        // Assert
        Assert.Equal("reachable on :7470", cache.CockpitStatusDisplay);
    }

    [Fact]
    public void CockpitStatusDisplay_AfterUnreachableProbe_SaysNotReachableWithPort()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();
        cache.SetCockpitStatus(reachable: true, port: 7470);

        // Act - a later heartbeat finds the Cockpit down
        cache.SetCockpitStatus(reachable: false, port: 7470);

        // Assert
        Assert.Equal("not reachable on :7470", cache.CockpitStatusDisplay);
    }

    [Fact]
    public void BrainSummaryDisplay_BeforeFirstHeartbeat_ReturnsPlaceholder()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();

        // Act + Assert
        Assert.Equal(GatewayTrayFlyoutCache.Placeholder, cache.BrainSummaryDisplay);
    }

    [Fact]
    public void BrainSummaryDisplay_AfterSet_ReturnsSummary()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();

        // Act
        cache.SetBrainSummary("not started (spawns on first use)");

        // Assert
        Assert.Equal("not started (spawns on first use)", cache.BrainSummaryDisplay);
    }

    [Fact]
    public void FleetLines_BeforeFirstHeartbeat_IsEmpty()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();

        // Act + Assert - never resolved yet, so the flyout renders no Fleet section
        Assert.Empty(cache.FleetLines);
    }

    [Fact]
    public void FleetLines_AfterSetFleet_ReturnsLines()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();

        // Act
        cache.SetFleet(new[] { new FleetLine("MACHINE_A", "v0.9.32, seen just now") });

        // Assert
        var line = Assert.Single(cache.FleetLines);
        Assert.Equal("MACHINE_A", line.Label);
        Assert.Equal("v0.9.32, seen just now", line.Value);
    }

    [Fact]
    public void DevicesDisplay_CoversPlaceholderZeroOneAndMany()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();

        // Act + Assert - placeholder before the first heartbeat, then real counts
        Assert.Equal(GatewayTrayFlyoutCache.Placeholder, cache.DevicesDisplay);
        cache.SetDeviceCount(0);
        Assert.Equal("none paired", cache.DevicesDisplay);
        cache.SetDeviceCount(1);
        Assert.Equal("1 paired", cache.DevicesDisplay);
        cache.SetDeviceCount(3);
        Assert.Equal("3 paired", cache.DevicesDisplay);
    }

    [Fact]
    public void MachinesDisplay_CoversPlaceholderOneAndMany()
    {
        // Arrange
        var cache = new GatewayTrayFlyoutCache();

        // Act + Assert
        Assert.Equal(GatewayTrayFlyoutCache.Placeholder, cache.MachinesDisplay);
        cache.SetMachineCount(1);
        Assert.Equal("1 online", cache.MachinesDisplay);
        cache.SetMachineCount(2);
        Assert.Equal("2 online", cache.MachinesDisplay);
    }

    [Fact]
    public void DescribeDirector_WithVersionAndLastSeen_FormatsBoth()
    {
        // Arrange
        var now = new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);

        // Act
        var text = GatewayTrayFlyoutCache.DescribeDirector(
            "0.9.32+abcdef", now.AddSeconds(-42), advertisedEndpointState: null, now);

        // Assert - the +githash is trimmed, the age is short-form
        Assert.Equal("v0.9.32, seen 42s ago", text);
    }

    [Fact]
    public void DescribeDirector_UnreachableEndpoint_AppendsWarning()
    {
        // Arrange
        var now = new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);

        // Act
        var text = GatewayTrayFlyoutCache.DescribeDirector(
            "0.9.32", now.AddMinutes(-3),
            advertisedEndpointState: CcDirector.Gateway.Contracts.DirectorDto.EndpointStateUnreachableByName, now);

        // Assert
        Assert.Equal("v0.9.32, seen 3m ago, endpoint unreachable", text);
    }

    [Fact]
    public void DescribeDirector_NoVersionNoLastSeen_SaysUnknownVersion()
    {
        // Arrange
        var now = new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);

        // Act
        var text = GatewayTrayFlyoutCache.DescribeDirector("", lastSeenUtc: null, advertisedEndpointState: null, now);

        // Assert
        Assert.Equal("unknown version", text);
    }

    [Fact]
    public void AgeText_CoversAllBrackets()
    {
        // Act + Assert
        Assert.Equal("just now", GatewayTrayFlyoutCache.AgeText(TimeSpan.FromSeconds(2)));
        Assert.Equal("42s ago", GatewayTrayFlyoutCache.AgeText(TimeSpan.FromSeconds(42)));
        Assert.Equal("5m ago", GatewayTrayFlyoutCache.AgeText(TimeSpan.FromMinutes(5)));
        Assert.Equal("3h ago", GatewayTrayFlyoutCache.AgeText(TimeSpan.FromHours(3.4)));
    }

    [Fact]
    public async Task Reads_AreInstant_WhileASlowProbeRunsInTheBackground()
    {
        // Acceptance criterion 2 at the mechanism level: the flyout open path reads ONLY these cached
        // getters, while the slow tailscale probe runs entirely BEFORE SetFrontDoorBaseUrl on a
        // background heartbeat. So a flyout-style read must return effectively instantly even while a
        // multi-second "probe" is in flight - it must never wait for the probe.

        // Arrange - a background task simulates a slow (500ms) front-door probe, then publishes.
        var cache = new GatewayTrayFlyoutCache();
        cache.SetDirectorCount(1);
        var slowProbe = Task.Run(() =>
        {
            Thread.Sleep(500); // stand in for the blocking tailscale CLI probe
            cache.SetFrontDoorBaseUrl("https://machine-a.tail0123.ts.net");
        });

        // Act - read the cache the way BuildFlyoutModel / OpenCockpit do, while the probe is mid-flight.
        var sw = Stopwatch.StartNew();
        _ = cache.DirectorCountDisplay;
        _ = cache.FrontDoorBaseUrl;
        sw.Stop();

        // Assert - the read did not block on the 500ms probe (generous 100ms bar to avoid flakiness).
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"flyout-style read should be instant, took {sw.ElapsedMilliseconds}ms");
        await slowProbe;
        Assert.Equal("https://machine-a.tail0123.ts.net", cache.FrontDoorBaseUrl);
    }
}
