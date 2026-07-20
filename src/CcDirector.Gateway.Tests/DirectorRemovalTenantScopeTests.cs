using System;
using System.IO;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Removing one tenant's Director must not destroy another tenant's cached roster.
///
/// THE DEFECT THESE PIN. The tunnel Hello takes the director id straight from the authenticated client's
/// payload - trimmed, checked non-blank, and otherwise unconstrained - and the registry keys entries by
/// (tenant, id), so TWO TENANTS MAY HOLD THE SAME ID AT ONCE. The registry's removal was already
/// tenant-correct, but <c>OnDirectorRemoved</c> was typed <c>Action&lt;string&gt;</c>, which DISCARDED the
/// tenant the sweep had in hand; the roster cache's forget was correspondingly ownerless and removed every
/// partition whose id matched case-insensitively. So tenant A could register an id it had observed tenant B
/// using, disconnect, and - after the heartbeat timeout plus one sweep - have B's cached roster destroyed.
/// If B was healthy its next read repopulated the cache; if B was inside its GRACE WINDOW, its
/// last-known-good sessions were destroyed and the next read dropped them instead of serving them.
///
/// WHAT EACH TEST HOLDS DOWN, and why both halves are needed. Survival alone proves nothing - a forget that
/// removed nothing at all would also leave the victim intact - so every test here asserts the
/// DESTRUCTIBILITY CONTROL beside it: the owner's own entry really is destroyed by the same call.
///
/// REVERT-PROOF. Make <see cref="FleetRosterCache.Forget"/> ignore its tenant argument and enumerate every
/// partition again (the pre-fix body) and both survival assertions go RED while both destructibility
/// controls stay GREEN. Separately, make <c>DirectorRegistry.RaiseDirectorRemoved</c> stamp a fixed tenant
/// onto the event instead of the removed key's own, and
/// <see cref="Stale_sweep_forgets_only_the_swept_tenants_cached_roster"/> goes RED on its destructibility
/// control - the owner's entry survives a removal that should have cleared it.
/// </summary>
public sealed class DirectorRemovalTenantScopeTests
{
    private static readonly TenantId TenantA = new("tenant-alice");
    private static readonly TenantId TenantB = new("tenant-bob");

    /// <summary>The id both tenants choose. The client picks it, so this collision is reachable on purpose.</summary>
    private const string SharedId = "dir-shared";

    private static SessionDto Session(string id) => new()
    {
        SessionId = id,
        ActivityState = "Working",
        LastActivityAt = DateTime.UtcNow,
    };

    [Fact]
    public void Forget_clears_only_the_named_tenants_entry()
    {
        // Arrange - both tenants have a last-known-good roster cached under the SAME director id.
        var cache = new FleetRosterCache();
        cache.RecordReachable(TenantA, SharedId, new[] { Session("a-only") });
        cache.RecordReachable(TenantB, SharedId, new[] { Session("b-only") });

        // Act - tenant A's Director is removed.
        cache.Forget(TenantA, SharedId);

        // Assert (DESTRUCTIBILITY CONTROL) - A's own entry really is gone: with no snapshot left, A's next
        // failed read is Offline rather than a grace-window serve. Without this, "B survived" would be
        // satisfied by a Forget that removed nothing whatsoever.
        var forA = cache.RecordUnreachable(TenantA, SharedId, "gone");
        Assert.Equal(FleetReachabilityState.Offline, forA.State);
        Assert.Null(forA.StaleSessions);

        // Assert (THE PROPERTY) - B's cached roster is untouched: still inside its grace window, still
        // serving B's OWN session, never A's.
        var forB = cache.RecordUnreachable(TenantB, SharedId, "transient");
        Assert.Equal(FleetReachabilityState.Wobbly, forB.State);
        Assert.NotNull(forB.StaleSessions);
        Assert.Equal("b-only", Assert.Single(forB.StaleSessions!).SessionId);
    }

    [Fact]
    public void Stale_sweep_forgets_only_the_swept_tenants_cached_roster()
    {
        // The end-to-end attack, through the real registry, the real event, and the real subscriber wiring.
        var dir = Path.Combine(Path.GetTempPath(), "cc-drt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var registry = new DirectorRegistry(dir);
        try
        {
            var cache = new FleetRosterCache();
            // Wired exactly as GatewayHost wires it.
            registry.OnDirectorRemoved += removal => cache.Forget(removal.Tenant, removal.DirectorId);

            // Arrange - both tenants register a Director under the SAME client-chosen id, and both have a
            // last-known-good roster cached. Tenant B is the victim; it is healthy and cached.
            var a = registry.RegisterFromStream(SharedId, "machine-a", "alice", "1.0", 111, DateTime.UtcNow, TenantA);
            registry.RegisterFromStream(SharedId, "machine-b", "bob", "1.0", 222, DateTime.UtcNow, TenantB);
            cache.RecordReachable(TenantA, SharedId, new[] { Session("a-only") });
            cache.RecordReachable(TenantB, SharedId, new[] { Session("b-only") });

            // Act - tenant A's tunnel dies: its entry stops being refreshed and ages past the heartbeat
            // timeout, then the stale sweep runs. This is the attacker's move - A disconnects on purpose.
            a.LastSeen = DateTime.UtcNow - DirectorRegistry.HttpHeartbeatTimeout - TimeSpan.FromSeconds(30);
            registry.SweepStale();

            // Assert (DESTRUCTIBILITY CONTROL) - the sweep really did remove A's entry AND really did reach
            // the subscriber for A: A's registry entry is gone and A's cached roster was forgotten.
            Assert.Null(registry.Get(TenantA, SharedId));
            var forA = cache.RecordUnreachable(TenantA, SharedId, "swept");
            Assert.Equal(FleetReachabilityState.Offline, forA.State);

            // Assert (THE PROPERTY) - tenant B is untouched. Its registry entry survives, and its cached
            // roster survives: a failed read still serves B's last-known-good session from the grace window
            // rather than dropping it. This is the assertion the bare-string event could not satisfy.
            Assert.NotNull(registry.Get(TenantB, SharedId));
            var forB = cache.RecordUnreachable(TenantB, SharedId, "transient");
            Assert.Equal(FleetReachabilityState.Wobbly, forB.State);
            Assert.NotNull(forB.StaleSessions);
            Assert.Equal("b-only", Assert.Single(forB.StaleSessions!).SessionId);
        }
        finally
        {
            registry.Dispose();
            try { Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Removal_event_carries_the_tenant_that_owned_the_entry()
    {
        // The seam itself: the tenant must survive the event boundary. A subscriber that receives the wrong
        // owner - or a defaulted one - cannot scope anything correctly no matter how careful it is.
        var dir = Path.Combine(Path.GetTempPath(), "cc-drt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var registry = new DirectorRegistry(dir);
        try
        {
            var seen = new List<DirectorRemoval>();
            registry.OnDirectorRemoved += removal => seen.Add(removal);

            var a = registry.RegisterFromStream(SharedId, "machine-a", "alice", "1.0", 111, DateTime.UtcNow, TenantA);
            registry.RegisterFromStream(SharedId, "machine-b", "bob", "1.0", 222, DateTime.UtcNow, TenantB);

            a.LastSeen = DateTime.UtcNow - DirectorRegistry.HttpHeartbeatTimeout - TimeSpan.FromSeconds(30);
            registry.SweepStale();

            var removal = Assert.Single(seen);
            Assert.Equal(TenantA, removal.Tenant);
            Assert.Equal(SharedId, removal.DirectorId);
            // And it is A's tenant specifically - not Local, not B's, not a default.
            Assert.NotEqual(TenantB, removal.Tenant);
            Assert.NotEqual(TenantId.Local, removal.Tenant);
        }
        finally
        {
            registry.Dispose();
            try { Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }
}
