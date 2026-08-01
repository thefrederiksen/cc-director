using System.Security.Claims;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Snooze;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
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
    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();
    private DateTime _now = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

    private SnoozeRegistry NewReg() => new(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));

    public DirectorHubTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-hub-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _registry = new DirectorRegistry(_tempDir);
        _store = new PushedSessionStore(() => _now);
        _inputStats = new GatewayInputStatsAggregator(Path.Combine(_tempDir, "gateway-stats.db"));
    }

    public void Dispose()
    {
        _registry.Dispose();
        _h.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception) { /* best-effort temp cleanup */ }
    }

    // The boundary is required and non-nullable now (finding I1-01). These are self-host hub tests, so they
    // get the REAL self-host boundary: built over the SingleTenantContext, it always resolves Local.
    private static CcDirector.Gateway.Tenancy.HostedTenantBoundary SelfHostBoundary() =>
        new(new CcDirector.Core.Tenancy.SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry());

    private (DirectorHub hub, FakeHubCallerContext ctx) NewHub(string connectionId)
    {
        var ctx = new FakeHubCallerContext(connectionId);
        var hub = new DirectorHub(_store, _registry, InputStatsHandle.Available(_inputStats), new GatewayStreamRegistry(), SelfHostBoundary()) { Context = ctx };
        return (hub, ctx);
    }

    private (DirectorHub hub, FakeHubCallerContext ctx) NewHub(string connectionId, SnoozeLandingObserver snooze)
    {
        var ctx = new FakeHubCallerContext(connectionId);
        var hub = new DirectorHub(_store, _registry, InputStatsHandle.Available(_inputStats), new GatewayStreamRegistry(), SelfHostBoundary(), snoozeLandings: snooze) { Context = ctx };
        return (hub, ctx);
    }

    // ---------- F1: a REJECTED push must not drive the snooze observer ----------
    // The observer's edges MUTATE the authoritative registry - ClearIfArmed deletes an armed snooze, Land
    // converts a deferral - so a push the store rejected as non-authoritative (from a superseded connection,
    // or a stale sequence) must not reach it. A single SnoozeRegistry + SnoozeLandingObserver is shared by
    // both hubs, exactly as the Gateway wires one singleton across every connection.

    [Fact]
    public void ARejectedWorkingPushFromASupersededConnection_DoesNotDeleteAnArmedSnooze()
    {
        var reg = NewReg();
        var obs = new SnoozeLandingObserver(reg, () => _now);

        var (hub1, _) = NewHub("conn-1", obs);
        hub1.Hello(Hello("dir-A"));
        hub1.PushSnapshot(1, new[] { Session("s1", "WaitingForInput") });
        reg.Snooze("s1", _now.AddHours(12), "dir-A"); // armed, running clock

        // A replacement connection supersedes conn-1 (a reconnect). conn-1 is no longer authoritative.
        var (hub2, _) = NewHub("conn-2", obs);
        hub2.Hello(Hello("dir-A"));

        // A late Working push arrives on the SUPERSEDED conn-1. The store rejects it - and it must not reach
        // the working edge, or it would delete a snooze the current connection owns.
        hub1.PushDelta(2, Session("s1", "Working"));

        Assert.True(reg.Contains("s1")); // the armed snooze survived a non-authoritative push
    }

    [Fact]
    public void ARejectedSettledPushFromASupersededConnection_DoesNotLandADeferral()
    {
        var reg = NewReg();
        var obs = new SnoozeLandingObserver(reg, () => _now);

        var (hub1, _) = NewHub("conn-1", obs);
        hub1.Hello(Hello("dir-A"));
        hub1.PushSnapshot(1, new[] { Session("s1", "Working") });
        reg.SnoozeDeferred("s1", 720, "dir-A"); // "snooze me when I finish" - not landed yet

        var (hub2, _) = NewHub("conn-2", obs);
        hub2.Hello(Hello("dir-A")); // supersede conn-1

        // A late settled push on the superseded conn-1 must not LAND the deferral: the authoritative session
        // is still working, and a stale landing plus a later working push is how a deferral gets lost.
        hub1.PushDelta(2, Session("s1", "WaitingForInput"));

        Assert.True(Assert.Single(reg.Entries()).IsDeferred); // still deferred, not armed
    }

    [Fact]
    public void ARejectedSnapshotFromASupersededConnection_DoesNotDeleteAnArmedSnooze()
    {
        // Inspection round 2, finding 4: the SNAPSHOT path is gated on ApplySnapshot acceptance too, not
        // only the delta path. Reconnect and periodic reseeds arrive as PushSnapshot, so a rejected/stale
        // snapshot must not reach ObserveSnapshot and delete a snooze the current connection owns.
        var reg = NewReg();
        var obs = new SnoozeLandingObserver(reg, () => _now);

        var (hub1, _) = NewHub("conn-1", obs);
        hub1.Hello(Hello("dir-A"));
        hub1.PushSnapshot(1, new[] { Session("s1", "WaitingForInput") });
        reg.Snooze("s1", _now.AddHours(12), "dir-A"); // armed

        var (hub2, _) = NewHub("conn-2", obs);
        hub2.Hello(Hello("dir-A")); // supersede conn-1

        // A late full SNAPSHOT on the superseded conn-1 is rejected by ApplySnapshot; it must not reach the
        // working edge through ObserveSnapshot.
        hub1.PushSnapshot(2, new[] { Session("s1", "Working") });

        Assert.True(reg.Contains("s1")); // the armed snooze survived a rejected snapshot
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
        Assert.True(_store.IsStreamConnected(TenantId.Local, "dir-A"));
    }

    [Fact]
    public void PushSnapshot_AfterHello_AppliesToBoundDirector()
    {
        var (hub, _) = NewHub("conn-1");
        hub.Hello(Hello("dir-A"));

        hub.PushSnapshot(0, new[] { Session("s1"), Session("s2") });

        var fresh = _store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter);
        Assert.NotNull(fresh);
        Assert.Equal(2, fresh.Count);
    }

    [Fact]
    public void Hello_WithEmptyDirectorId_AbortsAndDoesNotBind()
    {
        var (hub, ctx) = NewHub("conn-1");

        hub.Hello(Hello("   "));

        Assert.True(ctx.Aborted);
        Assert.False(_store.IsStreamConnected(TenantId.Local, "dir-A"));
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

        var fresh = _store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter);
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

        var fresh = _store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter);
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

        Assert.False(_store.IsStreamConnected(TenantId.Local, "dir-A"));
        Assert.Null(_store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter));
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

        var a = _store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter);
        var b = _store.TryGetFresh(TenantId.Local, "dir-B", _staleAfter);
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

        var fresh = _store.TryGetFresh(TenantId.Local, "dir-A", _staleAfter);
        Assert.NotNull(fresh);
        Assert.Single(fresh);
        Assert.Equal("fresh", fresh[0].SessionId);
    }

    private sealed class FakeHubCallerContext : HubCallerContext
    {
        public FakeHubCallerContext(string connectionId)
        {
            ConnectionId = connectionId;
            // The hub resolves its connection tenant through the boundary, which on self-host answers Local
            // for any request - but only when the connection HAS an HttpContext, exactly as a real SignalR
            // negotiate does. The fake used to carry none, which was invisible while these tests passed a
            // null boundary; with the REAL self-host boundary (finding I1-01) an absent HttpContext is an
            // unresolvable connection and Hello would abort.
            Features.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(
                new HttpContextFeatureImpl { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() });
        }

        private sealed class HttpContextFeatureImpl : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
        {
            public Microsoft.AspNetCore.Http.HttpContext? HttpContext { get; set; }
        }

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
