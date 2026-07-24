using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

public class PushedRepositoryStoreTests
{
    private static RepoStatusDto Repo(string name, string machine = "M1") => new()
    {
        Name = name,
        Path = $@"D:\repos\{name}",
        MachineName = machine,
        DirectorId = "d1",
        Worktrees = new List<WorktreeDto> { new() { Path = $@"D:\repos\{name}-wt", Branch = "b", State = "safe-to-reap", Reason = "merged" } },
    };

    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    [Fact]
    public void Snapshot_RoundTrips_ForItsTenantAndDirector()
    {
        var store = new PushedRepositoryStore();
        var tenant = TenantId.Local;

        store.RegisterConnection(tenant, "d1", "conn1");
        Assert.True(store.ApplySnapshot(tenant, "d1", "conn1", 1, new[] { Repo("alpha") }));

        var fresh = store.TryGetFresh(tenant, "d1", Fresh);
        Assert.NotNull(fresh);
        Assert.Equal("alpha", Assert.Single(fresh!.Value.Repositories).Name);
        Assert.Contains("d1", store.DirectorIdsFor(tenant));
    }

    [Fact]
    public void SameConnection_StaleOrDuplicateSequence_IsRejected()
    {
        var store = new PushedRepositoryStore();
        var tenant = TenantId.Local;

        store.RegisterConnection(tenant, "d1", "conn1");
        Assert.True(store.ApplySnapshot(tenant, "d1", "conn1", 5, new[] { Repo("alpha") }));
        Assert.False(store.ApplySnapshot(tenant, "d1", "conn1", 5, new[] { Repo("stale") }));   // duplicate
        Assert.False(store.ApplySnapshot(tenant, "d1", "conn1", 3, new[] { Repo("older") }));   // out of order

        var fresh = store.TryGetFresh(tenant, "d1", Fresh);
        Assert.Equal("alpha", Assert.Single(fresh!.Value.Repositories).Name); // the accepted one survived
    }

    [Fact]
    public void NewConnection_AfterTakeover_WinsEvenWithALowerSequence()
    {
        // A restarted Director re-Hellos (registering the new connection) and begins a new
        // sequence from 1 - its snapshot is authoritative and must replace the old state.
        var store = new PushedRepositoryStore();
        var tenant = TenantId.Local;

        store.RegisterConnection(tenant, "d1", "conn1");
        Assert.True(store.ApplySnapshot(tenant, "d1", "conn1", 900, new[] { Repo("old") }));
        store.RegisterConnection(tenant, "d1", "conn2");
        Assert.True(store.ApplySnapshot(tenant, "d1", "conn2", 1, new[] { Repo("fresh") }));

        var fresh = store.TryGetFresh(tenant, "d1", Fresh);
        Assert.Equal("fresh", Assert.Single(fresh!.Value.Repositories).Name);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F9): only the CURRENT connection may push. After a new
    // connection takes over, a late push from the superseded connection is dropped - even at a
    // higher sequence - so two live connections can never alternate ownership of the store.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void LatePush_FromASupersededConnection_IsRejected()
    {
        var store = new PushedRepositoryStore();
        var tenant = TenantId.Local;

        store.RegisterConnection(tenant, "d1", "conn1");
        Assert.True(store.ApplySnapshot(tenant, "d1", "conn1", 10, new[] { Repo("old-world") }));

        // The Director reconnects: conn2 is now the current connection.
        store.RegisterConnection(tenant, "d1", "conn2");
        Assert.True(store.ApplySnapshot(tenant, "d1", "conn2", 1, new[] { Repo("current") }));

        // A late (even higher-sequence) push from the superseded conn1 must be dropped.
        Assert.False(store.ApplySnapshot(tenant, "d1", "conn1", 999, new[] { Repo("stale-late") }));

        var fresh = store.TryGetFresh(tenant, "d1", Fresh);
        Assert.Equal("current", Assert.Single(fresh!.Value.Repositories).Name);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection round 2, ruling R2-4): a REPEATED Hello from the SAME connection
    // must not reset the sequence baseline - otherwise a duplicate Hello lets an older
    // sequence replay over newer data. The baseline resets only when ownership actually
    // changes to a different connection.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void RepeatedHello_OnTheSameConnection_KeepsTheSequenceBaseline()
    {
        var store = new PushedRepositoryStore();
        var tenant = TenantId.Local;

        store.RegisterConnection(tenant, "d1", "conn1");
        Assert.True(store.ApplySnapshot(tenant, "d1", "conn1", 100, new[] { Repo("newest") }));

        // The same connection says Hello again - the baseline must survive.
        store.RegisterConnection(tenant, "d1", "conn1");

        Assert.False(store.ApplySnapshot(tenant, "d1", "conn1", 50, new[] { Repo("replayed") }));

        var fresh = store.TryGetFresh(tenant, "d1", Fresh);
        Assert.Equal("newest", Assert.Single(fresh!.Value.Repositories).Name); // no replay landed
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (issue 516): a superseded connection that re-Hellos (its periodic reseed) must
    // NOT reclaim ownership. Before the fix, RegisterConnection treated any connection different
    // from the current one as a new owner, so connA -> connB -> connA-again handed ownership back
    // to A, and two overlapping connections could alternate the authoritative snapshot forever.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void SupersededConnection_ReHelloing_CannotReclaimOwnership()
    {
        var store = new PushedRepositoryStore();
        var tenant = TenantId.Local;

        store.RegisterConnection(tenant, "d1", "connA");
        Assert.True(store.ApplySnapshot(tenant, "d1", "connA", 5, new[] { Repo("from-A") }));

        // connB supersedes connA.
        store.RegisterConnection(tenant, "d1", "connB");
        Assert.True(store.ApplySnapshot(tenant, "d1", "connB", 1, new[] { Repo("from-B") }));

        // connA re-Hellos - the critical sequence register A -> register B -> register A again.
        store.RegisterConnection(tenant, "d1", "connA");

        // connA still cannot push; connB remains the owner and its data stands.
        Assert.False(store.ApplySnapshot(tenant, "d1", "connA", 6, new[] { Repo("A-reclaim") }));
        var fresh = store.TryGetFresh(tenant, "d1", Fresh);
        Assert.Equal("from-B", Assert.Single(fresh!.Value.Repositories).Name);
    }

    // A still-live older connection CAN take over once the newer owner disconnects - the lockout
    // is only while a newer connection is present, never permanent.
    [Fact]
    public void OlderConnection_ReclaimsAfterTheNewerOwnerDisconnects()
    {
        var store = new PushedRepositoryStore();
        var tenant = TenantId.Local;

        store.RegisterConnection(tenant, "d1", "connA");
        store.RegisterConnection(tenant, "d1", "connB"); // B supersedes A
        Assert.True(store.ApplySnapshot(tenant, "d1", "connB", 1, new[] { Repo("from-B") }));

        // B disconnects; A re-Hellos and may now resume.
        store.UnregisterConnection(tenant, "d1", "connB");
        store.RegisterConnection(tenant, "d1", "connA");
        Assert.True(store.ApplySnapshot(tenant, "d1", "connA", 1, new[] { Repo("from-A-again") }));

        var fresh = store.TryGetFresh(tenant, "d1", Fresh);
        Assert.Equal("from-A-again", Assert.Single(fresh!.Value.Repositories).Name);
    }

    [Fact]
    public void Push_WithoutARegisteredConnection_IsRejected()
    {
        var store = new PushedRepositoryStore();
        Assert.False(store.ApplySnapshot(TenantId.Local, "d1", "conn1", 1, new[] { Repo("alpha") }));
        Assert.Null(store.TryGetFresh(TenantId.Local, "d1", Fresh));
    }

    [Fact]
    public void Unregister_OnlyClearsTheCurrentConnection()
    {
        var store = new PushedRepositoryStore();
        var tenant = TenantId.Local;

        store.RegisterConnection(tenant, "d1", "conn1");
        store.RegisterConnection(tenant, "d1", "conn2");

        // conn1's late disconnect must not clear conn2's ownership.
        store.UnregisterConnection(tenant, "d1", "conn1");
        Assert.True(store.ApplySnapshot(tenant, "d1", "conn2", 1, new[] { Repo("alpha") }));

        // conn2's own disconnect does clear it - nothing may push afterwards.
        store.UnregisterConnection(tenant, "d1", "conn2");
        Assert.False(store.ApplySnapshot(tenant, "d1", "conn2", 2, new[] { Repo("beta") }));
    }

    [Fact]
    public void TenantPartition_NeitherDirectionLeaks()
    {
        var store = new PushedRepositoryStore();
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");

        store.RegisterConnection(tenantA, "dA", "cA");
        store.RegisterConnection(tenantB, "dB", "cB");
        store.ApplySnapshot(tenantA, "dA", "cA", 1, new[] { Repo("a-repo") });
        store.ApplySnapshot(tenantB, "dB", "cB", 1, new[] { Repo("b-repo") });

        // A sees only A's directors and repos; B only B's - the neighbour probe in both directions.
        Assert.Equal(new[] { "dA" }, store.DirectorIdsFor(tenantA));
        Assert.Equal(new[] { "dB" }, store.DirectorIdsFor(tenantB));
        Assert.Null(store.TryGetFresh(tenantA, "dB", Fresh));
        Assert.Null(store.TryGetFresh(tenantB, "dA", Fresh));
        Assert.Equal("a-repo", Assert.Single(store.TryGetFresh(tenantA, "dA", Fresh)!.Value.Repositories).Name);
        Assert.Equal("b-repo", Assert.Single(store.TryGetFresh(tenantB, "dB", Fresh)!.Value.Repositories).Name);
    }

    [Fact]
    public void StaleData_IsWithheld_NotServed()
    {
        var store = new PushedRepositoryStore();
        var tenant = TenantId.Local;
        store.RegisterConnection(tenant, "d1", "conn1");
        store.ApplySnapshot(tenant, "d1", "conn1", 1, new[] { Repo("alpha") });

        // With a zero stale window everything is stale immediately.
        Assert.Null(store.TryGetFresh(tenant, "d1", TimeSpan.Zero));
    }
}
