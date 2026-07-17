using System;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Stage 3b: proves the <see cref="PushedSessionStore"/> is tenant-partitioned - one tenant can never
/// read another's Directors or sessions, not even by presenting a real session id that exists in the
/// other tenant. The production core is single-tenant (everything is <see cref="TenantId.Local"/>), so
/// this test injects two distinct tenants directly to exercise the isolation the resolver will rely on.
/// </summary>
public sealed class PushedSessionStoreTenantIsolationTests
{
    private static readonly TenantId Acme = new("acme");
    private static readonly TenantId Globex = new("globex");
    private readonly DateTime _now = new(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
    private readonly TimeSpan _stale = TimeSpan.FromSeconds(20);

    private PushedSessionStore NewStore() => new(() => _now);

    private static SessionDto Session(string id) => new() { SessionId = id, ActivityState = "Working" };

    private void Seed(PushedSessionStore store, TenantId tenant, string directorId, string connectionId, params string[] sessionIds)
    {
        store.RegisterConnection(tenant, directorId, connectionId);
        var sessions = new SessionDto[sessionIds.Length];
        for (var i = 0; i < sessionIds.Length; i++)
            sessions[i] = Session(sessionIds[i]);
        Assert.True(store.ApplySnapshot(tenant, directorId, connectionId, 0, sessions));
    }

    [Fact]
    public void TryLocate_CannotFindAnotherTenantsSession_EvenWithTheRealSessionId()
    {
        var store = NewStore();
        Seed(store, Acme, "dir-A", "conn-A", "s1");

        // The real session id from Acme, presented as Globex, must resolve to nothing.
        Assert.Null(store.TryLocate(Globex, "s1", _stale));

        // ...and Acme still finds its own.
        var located = store.TryLocate(Acme, "s1", _stale);
        Assert.NotNull(located);
        Assert.Equal("dir-A", located.Value.DirectorId);
    }

    [Fact]
    public void SnapshotFresh_ReturnsOnlyTheCallingTenantsSessions()
    {
        var store = NewStore();
        Seed(store, Acme, "dir-A", "conn-A", "s1", "s2");
        Seed(store, Globex, "dir-B", "conn-B", "s3");

        var acme = store.SnapshotFresh(Acme, _stale);
        var globex = store.SnapshotFresh(Globex, _stale);

        Assert.Equal(new[] { "s1", "s2" }, SortedSessionIds(acme));
        Assert.Equal(new[] { "s3" }, SortedSessionIds(globex));
    }

    [Fact]
    public void TryGetFresh_CannotReadADirectorThatLivesInAnotherTenant()
    {
        var store = NewStore();
        Seed(store, Acme, "dir-A", "conn-A", "s1");

        // dir-A exists, but not under Globex - so Globex sees nothing.
        Assert.Null(store.TryGetFresh(Globex, "dir-A", _stale));
        Assert.False(store.IsStreamConnected(Globex, "dir-A"));

        Assert.NotNull(store.TryGetFresh(Acme, "dir-A", _stale));
        Assert.True(store.IsStreamConnected(Acme, "dir-A"));
    }

    [Fact]
    public void SameDirectorId_InTwoTenants_AreIndependentEntries()
    {
        var store = NewStore();
        Seed(store, Acme, "dir-shared", "conn-acme", "acme-session");
        Seed(store, Globex, "dir-shared", "conn-globex", "globex-session");

        var acme = store.TryGetFresh(Acme, "dir-shared", _stale);
        var globex = store.TryGetFresh(Globex, "dir-shared", _stale);

        Assert.NotNull(acme);
        Assert.NotNull(globex);
        Assert.Equal("acme-session", Assert.Single(acme).SessionId);
        Assert.Equal("globex-session", Assert.Single(globex).SessionId);

        // The active connection is per-tenant too.
        Assert.Equal("conn-acme", store.GetActiveConnectionId(Acme, "dir-shared"));
        Assert.Equal("conn-globex", store.GetActiveConnectionId(Globex, "dir-shared"));
    }

    [Fact]
    public void AnInvalidTenant_IsRejected_NeverDefaulted()
    {
        var store = NewStore();
        TenantId invalid = default;

        Assert.Throws<ArgumentException>(() => store.RegisterConnection(invalid, "dir-A", "conn-A"));
        Assert.Throws<ArgumentException>(() => store.TryLocate(invalid, "s1", _stale));
        Assert.Throws<ArgumentException>(() => store.SnapshotFresh(invalid, _stale));
        Assert.Throws<ArgumentException>(() => store.TryGetFresh(invalid, "dir-A", _stale));
    }

    private static string[] SortedSessionIds(System.Collections.Generic.IReadOnlyList<(string DirectorId, SessionDto Session)> rows)
    {
        var ids = new string[rows.Count];
        for (var i = 0; i < rows.Count; i++)
            ids[i] = rows[i].Session.SessionId!;
        Array.Sort(ids, StringComparer.Ordinal);
        return ids;
    }
}
