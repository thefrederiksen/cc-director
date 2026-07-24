using System;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// REGRESSION (inspection): the SESSION roster is a DESTRUCTIVE authority (the worktree reaper trusts
/// it to know which worktrees are in use), so it needs the same connection-recency discipline as the
/// repository store - a superseded connection re-sending Hello must not reclaim ownership and push a
/// stale snapshot that omits a live session.
/// </summary>
public class PushedSessionStoreConnectionEpochTests
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);
    private static SessionDto Sess(string id) => new() { SessionId = id };

    [Fact]
    public void SupersededConnection_ReHelloing_CannotReclaimTheSessionRoster()
    {
        var store = new PushedSessionStore();
        var t = TenantId.Local;

        store.RegisterConnection(t, "d1", "connA");
        Assert.True(store.ApplySnapshot(t, "d1", "connA", 5, new[] { Sess("from-A") }));

        store.RegisterConnection(t, "d1", "connB"); // supersedes A
        Assert.True(store.ApplySnapshot(t, "d1", "connB", 1, new[] { Sess("from-B") }));

        // connA re-Hellos (its periodic reseed) - the critical register A -> register B -> register A.
        store.RegisterConnection(t, "d1", "connA");
        Assert.False(store.ApplySnapshot(t, "d1", "connA", 6, new[] { Sess("A-reclaim") }));

        var fresh = store.TryGetFresh(t, "d1", Fresh);
        Assert.NotNull(fresh);
        Assert.Contains(fresh!, s => s.SessionId == "from-B");       // connB's true roster stands
        Assert.DoesNotContain(fresh!, s => s.SessionId == "A-reclaim"); // A's stale reclaim never landed
    }

    [Fact]
    public void OlderConnection_ReclaimsTheSessionRoster_AfterTheNewerOwnerDisconnects()
    {
        var store = new PushedSessionStore();
        var t = TenantId.Local;

        store.RegisterConnection(t, "d1", "connA");
        store.RegisterConnection(t, "d1", "connB"); // B supersedes A
        Assert.True(store.ApplySnapshot(t, "d1", "connB", 1, new[] { Sess("from-B") }));

        store.UnregisterConnection(t, "d1", "connB"); // B disconnects
        store.RegisterConnection(t, "d1", "connA");   // A may now resume
        Assert.True(store.ApplySnapshot(t, "d1", "connA", 1, new[] { Sess("from-A-again") }));

        Assert.Contains(store.TryGetFresh(t, "d1", Fresh)!, s => s.SessionId == "from-A-again");
    }
}
