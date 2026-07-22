using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Audit gap (audit-a/e): the <c>/launcher-stream</c> SignalR hub was the ONE launcher/machine writer #1917's
/// HTTP deny did not cover. The HTTP family (<c>/launchers</c> + <c>/machines</c>) is denied on hosted through
/// <see cref="HostedRouteDeny.ExclusiveGroup"/>, but the hub was mapped unconditionally in
/// <c>GatewayHost.ConfigurePipeline</c>. <c>LauncherHub.Hello</c> resolves NO tenant (unlike
/// <c>DirectorHub.Hello</c>, which aborts when the device key resolves to no tenant), and
/// <c>LauncherConnectionRegistry</c> keys one active connection per BARE machine name. So on hosted:
///
///   1. Tenant B connects a launcher for machine X -> <c>_byMachine[X] = connB</c>.
///   2. Tenant A connects a launcher and Hellos as the same machine name X -> <c>RegisterConnection</c>
///      overwrites <c>_byMachine[X] = connA</c>, SUPERSEDING tenant B's active connection across tenants.
///
/// There is no per-tenant ownership of a physical machine on shared hosted infrastructure - the same reason
/// #1917 chose a DENY over a partition. So the consistent close is to DENY the hub on hosted: on hosted the
/// hub is never mapped, so no launcher can join and no bare-machine-keyed connection row is ever written.
/// Self-host keeps the hub exactly as before.
///
/// <see cref="Cross_tenant_replacement_is_impossible_because_the_hub_is_denied_on_hosted"/> is the
/// reproduction turned revert-proof: on CURRENT main the two connections both join and the second replaces the
/// first (the collision); with the deny in place the connect itself fails, so the collision cannot occur.
/// Re-map the hub on hosted and this reddens. <see cref="The_hub_still_serves_on_self_host"/> is the control -
/// it stays green through that revert, because a hub that is denied EVERYWHERE would pass the deny test while
/// breaking self-host.
/// </summary>
[Collection("DirectorRoot")]
public sealed class HostedLauncherHubDenyTests : IAsyncLifetime
{
    private const string Token = "test-token-launcher-hub-deny";
    private const string Machine = "SHARED-MACHINE-X";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string? _priorHosted;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-launcher-hub-deny-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;

    public HostedLauncherHubDenyTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-launcher-hub-deny-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_gateway is not null)
            await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch (Exception) { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (Exception) { /* best effort */ }
    }

    private async Task StartGatewayAsync(bool hosted)
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", hosted ? "1" : "0");
        Assert.Equal(hosted, GatewayHostedMode.IsHosted);

        _gateway = new GatewayHost(port: AllocateFreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
    }

    /// <summary>
    /// THE REPRODUCTION, REVERT-PROOF. Two enrolled tenants each bind a device key, then each dials the
    /// launcher hub claiming the SAME machine name. On CURRENT main both would join and tenant A's Hello would
    /// replace tenant B's active connection in the bare-machine-keyed registry (cross-tenant supersession).
    /// With the hub denied on hosted, the connect fails at negotiate (the route is not mapped), so neither
    /// launcher ever joins and the registry stays empty - there is no shared row to collide on.
    ///
    /// Re-map <c>LauncherHub</c> on hosted (the revert) and the first connect succeeds instead of throwing,
    /// reddening this test.
    /// </summary>
    [Fact]
    public async Task Cross_tenant_replacement_is_impossible_because_the_hub_is_denied_on_hosted()
    {
        await StartGatewayAsync(hosted: true);

        // Two fully enrolled, tenant-bound device keys - the strongest callers hosted has. Neither may reach
        // the hub: a physical machine is not owned by a tenant on shared hardware.
        var keyB = _gateway.Devices.Register("dev-b", "MB").DeviceKey;
        var tenantB = _gateway.TenantRegistry.MintOrLookupBySubject("sub-bob", "bob@example.com");
        _gateway.Devices.SetAccountBinding("dev-b", "sub-bob", tenantB.Value);

        var keyA = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        var tenantA = _gateway.TenantRegistry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", tenantA.Value);

        // Tenant B's launcher cannot even establish the connection: /launcher-stream is not mapped on hosted,
        // so negotiate 404s and StartAsync throws. That is the whole collision closed at the transport.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var connB = await ConnectLauncherAsync(keyB);
        });

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var connA = await ConnectLauncherAsync(keyA);
        });

        // The registry was never written by either tenant - there is no bare-machine-keyed row to supersede.
        Assert.False(_gateway.LauncherConnections.IsStreamConnected(CcDirector.Core.Tenancy.TenantId.Local, Machine));
        Assert.Null(_gateway.LauncherConnections.GetActiveConnectionId(CcDirector.Core.Tenancy.TenantId.Local, Machine));
    }

    /// <summary>
    /// THE CONTROL. Off hosted the hub is mapped exactly as before: a launcher joins, Hello binds the machine,
    /// and the connection is registered. This stays green through the revert on
    /// <see cref="Cross_tenant_replacement_is_impossible_because_the_hub_is_denied_on_hosted"/> - a hub denied
    /// everywhere would pass the deny test while silently breaking self-host, so the deny must be proved to be
    /// scoped to hosted, not universal.
    /// </summary>
    [Fact]
    public async Task The_hub_still_serves_on_self_host()
    {
        await StartGatewayAsync(hosted: false);

        await using var conn = await ConnectLauncherAsync(Token);
        conn.On<LauncherCommand, LauncherCommandResult>("Command", _ => Task.FromResult(LauncherCommandResult.Ok()));

        await conn.InvokeAsync("Hello", new LauncherStreamHello { MachineName = Machine, Port = 7878, Version = "test" });

        Assert.True(_gateway.LauncherConnections.IsStreamConnected(CcDirector.Core.Tenancy.TenantId.Local, Machine));
    }

    private async Task<HubConnection> ConnectLauncherAsync(string token)
    {
        var conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/launcher-stream", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        await conn.StartAsync();
        return conn;
    }

    private static int AllocateFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
