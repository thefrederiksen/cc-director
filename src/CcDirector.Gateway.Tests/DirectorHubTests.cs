using System.Security.Claims;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="DirectorHub"/> (issue #1176). The hub is driven directly against a fake
/// SignalR caller context, the real <see cref="PushedSessionStore"/>, and a real
/// <see cref="DirectorRegistry"/> rooted at a temp directory, so message handling and identity binding
/// are verified without the ASP.NET pipeline (that is the integration harness, a later increment).
/// </summary>
public sealed class DirectorHubTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DirectorRegistry _registry;
    private readonly PushedSessionStore _store;
    private readonly GatewayInputStatsAggregator _inputStats;
    private DateTime _now = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

    public DirectorHubTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-hub-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _registry = new DirectorRegistry(_tempDir);
        _store = new PushedSessionStore(() => _now);
        _inputStats = new GatewayInputStatsAggregator(Path.Combine(_tempDir, "input-stats.json"));
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception) { /* best-effort temp cleanup */ }
    }

    private (DirectorHub hub, FakeHubCallerContext ctx) NewHub(string connectionId)
    {
        var ctx = new FakeHubCallerContext(connectionId);
        var hub = new DirectorHub(_store, _registry, _inputStats) { Context = ctx };
        return (hub, ctx);
    }

    private static DirectorStreamHello Hello(string directorId) => new() { DirectorId = directorId, Version = "test" };

    private static SessionDto Session(string id, string state = "Working") => new() { SessionId = id, ActivityState = state };

    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(20);

    [Fact]
    public void Hello_BindsConnection_AndMarksStateReporting()
    {
        var (hub, ctx) = NewHub("conn-1");

        hub.Hello(Hello("dir-A"));

        Assert.False(ctx.Aborted);
        Assert.True(_registry.IsStateReporting("dir-A"));
        Assert.True(_store.IsStreamConnected("dir-A"));
    }

    [Fact]
    public void PushSnapshot_AfterHello_AppliesToBoundDirector()
    {
        var (hub, _) = NewHub("conn-1");
        hub.Hello(Hello("dir-A"));

        hub.PushSnapshot(0, new[] { Session("s1"), Session("s2") });

        var fresh = _store.TryGetFresh("dir-A", _staleAfter);
        Assert.NotNull(fresh);
        Assert.Equal(2, fresh.Count);
    }

    [Fact]
    public void Hello_WithEmptyDirectorId_AbortsAndDoesNotBind()
    {
        var (hub, ctx) = NewHub("conn-1");

        hub.Hello(Hello("   "));

        Assert.True(ctx.Aborted);
        Assert.False(_store.IsStreamConnected("dir-A"));
    }

    [Fact]
    public void PushSnapshot_BeforeHello_ThrowsHubException()
    {
        var (hub, _) = NewHub("conn-1");

        Assert.Throws<HubException>(() => hub.PushSnapshot(0, new[] { Session("s1") }));
    }

    [Fact]
    public void PushDelta_AfterHello_UpsertsSession()
    {
        var (hub, _) = NewHub("conn-1");
        hub.Hello(Hello("dir-A"));
        hub.PushSnapshot(1, new[] { Session("s1", "Working") });

        hub.PushDelta(2, Session("s1", "WaitingForInput"));

        var fresh = _store.TryGetFresh("dir-A", _staleAfter);
        Assert.NotNull(fresh);
        Assert.Single(fresh);
        Assert.Equal("WaitingForInput", fresh[0].ActivityState);
    }

    [Fact]
    public void RemoveSession_AfterHello_DropsSession()
    {
        var (hub, _) = NewHub("conn-1");
        hub.Hello(Hello("dir-A"));
        hub.PushSnapshot(1, new[] { Session("s1"), Session("s2") });

        hub.RemoveSession(2, "s1");

        var fresh = _store.TryGetFresh("dir-A", _staleAfter);
        Assert.NotNull(fresh);
        Assert.Single(fresh);
        Assert.Equal("s2", fresh[0].SessionId);
    }

    [Fact]
    public async Task OnDisconnected_UnregistersTheConnection()
    {
        var (hub, _) = NewHub("conn-1");
        hub.Hello(Hello("dir-A"));
        hub.PushSnapshot(0, new[] { Session("s1") });

        await hub.OnDisconnectedAsync(null);

        Assert.False(_store.IsStreamConnected("dir-A"));
        Assert.Null(_store.TryGetFresh("dir-A", _staleAfter));
    }

    [Fact]
    public void TwoConnectionsBoundToDifferentDirectors_DoNotCrossContaminate()
    {
        var (hubA, _) = NewHub("conn-1");
        var (hubB, _) = NewHub("conn-2");
        hubA.Hello(Hello("dir-A"));
        hubB.Hello(Hello("dir-B"));

        hubA.PushSnapshot(0, new[] { Session("a1"), Session("a2") });
        hubB.PushSnapshot(0, new[] { Session("b1") });

        var a = _store.TryGetFresh("dir-A", _staleAfter);
        var b = _store.TryGetFresh("dir-B", _staleAfter);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(2, a.Count);
        Assert.Single(b);
        Assert.Equal("b1", b[0].SessionId);
    }

    [Fact]
    public void RestartedDirector_SecondConnectionSameDirector_Reseeds()
    {
        var (hub1, _) = NewHub("conn-1");
        hub1.Hello(Hello("dir-A"));
        hub1.PushSnapshot(42, new[] { Session("old") });

        // Process restart: a brand-new connection for the same Director, sequence back at 0.
        var (hub2, _) = NewHub("conn-2");
        hub2.Hello(Hello("dir-A"));
        hub2.PushSnapshot(0, new[] { Session("fresh") });

        var fresh = _store.TryGetFresh("dir-A", _staleAfter);
        Assert.NotNull(fresh);
        Assert.Single(fresh);
        Assert.Equal("fresh", fresh[0].SessionId);
    }

    private sealed class FakeHubCallerContext : HubCallerContext
    {
        public FakeHubCallerContext(string connectionId) => ConnectionId = connectionId;

        public override string ConnectionId { get; }
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public bool Aborted { get; private set; }
        public override void Abort() => Aborted = true;
    }
}
