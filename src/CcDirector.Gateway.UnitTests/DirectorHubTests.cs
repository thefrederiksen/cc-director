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

    private (DirectorHub hub, FakeHubCallerContext ctx) NewHub(string connectionId, CcDirector.Gateway.History.SessionTurnStore turns)
    {
        var ctx = new FakeHubCallerContext(connectionId);
        var hub = new DirectorHub(_store, _registry, InputStatsHandle.Available(_inputStats), new GatewayStreamRegistry(), SelfHostBoundary(), sessionTurns: turns) { Context = ctx };
        return (hub, ctx);
    }

    // ---------- Turn push (the turn-push mission, phase 1): PushTurns stores, Hello hands back the watermarks ----------

    private static TurnPushBatch Turns(string sid, string generation, int start, int count) => new()
    {
        SessionId = sid,
        Generation = generation,
        GenerationStartedUtc = new DateTime(2026, 9, 1, 20, 0, 0, DateTimeKind.Utc),
        Agent = "ClaudeCode",
        StartOrdinal = start,
        TotalCount = start + count,
        Turns = Enumerable.Range(start, count).Select(i => new PushedTurn
        {
            Ordinal = i,
            Role = i % 2 == 0 ? "User" : "Assistant",
            Parts = { new HistoryPartDto { Kind = "Text", Text = "m" + i } },
        }).ToList(),
    };

    [Fact]
    public void PushTurns_AfterHello_StoresTheTurns_AndAnswersTheWatermark()
    {
        var turns = new CcDirector.Gateway.History.SessionTurnStore(Db);
        var (hub, _) = NewHub("conn-1", turns);
        hub.Hello(Hello("dir-A"));

        var mark = hub.PushTurns(1, Turns("s1", "gen-a", 0, 3));

        Assert.NotNull(mark);
        Assert.Equal(3, mark.Count);
        var stored = turns.ReadCurrent("s1");
        Assert.NotNull(stored);
        Assert.Equal(new[] { "m0", "m1", "m2" }, stored.Value.Messages.Select(m => m.Parts[0].Text));
        Assert.Equal("dir-A", stored.Value.Head.DirectorId);   // attributed to the BOUND Director, not to anything in the batch
    }

    [Fact]
    public void PushTurns_AMalformedBatch_IsRefused_AndNothingIsStored()
    {
        var turns = new CcDirector.Gateway.History.SessionTurnStore(Db);
        var (hub, _) = NewHub("conn-1", turns);
        hub.Hello(Hello("dir-A"));
        var bad = Turns("s1", "gen-a", 0, 2);
        bad.Turns[1].Ordinal = 9;   // disagrees with its own position

        Assert.Null(hub.PushTurns(1, bad));
        Assert.Null(turns.ReadCurrent("s1"));
    }

    [Fact]
    public void Hello_HandsBackTheWatermarks_ForThisDirectorsSessionsOnly()
    {
        var turns = new CcDirector.Gateway.History.SessionTurnStore(Db);
        var (hubA, _) = NewHub("conn-1", turns);
        hubA.Hello(Hello("dir-A"));
        hubA.PushTurns(1, Turns("s1", "gen-a", 0, 2));
        hubA.PushTurns(2, Turns("s2", "gen-b", 0, 5));
        var (hubB, _) = NewHub("conn-2", turns);
        hubB.Hello(Hello("dir-B"));
        hubB.PushTurns(1, Turns("s3", "gen-c", 0, 1));

        var again = hubA.Hello(Hello("dir-A"));

        Assert.NotNull(again);
        var marks = again.TurnWatermarks.OrderBy(m => m.SessionId).ToList();
        Assert.Equal(new[] { "s1", "s2" }, marks.Select(m => m.SessionId));
        Assert.Equal(new[] { 2, 5 }, marks.Select(m => m.Count));
        Assert.Equal(new[] { "gen-a", "gen-b" }, marks.Select(m => m.Generation));
        Assert.Contains("PushTurns", again.HubMethods);
        Assert.True(again.TurnWatermarksKnown);
    }

    [Fact]
    public void Hello_WithNothingStoredForThisDirector_SaysSo_RatherThanStayingSilent()
    {
        // An empty list that the Gateway VOUCHES for is a fact the Director acts on: it pushes every
        // conversation from the start. A Gateway with no store at all must not look like that.
        var turns = new CcDirector.Gateway.History.SessionTurnStore(Db);
        var (withStore, _) = NewHub("conn-1", turns);
        var answered = withStore.Hello(Hello("dir-A"));

        Assert.NotNull(answered);
        Assert.Empty(answered.TurnWatermarks);
        Assert.True(answered.TurnWatermarksKnown);

        var (noStore, _) = NewHub("conn-2");
        var silent = noStore.Hello(Hello("dir-B"));

        Assert.NotNull(silent);
        Assert.Empty(silent.TurnWatermarks);
        Assert.False(silent.TurnWatermarksKnown);
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

    // ---------- The clean-shutdown farewell reaches DISCOVERY, not only work history ----------
    // It used to tell the history recorder alone, so the registry went on expecting a Director that had
    // announced it was leaving - and reported it unreachable until the 24-hour eviction horizon swept it.
    // Revert-prove: delete the MarkStopped line from DirectorHub.DirectorStopping and the first of these
    // goes red at the stamp, after its positive control has passed.

    [Fact]
    public void TheFarewell_RetiresTheRegistration()
    {
        var (hub, _) = NewHub("conn-1");
        hub.Hello(Hello("dir-A"));
        // Positive control: registered and RUNNING before the goodbye, so the assertion below cannot pass
        // against an entry that was never created.
        Assert.Null(_registry.Get(CcDirector.Core.Tenancy.TenantId.Local, "dir-A")!.StoppedAtUtc);

        hub.DirectorStopping();

        Assert.NotNull(_registry.Get(CcDirector.Core.Tenancy.TenantId.Local, "dir-A")!.StoppedAtUtc);
    }

    /// <summary>
    /// A DISCONNECT IS NOT A GOODBYE. Only an explicit farewell retires a registration; a Director that dies
    /// without one - a force-kill, a crash, a power cut - stays "expected", which is exactly the case the
    /// owner needs reported as unreachable. Losing this distinction would silence the real fault along with
    /// the false one.
    /// </summary>
    [Fact]
    public async Task ADisconnectWithoutAFarewell_LeavesTheRegistrationExpected()
    {
        var (hub, _) = NewHub("conn-1");
        hub.Hello(Hello("dir-A"));

        await hub.OnDisconnectedAsync(new Exception("the machine went away"));

        Assert.Null(_registry.Get(CcDirector.Core.Tenancy.TenantId.Local, "dir-A")!.StoppedAtUtc);
    }

    /// <summary>
    /// A DELAYED FAREWELL FROM A SUPERSEDED CONNECTION MUST NOT RETIRE THE LIVE REGISTRATION. Found by
    /// review. conn-1 says Hello as dir-A; conn-2 reconnects and supersedes it; conn-1's farewell then
    /// arrives late. Acting on it would stamp the registry row that now represents conn-2 - and when THAT
    /// Director later died for real, its crash would read as an orderly shutdown for the whole eviction
    /// horizon, silencing exactly the fault the state exists to keep visible.
    ///
    /// Revert-prove: drop the IsActiveConnection guard in DirectorStopping and this goes red at the final
    /// assertion, after its two positive controls have passed.
    /// </summary>
    [Fact]
    public void AFarewellFromASupersededConnection_DoesNotRetireTheLiveRegistration()
    {
        var (hub1, _) = NewHub("conn-1");
        hub1.Hello(Hello("dir-A"));

        var (hub2, _) = NewHub("conn-2");
        hub2.Hello(Hello("dir-A"));   // the reconnect supersedes conn-1

        // Positive control: the registration exists and is running.
        Assert.Null(_registry.Get(CcDirector.Core.Tenancy.TenantId.Local, "dir-A")!.StoppedAtUtc);

        hub1.DirectorStopping();      // the late farewell, on the connection that is no longer active

        Assert.Null(_registry.Get(CcDirector.Core.Tenancy.TenantId.Local, "dir-A")!.StoppedAtUtc);

        // Positive control on the guard: the CURRENT connection's farewell still works, so the assertion
        // above is not passing because the whole path is broken.
        hub2.DirectorStopping();
        Assert.NotNull(_registry.Get(CcDirector.Core.Tenancy.TenantId.Local, "dir-A")!.StoppedAtUtc);
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
