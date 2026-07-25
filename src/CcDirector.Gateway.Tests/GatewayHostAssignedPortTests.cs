using System.Net;
using System.Net.Sockets;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #2161 - the Gateway binds an operating-system-assigned port and reports back the number it got.
///
/// This replaces a pattern that was copied into 92 files: ask the operating system for a free port, RELEASE
/// it, then bind that number a moment later. In the gap the port is unheld, so anything on the machine can
/// take it, and the bind fails with "address already in use" - in a test that touched nothing. That is what
/// reddened whole suite runs. Assignment and bind now happen in one step, so there is no gap to lose.
///
/// The tests below pin the three things a caller depends on: the number comes back, it is the number the
/// host is actually reachable on, and two hosts started the same way never collide.
/// </summary>
public sealed class GatewayHostAssignedPortTests
{
    private static GatewayHost NewHost(string instancesDir) =>
        new(port: GatewayHost.OperatingSystemAssignedPort,
            token: "test-token-assigned-port",
            authEnabled: false,
            instancesDirectory: instancesDir);

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-assigned-port-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task StartAsync_WithAnAssignedPort_ReportsTheRealPortItBound()
    {
        var dir = TempDir();
        await using var gateway = NewHost(dir);

        await gateway.StartAsync();

        Assert.NotEqual(GatewayHost.OperatingSystemAssignedPort, gateway.Port);
        Assert.InRange(gateway.Port, 1, 65535);
    }

    /// <summary>
    /// The number must be the one the host is REACHABLE on. A port that is merely non-zero would satisfy the
    /// test above while pointing at nothing - and every caller builds its base address out of this value.
    /// </summary>
    [Fact]
    public async Task TheReportedPort_IsTheOneTheHostAnswersOn()
    {
        var dir = TempDir();
        await using var gateway = NewHost(dir);
        await gateway.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
        using var response = await http.GetAsync("healthz");

        Assert.True(response.IsSuccessStatusCode, $"the reported port {gateway.Port} did not answer /healthz");
    }

    /// <summary>
    /// The point of the whole change: two hosts started the same way, at the same time, land on different
    /// ports and both serve. Under the old probe-and-release pattern this is precisely the case that could
    /// hand the same number to both.
    /// </summary>
    [Fact]
    public async Task TwoHostsStartedTogether_GetDifferentPorts_AndBothServe()
    {
        await using var first = NewHost(TempDir());
        await using var second = NewHost(TempDir());

        await first.StartAsync();
        await second.StartAsync();

        Assert.NotEqual(first.Port, second.Port);

        using var firstHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{first.Port}/") };
        using var secondHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{second.Port}/") };
        Assert.True((await firstHttp.GetAsync("healthz")).IsSuccessStatusCode);
        Assert.True((await secondHttp.GetAsync("healthz")).IsSuccessStatusCode);
    }

    /// <summary>
    /// An explicitly chosen port is still honoured verbatim - the assigned-port mode is opt-in, and the
    /// shipped Gateway names its own port. This is the guard against "read back whatever we bound" quietly
    /// becoming "ignore what the caller asked for".
    /// </summary>
    [Fact]
    public async Task AnExplicitPort_IsHonouredUnchanged()
    {
        var chosen = ReserveAPortNobodyElseWillTake(out var reservation);
        using (reservation)
        {
            // held until the moment before we hand it over, which is the closest an explicit-port test can
            // safely get to "this port is free"
        }

        await using var gateway = new GatewayHost(port: chosen, token: "t", authEnabled: false,
            instancesDirectory: TempDir());
        await gateway.StartAsync();

        Assert.Equal(chosen, gateway.Port);
    }

    private static int ReserveAPortNobodyElseWillTake(out IDisposable reservation)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        reservation = new ListenerHandle(listener);
        return port;
    }

    private sealed class ListenerHandle(TcpListener listener) : IDisposable
    {
        public void Dispose() => listener.Stop();
    }
}
