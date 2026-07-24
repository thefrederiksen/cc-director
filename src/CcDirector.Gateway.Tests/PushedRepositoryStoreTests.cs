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

        Assert.True(store.ApplySnapshot(tenant, "d1", "conn1", 5, new[] { Repo("alpha") }));
        Assert.False(store.ApplySnapshot(tenant, "d1", "conn1", 5, new[] { Repo("stale") }));   // duplicate
        Assert.False(store.ApplySnapshot(tenant, "d1", "conn1", 3, new[] { Repo("older") }));   // out of order

        var fresh = store.TryGetFresh(tenant, "d1", Fresh);
        Assert.Equal("alpha", Assert.Single(fresh!.Value.Repositories).Name); // the accepted one survived
    }

    [Fact]
    public void NewConnection_AlwaysWins_EvenWithALowerSequence()
    {
        // A restarted Director begins a new sequence from 1 on a new connection - its snapshot is
        // authoritative and must replace the old connection's state.
        var store = new PushedRepositoryStore();
        var tenant = TenantId.Local;

        Assert.True(store.ApplySnapshot(tenant, "d1", "conn1", 900, new[] { Repo("old") }));
        Assert.True(store.ApplySnapshot(tenant, "d1", "conn2", 1, new[] { Repo("fresh") }));

        var fresh = store.TryGetFresh(tenant, "d1", Fresh);
        Assert.Equal("fresh", Assert.Single(fresh!.Value.Repositories).Name);
    }

    [Fact]
    public void TenantPartition_NeitherDirectionLeaks()
    {
        var store = new PushedRepositoryStore();
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");

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
        store.ApplySnapshot(tenant, "d1", "conn1", 1, new[] { Repo("alpha") });

        // With a zero stale window everything is stale immediately.
        Assert.Null(store.TryGetFresh(tenant, "d1", TimeSpan.Zero));
    }
}
