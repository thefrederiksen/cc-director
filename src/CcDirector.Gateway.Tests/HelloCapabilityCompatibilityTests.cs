using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The capability handshake's compatibility claim, proved over a REAL SignalR connection rather than
/// asserted in a comment (#2457, #2459).
///
/// The handshake rests entirely on one property of SignalR: that a client invoking a hub method
/// GENERICALLY - <c>InvokeAsync&lt;GatewayCapabilities?&gt;("Hello", ...)</c> - against a Gateway whose
/// Hello returns NOTHING gets null back, and does not fail. Every part of the design leans on it. Null
/// is how a Director recognises a Gateway older than itself, and it is why no version negotiation, no
/// second round trip, and no new hub method were needed.
///
/// If that property does not hold, this feature is not merely useless - it is catastrophic, and in the
/// worst possible place. Hello is the FIRST message on the tunnel, and the Director sends it on every
/// connect and every reconnect. A generic invoke that threw against an older Gateway would fail the
/// reseed, take the roster push down with it, and do so on exactly the deployment - new Director, old
/// Gateway - that this whole change exists to make survivable. It would turn a Gateway that refuses
/// session keys into a Gateway a Director cannot talk to at all.
///
/// That is far too much weight for an assertion about someone else's framework, so it is tested here,
/// in the suite that stands up a genuine hub and dials it with a genuine client. Both directions:
/// new client against old server, and old client against new server.
///
/// The hubs below are deliberately hand-written stand-ins rather than the real DirectorHub. The point
/// is the SHAPE of the method - returns nothing versus returns a value - and a stand-in states that
/// shape in one line, with no identity binding, tenant resolution or registry to satisfy first.
/// </summary>
public sealed class HelloCapabilityCompatibilityTests : IAsyncLifetime
{
    /// <summary>A Gateway from before this work: Hello returns NOTHING.</summary>
    private sealed class OldShapeHub : Hub
    {
        public void Hello(DirectorStreamHello hello) { _ = hello; }
    }

    /// <summary>A Gateway with this work: Hello returns its capabilities.</summary>
    private sealed class NewShapeHub : Hub
    {
        public GatewayCapabilities Hello(DirectorStreamHello hello)
        {
            _ = hello;
            return new GatewayCapabilities
            {
                Version = "1.9.11",
                Commit = "abc1234",
                HubMethods = new List<string> { "Hello", "RegisterSessionKey" },
            };
        }
    }

    private WebApplication _old = null!;
    private WebApplication _new = null!;

    public async Task InitializeAsync()
    {
        _old = await StartAsync<OldShapeHub>();
        _new = await StartAsync<NewShapeHub>();
    }

    public async Task DisposeAsync()
    {
        await _old.StopAsync();
        await _old.DisposeAsync();
        await _new.StopAsync();
        await _new.DisposeAsync();
    }

    private static async Task<WebApplication> StartAsync<THub>() where THub : Hub
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        var app = builder.Build();
        app.MapHub<THub>("/director-stream");
        await app.StartAsync();
        return app;
    }

    private static string UrlOf(WebApplication app)
    {
        var address = app.Urls.First();
        return $"{address}/director-stream";
    }

    private static async Task<HubConnection> ConnectAsync(WebApplication app)
    {
        var conn = new HubConnectionBuilder().WithUrl(UrlOf(app)).Build();
        await conn.StartAsync();
        return conn;
    }

    private static DirectorStreamHello AnyHello() => new()
    {
        DirectorId = "director-under-test",
        Version = "1.9.11",
        MachineName = "test-machine",
        User = "test-user",
        Pid = 1234,
        StartedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task NewDirector_AgainstAnOldGateway_GetsNullRatherThanAFailure()
    {
        // THE claim the whole handshake rests on. If this ever starts throwing, the Director cannot
        // complete Hello against any Gateway older than itself, and the tunnel is dead on the exact
        // deployment this change exists to survive.
        await using var conn = await ConnectAsync(_old);

        var capabilities = await conn.InvokeAsync<GatewayCapabilities?>("Hello", AnyHello());

        Assert.Null(capabilities);
        Assert.Equal(HubConnectionState.Connected, conn.State);
    }

    [Fact]
    public async Task NewDirector_AgainstANewGateway_ReadsTheCapabilities()
    {
        await using var conn = await ConnectAsync(_new);

        var capabilities = await conn.InvokeAsync<GatewayCapabilities?>("Hello", AnyHello());

        Assert.NotNull(capabilities);
        Assert.Equal("1.9.11", capabilities!.Version);
        Assert.Equal("abc1234", capabilities.Commit);
        Assert.Contains("RegisterSessionKey", capabilities.HubMethods);
    }

    [Fact]
    public async Task OldDirector_AgainstANewGateway_IsUnaffectedByTheReturnValue()
    {
        // The other direction, and it is not hypothetical: every Director already in the field invokes
        // Hello non-generically. Giving the hub a return type must not disturb them, or deploying the
        // Gateway would break the fleet it is meant to repair.
        await using var conn = await ConnectAsync(_new);

        await conn.InvokeAsync("Hello", AnyHello());

        Assert.Equal(HubConnectionState.Connected, conn.State);
    }

    [Fact]
    public async Task OldDirector_AgainstAnOldGateway_StillWorks()
    {
        // The control. Without it, a failure in the three tests above could be read as "this harness
        // cannot invoke Hello at all" rather than as the compatibility break it would actually be.
        await using var conn = await ConnectAsync(_old);

        await conn.InvokeAsync("Hello", AnyHello());

        Assert.Equal(HubConnectionState.Connected, conn.State);
    }
}
