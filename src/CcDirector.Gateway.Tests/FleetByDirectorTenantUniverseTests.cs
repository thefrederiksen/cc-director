using System;
using System.Linq;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Hosted Multi-Tenancy (audit H1, Codex residual): <see cref="GatewayEndpoints.FleetByDirector"/> is the role
/// universe for the folds that run outside the /sessions roster loop - <c>/exes/list</c>, <c>GET
/// /sessions/{sid}</c>, and the one this residual is about, <c>POST /sessions/voice-mode/all</c>. It must build
/// that universe from the CALLER'S OWN tenant partition, not the fleet-global registry.
///
/// The push-store read (<see cref="PushedSessionStore.TryGetFresh"/>) is tenant-keyed, so it already keeps
/// another tenant's DATA out. But the helper also iterated the fleet-global <see cref="DirectorRegistry.ListDirectors()"/>,
/// projected each entry to its BARE DirectorId, and read the caller's cache under that fleet-wide id set. Because
/// two tenants can own a Director with the SAME id (the registry key is (tenant, id), the id is client-chosen),
/// that let ANOTHER tenant's registered Director id decide which of the caller's own cached rosters the fold
/// surfaced - a cross-tenant coupling on the id SET even though the data itself stayed the caller's.
///
/// This test isolates that coupling. Acme (the caller) holds a fresh pushed roster under the id "dir-shared"
/// while Acme's REGISTRY has no such Director - the honest race where Acme's Director dropped from the registry
/// but its push cache has not yet expired. Only GLOBEX's registry lists "dir-shared". With the fix the fold's
/// universe is Acme's registered Directors, so "dir-shared" is absent; revert the helper to the fleet-global
/// <c>ListDirectors()</c> and Globex's registered id drags Acme's orphan cache back into the result - the
/// ContainsKey("dir-shared") assertion below goes RED. Acme's genuinely-registered "dir-acme" stays present, so
/// the fix is a boundary, not a blanket empty.
/// </summary>
public sealed class FleetByDirectorTenantUniverseTests
{
    private static readonly TenantId Acme = new("acme");
    private static readonly TenantId Globex = new("globex");
    private readonly DateTime _now = new(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);
    private readonly TimeSpan _stale = TimeSpan.FromSeconds(20);

    private static SessionDto Session(string id) => new() { SessionId = id, ActivityState = "Working" };

    private void SeedCache(PushedSessionStore store, TenantId tenant, string directorId, string sessionId)
    {
        var conn = $"conn-{tenant.Value}-{directorId}";
        store.RegisterConnection(tenant, directorId, conn);
        Assert.True(store.ApplySnapshot(tenant, directorId, conn, 0, new[] { Session(sessionId) }));
    }

    private static void RegisterDirector(DirectorRegistry registry, TenantId tenant, string directorId, string machine)
        => registry.RegisterFromStream(directorId, machine, "user", "1.0", pid: 1234, startedAt: default, tenant);

    [Fact]
    public void FleetByDirector_universe_is_bounded_to_the_callers_registered_directors()
    {
        var store = new PushedSessionStore(() => _now);
        using var registry = new DirectorRegistry();

        // Acme owns "dir-acme" (registered AND freshly cached) - its legitimate roster.
        RegisterDirector(registry, Acme, "dir-acme", "acme-box");
        SeedCache(store, Acme, "dir-acme", "acme-session");

        // The residual's trap: Acme has a fresh cache under "dir-shared" that its REGISTRY no longer lists,
        // and only GLOBEX's registry lists "dir-shared". A fleet-global universe would surface Acme's orphan
        // cache purely because Globex registered that id.
        SeedCache(store, Acme, "dir-shared", "acme-orphan-session");
        RegisterDirector(registry, Globex, "dir-shared", "globex-box");

        var byDirector = GatewayEndpoints.FleetByDirector(registry, store, _stale, Acme);

        // Acme's genuinely-registered Director is present...
        Assert.True(byDirector.ContainsKey("dir-acme"));
        Assert.Equal("acme-session", Assert.Single(byDirector["dir-acme"]).SessionId);

        // ...but the id only Globex's registry lists never enters Acme's universe. REVERT-PROOF: with the helper
        // reading the fleet-global ListDirectors(), Globex's "dir-shared" drags Acme's orphan cache in and this
        // fails.
        Assert.False(byDirector.ContainsKey("dir-shared"));

        // The whole result is confined to Acme's registered Director ids - the universe is the caller's partition.
        var acmeIds = registry.ListDirectors(Acme).Select(d => d.DirectorId).ToHashSet(StringComparer.Ordinal);
        Assert.All(byDirector.Keys, k => Assert.Contains(k, acmeIds));
    }

    [Fact]
    public void FleetByDirector_never_folds_another_tenants_cache_under_a_shared_id()
    {
        var store = new PushedSessionStore(() => _now);
        using var registry = new DirectorRegistry();

        // Both tenants own a Director with the SAME id, each with its own fresh roster.
        RegisterDirector(registry, Acme, "dir-shared", "acme-box");
        SeedCache(store, Acme, "dir-shared", "acme-session");
        RegisterDirector(registry, Globex, "dir-shared", "globex-box");
        SeedCache(store, Globex, "dir-shared", "globex-secret-session");

        var byDirector = GatewayEndpoints.FleetByDirector(registry, store, _stale, Acme);

        // Acme sees its own roster under the shared id, and NEVER Globex's - the tenant-keyed push read is the
        // data boundary; this fold surfaces exactly one tenant's sessions for the id.
        Assert.True(byDirector.ContainsKey("dir-shared"));
        Assert.Equal("acme-session", Assert.Single(byDirector["dir-shared"]).SessionId);
        Assert.DoesNotContain(byDirector.Values.SelectMany(v => v),
            s => s.SessionId == "globex-secret-session");
    }
}
