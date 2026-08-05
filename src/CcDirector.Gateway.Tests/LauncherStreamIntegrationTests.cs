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
/// the connection registry, and <c>GatewayHost.SendLauncherCommandAsync</c>'s connected-or-undeliverable
/// decision - the stream being the only path to a launcher since phase 6.
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
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
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

        await conn.InvokeAsync("Hello", new LauncherStreamHello { MachineName = Machine, Version = "test" });

        Assert.True(_gateway.LauncherConnections.IsStreamConnected(CcDirector.Core.Tenancy.TenantId.Local, Machine));
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
        await conn.InvokeAsync("Hello", new LauncherStreamHello { MachineName = Machine, Version = "test" });

        var result = await _gateway.SendLauncherCommandAsync(CcDirector.Core.Tenancy.TenantId.Local, Machine, new LauncherCommand { Verb = "director/restart" });

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
        await conn.InvokeAsync("Hello", new LauncherStreamHello { MachineName = Machine, Version = "test" });

        var result = await _gateway.SendLauncherCommandAsync(CcDirector.Core.Tenancy.TenantId.Local, Machine, new LauncherCommand { Verb = "bogus" });

        Assert.NotNull(result);
        Assert.False(result!.IsOk);
        Assert.Equal(LauncherCommandStatus.BadRequest, result.Status);
        Assert.Equal("unknown verb: bogus", result.Error);
    }

    [Fact]
    public async Task SendLauncherCommandAsync_ForOfflineMachine_ReturnsNull_WhichTheCallerReportsAsUndeliverable()
    {
        // No launcher has joined for this machine => no active stream connection => null. The stream is
        // the ONLY path to a launcher (phase 6 deleted the HTTP relay along with the launcher's listener),
        // so the caller turns this into a loud refusal - never a dial.
        var result = await _gateway.SendLauncherCommandAsync(CcDirector.Core.Tenancy.TenantId.Local, "no-such-machine", new LauncherCommand { Verb = "director/start" });
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

    /// <summary>
    /// SETTLING INSPECTION 3'S ONE UNPROVED HYPOTHESIS, BY INJECTION RATHER THAN BY ARGUMENT.
    ///
    /// The inspector read LauncherStreamClient.SuperviseAsync - Hello is sent once, then the client waits
    /// for the connection to CLOSE - and noted that SayHelloAsync catches every invocation failure while
    /// claiming auto-reconnect will retry, when retry only happens on Reconnected. A Hub or protocol error
    /// that leaves SignalR CONNECTED would therefore strand the launcher: stream open, never registered,
    /// no command deliverable, until some later disconnect. The inspector did not inject it, so it was
    /// recorded as a hypothesis rather than a defect.
    ///
    /// This is the injection. Hello is invoked with an argument the hub cannot bind, which is a protocol
    /// error and NOT one of the hub's own rejections - those call Context.Abort, which closes the
    /// connection and would send a real launcher round its reconnect loop. The result: the invocation
    /// fails, the connection stays Connected, and the machine is undeliverable.
    ///
    /// CONFIRMED: the hypothesis is real. This test therefore pins the SHAPE of the failure the client
    /// must survive - see LauncherStreamClient, which now retries Hello while connected instead of
    /// swallowing the failure and waiting for a disconnect that may never come.
    /// </summary>
    [Fact]
    public async Task A_failed_Hello_that_leaves_the_connection_up_registers_nothing_and_delivers_nothing()
    {
        await using var conn = await ConnectLauncherAsync(Token);
        conn.On<LauncherCommand, LauncherCommandResult>("Command", _ => Task.FromResult(LauncherCommandResult.Ok()));

        // A protocol-level failure, not a hub rejection: the hub never runs, so it never aborts.
        await Assert.ThrowsAnyAsync<Exception>(
            () => conn.InvokeAsync("Hello", "this is not a LauncherStreamHello"));

        // THE POINT: the connection is still up. Nothing here will ever fire Reconnected, so a client that
        // only retries Hello on reconnect never retries at all.
        Assert.Equal(HubConnectionState.Connected, conn.State);

        // And the launcher is invisible to commands - registered nowhere, deliverable nothing. From the
        // Gateway this is indistinguishable from a launcher that is not running.
        Assert.False(_gateway.LauncherConnections.IsStreamConnected(CcDirector.Core.Tenancy.TenantId.Local, Machine));
        Assert.Null(await _gateway.SendLauncherCommandAsync(
            CcDirector.Core.Tenancy.TenantId.Local, Machine, new LauncherCommand { Verb = "director/start" }));
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

}
