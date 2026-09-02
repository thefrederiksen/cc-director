using CcDirector.Core.Tenancy;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

public sealed class KnownRepositoryStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private KnownRepositoryStore NewStore(ITenantContext? tenant = null) =>
        new(_harness.Open(tenant));

    [Fact]
    public void Observe_WindowsPathSpellingDiffers_DeduplicatesAndKeepsNewestFacts()
    {
        var store = NewStore();
        var first = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var second = first.AddDays(1);

        Assert.True(store.Observe(TenantId.Local, "SOREN_NORTH", @"D:\Repos\Project\", "Old name", first));
        Assert.True(store.Observe(TenantId.Local, "soren_north", "d:/repos/project", "Current name", second));

        var row = Assert.Single(store.ReadForMachine(TenantId.Local, "SOREN_NORTH"));
        Assert.Equal("Current name", row.Name);
        Assert.Equal("d:/repos/project", row.Path);
        Assert.Equal(second, row.LastUsed);
    }

    [Fact]
    public void Observe_OlderObservation_DoesNotMoveFactsBackwards()
    {
        var store = NewStore();
        var newest = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

        store.Observe(TenantId.Local, "Machine", "/repos/current", "Current name", newest);
        Assert.False(store.Observe(
            TenantId.Local, "Machine", "/repos/current/", "Stale name", newest.AddDays(-1)));

        var row = Assert.Single(store.ReadForMachine(TenantId.Local, "Machine"));
        Assert.Equal("Current name", row.Name);
        Assert.Equal("/repos/current", row.Path);
        Assert.Equal(newest, row.LastUsed);
    }

    [Fact]
    public void ReadForMachine_TenantsAndMachinesDiffer_ReturnsOnlyTheRequestedPartition()
    {
        var alphaTenant = new TenantId("alpha");
        var betaTenant = new TenantId("beta");
        var alpha = NewStore(new FixedTenantContext(alphaTenant));
        var beta = NewStore(new FixedTenantContext(betaTenant));
        var now = DateTime.UtcNow;

        alpha.Observe(alphaTenant, "North", "/repos/alpha", "Alpha", now);
        alpha.Observe(alphaTenant, "South", "/repos/south", "South", now);
        beta.Observe(betaTenant, "North", "/repos/beta", "Beta", now);

        Assert.Equal("/repos/alpha", Assert.Single(alpha.ReadForMachine(alphaTenant, "north")).Path);
        Assert.Equal("/repos/beta", Assert.Single(beta.ReadForMachine(betaTenant, "NORTH")).Path);
        Assert.Empty(alpha.ReadForMachine(alphaTenant, "South-West"));
    }

    [Fact]
    public void Observe_GatewayReopens_CatalogEntrySurvives()
    {
        var now = DateTime.UtcNow;
        NewStore().Observe(TenantId.Local, "Machine", "/repos/persistent", "Persistent", now);

        var reopened = NewStore();
        var row = Assert.Single(reopened.ReadForMachine(TenantId.Local, "Machine"));

        Assert.Equal("/repos/persistent", row.Path);
        Assert.Equal(now, row.LastUsed);
    }
}
