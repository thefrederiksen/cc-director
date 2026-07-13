using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end integration harness for launcher-persistent-join, the launcher twin of
/// <see cref="StreamIntegrationTests"/>. Boots a real GatewayHost with stream mode ON, dials the
/// <c>LauncherHub</c> at <c>/launcher-stream</c> with a real SignalR client, sends <c>Hello</c>, and asserts
/// the connection is registered. It then has the Gateway push a command DOWN the stream and asserts the
/// client's <c>Command</c> handler runs and returns a result over the same connection (SignalR client
/// results). This proves the whole join + command-dispatch path over the wire: hub auth, identity binding,
/// the connection registry, and <c>GatewayHost.SendLauncherCommandAsync</c>'s stream-vs-fallback decision.
/// </summary>
[Collection("DirectorRoot")]
public sealed class LauncherStreamIntegrationTests : IAsyncLifetime
{
    private const string Token = "test-token-launcher-join";
    private const string Machine = "launcher-test-machine";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-launcher-int-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;   // set in InitializeAsync

    public LauncherStreamIntegrationTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-launcher-int-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: AllocateFreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch (Exception) { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (Exception) { /* best effort */ }
    }

    [Fact]
    public async Task Hello_RegistersTheLauncherConnection()
    {
        await using var conn = await ConnectLauncherAsync(Token);
        conn.On<LauncherCommand, LauncherCommandResult>("Command", _ => Task.FromResult(LauncherCommandResult.Ok()));

        await conn.InvokeAsync("Hello", new LauncherStreamHello { MachineName = Machine, Port = 7878, Version = "test" });

        Assert.True(_gateway.LauncherConnections.IsStreamConnected(Machine));
    }

    [Fact]
    public async Task GatewayPushesCommand_DownTheStream_ClientHandlerRunsAndReturnsResult()
    {
        LauncherCommand? received = null;
        await using var conn = await ConnectLauncherAsync(Token);
        conn.On<LauncherCommand, LauncherCommandResult>("Command", cmd =>
        {
            received = cmd;
            return Task.FromResult(LauncherCommandResult.Ok());
        });
        await conn.InvokeAsync("Hello", new LauncherStreamHello { MachineName = Machine, Port = 7878, Version = "test" });

        var result = await _gateway.SendLauncherCommandAsync(Machine, new LauncherCommand { Verb = "director/restart" });

        Assert.NotNull(result);
        Assert.True(result!.IsOk);
        Assert.NotNull(received);
        Assert.Equal("director/restart", received!.Verb);
    }

    [Fact]
    public async Task ClientReturnsTypedFailure_IsReturnedAuthoritatively()
    {
        await using var conn = await ConnectLauncherAsync(Token);
        conn.On<LauncherCommand, LauncherCommandResult>("Command",
            _ => Task.FromResult(LauncherCommandResult.Fail(LauncherCommandStatus.BadRequest, "unknown verb: bogus")));
        await conn.InvokeAsync("Hello", new LauncherStreamHello { MachineName = Machine, Port = 7878, Version = "test" });

        var result = await _gateway.SendLauncherCommandAsync(Machine, new LauncherCommand { Verb = "bogus" });

        Assert.NotNull(result);
        Assert.False(result!.IsOk);
        Assert.Equal(LauncherCommandStatus.BadRequest, result.Status);
        Assert.Equal("unknown verb: bogus", result.Error);
    }

    [Fact]
    public async Task SendLauncherCommandAsync_ForOfflineMachine_ReturnsNull_ForRestFallback()
    {
        // No launcher has joined for this machine => no active stream connection => null, which the caller
        // treats as "no stream" and falls back to the existing HTTP relay.
        var result = await _gateway.SendLauncherCommandAsync("no-such-machine", new LauncherCommand { Verb = "director/start" });
        Assert.Null(result);
    }

    [Fact]
    public async Task UnauthenticatedConnect_IsRejected()
    {
        var conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/launcher-stream") // no token
            .Build();
        await Assert.ThrowsAnyAsync<Exception>(() => conn.StartAsync());
        await conn.DisposeAsync();
    }

    // Gateway Cleanup mission (the cut): the streamMode-OFF negative test was removed. The tunnel is now
    // MANDATORY - the LauncherHub is always mapped, there is no HTTP-fallback mode to keep it unmapped for.

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
