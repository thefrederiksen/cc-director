using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data.Entities;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The unscoped-context seam (Hosted Multi-Tenancy increment 1). <c>CreateUnscopedContext</c> exists for the
/// global mapping tables, but it MUST leave <c>ActiveTenant</c> null so that a tenant-SCOPED read through it
/// fails closed (returns nothing) rather than silently serving a leftover tenant's rows. Because the context
/// factory is a POOL and pooling does not reset custom properties, a context previously stamped by
/// <c>CreateContext</c> can be handed back here, so the null must be set explicitly - these tests pin that.
/// </summary>
public sealed class GatewayDatabaseUnscopedContextTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void CreateUnscopedContext_LeavesActiveTenantNull()
    {
        var db = _harness.Open(new FixedTenantContext(new TenantId("t-alice")));

        // Hand out (and dispose) a SCOPED context first, so its tenant-stamped instance returns to the pool.
        using (var scoped = db.CreateContext())
        {
            Assert.Equal("t-alice", scoped.ActiveTenant);
        }

        // A subsequent unscoped context must be null even if it reuses that pooled instance.
        using var unscoped = db.CreateUnscopedContext();
        Assert.Null(unscoped.ActiveTenant);
    }

    [Fact]
    public void ScopedRead_ThroughUnscopedContext_FailsClosed()
    {
        var db = _harness.Open(new FixedTenantContext(new TenantId("t-alice")));

        // Write a tenant-scoped row as t-alice.
        using (var ctx = db.CreateContext())
        {
            ctx.MissionNotes.Add(new MissionNoteEntity
            {
                Key = "k1",
                Mission = "m",
                Why = "w",
                UpdatedAtUtc = DateTime.UtcNow,
                TenantId = "t-alice",
            });
            ctx.SaveChanges();
        }

        // The owning tenant sees its row (sanity - the write landed).
        using (var scoped = db.CreateContext())
        {
            Assert.Single(scoped.MissionNotes.ToList());
        }

        // The unscoped context is null-tenant, so the global filter (tenant_id == null) matches nothing:
        // a scoped table read through it returns EMPTY - fail closed, never the leftover tenant's rows.
        using (var unscoped = db.CreateUnscopedContext())
        {
            Assert.Empty(unscoped.MissionNotes.ToList());
        }
    }
}
