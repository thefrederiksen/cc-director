using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="FleetRosterCache"/> - the last-known-good roster grace window (issue #1215,
/// Cockpit plan phase 6). Each test drives an injected clock so the last-seen and idle-clock behaviour is
/// deterministic. The three named transitions the issue calls out are covered explicitly:
/// reachable -> wobbly -> reachable, reachable -> wobbly -> offline, and offline -> reachable.
/// </summary>
public sealed class FleetRosterCacheTests
{
    private DateTime _now = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

    private FleetRosterCache NewCache() => new(() => _now);

    private static SessionDto Session(string id, string state = "Working", DateTime? lastActivity = null) => new()
    {
        SessionId = id,
        ActivityState = state,
        LastActivityAt = lastActivity,
    };

    [Fact]
    public void RecordReachable_StoresSnapshot_ReadsOnline()
    {
        // Arrange
        var cache = NewCache();

        // Act
        var projection = cache.RecordReachable(TenantId.Local, "dir-A", new[] { Session("s1"), Session("s2") });

        // Assert
        Assert.Equal(FleetReachabilityState.Online, projection.State);
        Assert.Equal(_now, projection.LastSeenUtc);
        Assert.Equal(0, projection.LastSeenAgeSeconds);
        Assert.Null(projection.StaleSessions);
    }

    [Fact]
    public void ReachableThenWobblyThenReachable_AbsorbsTransientMiss()
    {
        // Arrange
        var cache = NewCache();
        cache.RecordReachable(TenantId.Local, "dir-A", new[] { Session("s1"), Session("s2") });
        var seenAt = _now;

        // Act - one failed poll cycle inside the grace window
        _now = _now.AddSeconds(5);
        var wobbly = cache.RecordUnreachable(TenantId.Local, "dir-A", "timeout");

        // Assert - served stale, nothing dropped, marked with the last-seen time and age
        Assert.Equal(FleetReachabilityState.Wobbly, wobbly.State);
        Assert.NotNull(wobbly.StaleSessions);
        Assert.Equal(2, wobbly.StaleSessions!.Count);
        Assert.Contains(wobbly.StaleSessions, s => s.SessionId == "s1");
        Assert.Contains(wobbly.StaleSessions, s => s.SessionId == "s2");
        Assert.Equal(seenAt, wobbly.LastSeenUtc);
        Assert.Equal(5, wobbly.LastSeenAgeSeconds);

        // Act - the Director answers again
        _now = _now.AddSeconds(5);
        var backOnline = cache.RecordReachable(TenantId.Local, "dir-A", new[] { Session("s1") });

        // Assert - back to Online, streak reset, last-seen refreshed
        Assert.Equal(FleetReachabilityState.Online, backOnline.State);
        Assert.Equal(_now, backOnline.LastSeenUtc);
        Assert.Equal(0, backOnline.LastSeenAgeSeconds);
    }

    [Fact]
    public void ReachableThenWobblyThenOffline_TransitionsOnceAfterGraceWindow()
    {
        // Arrange
        var cache = NewCache();
        cache.RecordReachable(TenantId.Local, "dir-A", new[] { Session("s1") });

        // Act + Assert - every failure up to and including the grace-window count stays Wobbly (served stale)
        for (int cycle = 1; cycle <= FleetRosterCache.GraceWindowPollCycles; cycle++)
        {
            _now = _now.AddSeconds(2);
            var projection = cache.RecordUnreachable(TenantId.Local, "dir-A", "timeout");
            Assert.Equal(FleetReachabilityState.Wobbly, projection.State);
            Assert.NotNull(projection.StaleSessions);
            Assert.Single(projection.StaleSessions!);
        }

        // Act - one failure past the grace window
        _now = _now.AddSeconds(2);
        var offline = cache.RecordUnreachable(TenantId.Local, "dir-A", "timeout");

        // Assert - Offline, sessions dropped (nothing to serve), but last-seen is still reported
        Assert.Equal(FleetReachabilityState.Offline, offline.State);
        Assert.Null(offline.StaleSessions);
        Assert.NotNull(offline.LastSeenUtc);
        Assert.NotNull(offline.LastSeenAgeSeconds);
    }

    [Fact]
    public void OfflineThenReachable_ReappearsOnline()
    {
        // Arrange - drive the Director all the way to Offline
        var cache = NewCache();
        cache.RecordReachable(TenantId.Local, "dir-A", new[] { Session("s1") });
        for (int cycle = 0; cycle <= FleetRosterCache.GraceWindowPollCycles; cycle++)
        {
            _now = _now.AddSeconds(2);
            cache.RecordUnreachable(TenantId.Local, "dir-A", "timeout");
        }
        // Confirm it is Offline before the recovery.
        _now = _now.AddSeconds(2);
        Assert.Equal(FleetReachabilityState.Offline, cache.RecordUnreachable(TenantId.Local, "dir-A", "timeout").State);

        // Act - the machine comes back
        _now = _now.AddSeconds(2);
        var backOnline = cache.RecordReachable(TenantId.Local, "dir-A", new[] { Session("s9") });

        // Assert - Online again, and the freshly stored snapshot (not the discarded old one) is what a
        // subsequent Wobbly serves.
        Assert.Equal(FleetReachabilityState.Online, backOnline.State);
        _now = _now.AddSeconds(2);
        var wobbly = cache.RecordUnreachable(TenantId.Local, "dir-A", "timeout");
        Assert.Equal(FleetReachabilityState.Wobbly, wobbly.State);
        Assert.NotNull(wobbly.StaleSessions);
        Assert.Single(wobbly.StaleSessions!);
        Assert.Equal("s9", wobbly.StaleSessions![0].SessionId);
    }

    [Fact]
    public void RecordUnreachable_NeverReachable_IsOfflineWithNoSnapshot()
    {
        // Arrange
        var cache = NewCache();

        // Act - a Director that failed its very first poll (no last-known-good ever stored)
        var projection = cache.RecordUnreachable(TenantId.Local, "dir-A", "endpoint never answered");

        // Assert - Offline immediately, nothing to serve, and no last-seen time to report
        Assert.Equal(FleetReachabilityState.Offline, projection.State);
        Assert.Null(projection.StaleSessions);
        Assert.Null(projection.LastSeenUtc);
        Assert.Null(projection.LastSeenAgeSeconds);
    }

    [Fact]
    public void WobblyServe_RecomputesIdleClockFromLastActivity()
    {
        // Arrange - a session last active 10 seconds before the successful read
        var cache = NewCache();
        var lastActivity = _now.AddSeconds(-10);
        cache.RecordReachable(TenantId.Local, "dir-A", new[] { Session("s1", lastActivity: lastActivity) });

        // Act - a failed poll 30 seconds later serves the stale snapshot
        _now = _now.AddSeconds(30);
        var wobbly = cache.RecordUnreachable(TenantId.Local, "dir-A", "timeout");

        // Assert - the served copy's idle clock advanced to now - lastActivity (40s), not the frozen value
        Assert.Equal(FleetReachabilityState.Wobbly, wobbly.State);
        Assert.NotNull(wobbly.StaleSessions);
        Assert.Equal(40, wobbly.StaleSessions![0].IdleSeconds);
    }

    [Fact]
    public void Forget_ClearsSnapshot_NextFailureIsOffline()
    {
        // Arrange
        var cache = NewCache();
        cache.RecordReachable(TenantId.Local, "dir-A", new[] { Session("s1") });

        // Act - the Director is unregistered/evicted, then a later stray failure is recorded
        cache.Forget("dir-A");
        var projection = cache.RecordUnreachable(TenantId.Local, "dir-A", "unreachable");

        // Assert - no cached snapshot survives, so it reads Offline rather than serving stale sessions
        Assert.Equal(FleetReachabilityState.Offline, projection.State);
        Assert.Null(projection.StaleSessions);
    }

    [Fact]
    public void WobblyServe_IsPartitionedByTenant_NeverServesAnotherTenantsSnapshot()
    {
        // Hosted Multi-Tenancy (session-serving): the SAME Director id is reachable under one tenant and
        // unreachable under another (on hosted a Director lives in exactly one tenant, so from a different
        // tenant it is simply absent). The grace-window serve must NOT hand tenant-A's last-known-good sessions
        // to tenant-B: tenant-B never saw this Director reachable, so it has no snapshot to serve and reads
        // Offline. Reverting the cache key from (tenant, director) back to director-only makes tenant-B's
        // failed read serve tenant-A's snapshot Wobbly - the cross-tenant leak this asserts against.
        var cache = NewCache();
        var tenantA = new TenantId("tenant-alice");
        var tenantB = new TenantId("tenant-bob");

        // Tenant A reads the Director successfully and caches its roster.
        cache.RecordReachable(tenantA, "dir-shared", new[] { Session("a-only") });

        // Tenant B finds the same Director id unreachable on its first read: no snapshot of its own -> Offline,
        // and crucially it is NOT served tenant A's "a-only" session.
        var forB = cache.RecordUnreachable(tenantB, "dir-shared", "not in this tenant");
        Assert.Equal(FleetReachabilityState.Offline, forB.State);
        Assert.Null(forB.StaleSessions);

        // Tenant A's own grace window is intact - a failed read still serves A's snapshot to A.
        _now = _now.AddSeconds(2);
        var forA = cache.RecordUnreachable(tenantA, "dir-shared", "timeout");
        Assert.Equal(FleetReachabilityState.Wobbly, forA.State);
        Assert.NotNull(forA.StaleSessions);
        Assert.Equal("a-only", Assert.Single(forA.StaleSessions!).SessionId);
    }
}
