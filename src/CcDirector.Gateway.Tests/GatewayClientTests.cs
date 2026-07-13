using System.Net;
using System.Net.Sockets;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end of the Director-side <see cref="GatewayClient"/> against a real Gateway.
/// We boot a Gateway on a free port, then drive the client directly (without booting a
/// full Director) so we can assert it does the register/heartbeat/unregister sequence.
/// </summary>
public sealed class GatewayClientTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;

    // Isolated discovery dir so a real Director running on the dev machine never appears
    // in this test Gateway's registry.
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: FreePort(), token: "", authEnabled: false,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    [Fact]
    public void Disabled_config_makes_client_inert()
    {
        // No gateway.url => no Start work, no errors, no entries.
        var client = new GatewayClient(new GatewayConfig(), Guid.NewGuid().ToString(), 7879, "1.0.0");
        client.Start();
        Assert.False(client.IsEnabled);
        Assert.False(client.IsRegistered);
        client.Dispose();
    }

    private static async Task WaitFor(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(50);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
