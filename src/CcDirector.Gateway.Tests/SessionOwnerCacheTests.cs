using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #291: the owner cache must be pruned when a session exits on a REACHABLE Director, so the
/// per-session WS proxy reverts to 404 (session gone) instead of #288's 503 (owner offline). These
/// unit tests pin <see cref="SessionOwnerCache.Forget"/> and
/// <see cref="SessionOwnerCache.RetainForDirector"/>, and that an offline owner's entry survives a
/// reconcile of a DIFFERENT Director (the #288 503 path must not regress).
///
/// Hosted Multi-Tenancy (audit gap audit-a/f): the last two tests pin that the cache is partitioned by
/// (tenant, sessionId), so one tenant's write or reconcile can never touch another tenant's retained
/// state even when the two share a Director id or a session id. Reverting the partitioning reddens them.
/// </summary>
public sealed class SessionOwnerCacheTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");

    [Fact]
    public void Forget_removes_only_the_named_session()
    {
        var cache = new SessionOwnerCache();
        cache.Remember(TenantId.Local, "s1", "dir-a");
        cache.Remember(TenantId.Local, "s2", "dir-a");

        cache.Forget(TenantId.Local, "s1");

        Assert.Null(cache.OwnerOf(TenantId.Local, "s1"));
        Assert.Equal("dir-a", cache.OwnerOf(TenantId.Local, "s2"));
    }

    [Fact]
    public void Forget_is_a_noop_for_unknown_or_empty_id()
    {
        var cache = new SessionOwnerCache();
        cache.Remember(TenantId.Local, "s1", "dir-a");

        cache.Forget(TenantId.Local, "nope");
        cache.Forget(TenantId.Local, "");

        Assert.Equal("dir-a", cache.OwnerOf(TenantId.Local, "s1"));
    }

    [Fact]
    public void RetainForDirector_drops_sessions_no_longer_live_on_that_director()
    {
        var cache = new SessionOwnerCache();
        cache.Remember(TenantId.Local, "live", "dir-a");
        cache.Remember(TenantId.Local, "exited", "dir-a");

        // dir-a just answered and only reports "live" -> "exited" is gone.
        cache.RetainForDirector(TenantId.Local, "dir-a", new[] { "live" });

        Assert.Equal("dir-a", cache.OwnerOf(TenantId.Local, "live"));
        Assert.Null(cache.OwnerOf(TenantId.Local, "exited"));
    }

    [Fact]
    public void RetainForDirector_drops_all_when_director_reports_no_live_sessions()
    {
        var cache = new SessionOwnerCache();
        cache.Remember(TenantId.Local, "s1", "dir-a");
        cache.Remember(TenantId.Local, "s2", "dir-a");

        cache.RetainForDirector(TenantId.Local, "dir-a", Array.Empty<string>());

        Assert.Null(cache.OwnerOf(TenantId.Local, "s1"));
        Assert.Null(cache.OwnerOf(TenantId.Local, "s2"));
    }

    [Fact]
    public void RetainForDirector_never_touches_entries_owned_by_a_different_director()
    {
        // The #288 503 case: dir-b is OFFLINE (we never reconcile it). Reconciling the reachable
        // dir-a must leave dir-b's cached session intact so the WS proxy still answers 503 for it.
        var cache = new SessionOwnerCache();
        cache.Remember(TenantId.Local, "offline-owned", "dir-b");
        cache.Remember(TenantId.Local, "a1", "dir-a");

        cache.RetainForDirector(TenantId.Local, "dir-a", Array.Empty<string>());

        Assert.Null(cache.OwnerOf(TenantId.Local, "a1"));
        Assert.Equal("dir-b", cache.OwnerOf(TenantId.Local, "offline-owned"));
    }

    [Fact]
    public void RetainForDirector_is_a_noop_for_empty_director_id()
    {
        var cache = new SessionOwnerCache();
        cache.Remember(TenantId.Local, "s1", "dir-a");

        cache.RetainForDirector(TenantId.Local, "", Array.Empty<string>());

        Assert.Equal("dir-a", cache.OwnerOf(TenantId.Local, "s1"));
    }

    // ---------- audit gap audit-a/f: cross-tenant isolation ----------

    [Fact]
    public void RetainForDirector_in_one_tenant_never_evicts_another_tenant_retained_entry_for_a_shared_director_id()
    {
        // Both tenants happen to route through a Director with the SAME id "D" (e.g. a shared machine or
        // an id collision). Tenant B has recorded that D owns session "SB". Tenant A then reconciles its
        // OWN reachable roster for D, which reports only "SA" live. Tenant A's reconcile must NOT reach
        // into tenant B's partition and evict B's "SB" -> D entry.
        var cache = new SessionOwnerCache();
        cache.Remember(TenantB, "SB", "D");
        cache.Remember(TenantA, "SA", "D");

        cache.RetainForDirector(TenantA, "D", new[] { "SA" });

        Assert.Equal("D", cache.OwnerOf(TenantA, "SA")); // still live for A
        Assert.Equal("D", cache.OwnerOf(TenantB, "SB")); // survives - not A's to prune
    }

    [Fact]
    public void Remember_in_one_tenant_never_overwrites_another_tenant_owner_for_a_colliding_session_id()
    {
        // Two tenants observe a session with the SAME id "S" owned by different Directors. Neither
        // Remember may clobber the other's cached owner - each tenant reads back its own.
        var cache = new SessionOwnerCache();
        cache.Remember(TenantB, "S", "DB");
        cache.Remember(TenantA, "S", "DA");

        Assert.Equal("DB", cache.OwnerOf(TenantB, "S"));
        Assert.Equal("DA", cache.OwnerOf(TenantA, "S"));
    }
}
