using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The tenancy CI guard (Hosted Multi-Tenancy increment 1) - the check PostHog learned the hard way. It
/// reflects the actual EF model and fails the build if ANY tenant-scoped table is missing its tenant key or
/// its isolation filter. A future store that forgets to derive from <see cref="TenantScopedEntity"/>, or a
/// change that drops the <c>tenant_id</c> column or the global query filter, turns red here instead of
/// silently letting one tenant read another's rows.
///
/// The rule is exact and two-directional:
///  - EVERY entity derived from <see cref="TenantScopedEntity"/> MUST map a <c>tenant_id</c> column AND carry
///    a global query filter (deny-by-default isolation).
///  - The GLOBAL mapping table <see cref="TenantEntity"/> MUST NOT be scoped: it is the table the filter's
///    tenant values come from, so it carries no <c>tenant_id</c> column and no query filter.
/// </summary>
public sealed class TenantScopeGuardTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private IModel Model()
    {
        using var ctx = _harness.Open().CreateContext();
        return ctx.Model;
    }

    [Fact]
    public void EveryTenantScopedEntity_HasTenantIdColumnAndQueryFilter()
    {
        var model = Model();
        var scoped = model.GetEntityTypes()
            .Where(e => typeof(TenantScopedEntity).IsAssignableFrom(e.ClrType))
            .ToList();

        // Sanity: the scan actually found the scoped stores (guard against a reflection no-op passing green).
        Assert.NotEmpty(scoped);

        foreach (var entity in scoped)
        {
            var tenantColumn = entity.GetProperties()
                .FirstOrDefault(p => string.Equals(p.GetColumnName(), "tenant_id", StringComparison.Ordinal));
            Assert.True(tenantColumn is not null,
                $"{entity.ClrType.Name} derives from TenantScopedEntity but has no tenant_id column.");

            Assert.True(entity.GetQueryFilter() is not null,
                $"{entity.ClrType.Name} derives from TenantScopedEntity but has no global query filter " +
                "(a tenant could read another tenant's rows).");
        }
    }

    [Fact]
    public void TheTenantMappingTable_IsNotItselfTenantScoped()
    {
        var model = Model();
        var tenants = model.FindEntityType(typeof(TenantEntity));
        Assert.NotNull(tenants);

        // It is the global mapping table: no tenant_id column, no query filter.
        Assert.DoesNotContain(tenants!.GetProperties(),
            p => string.Equals(p.GetColumnName(), "tenant_id", StringComparison.Ordinal));
        Assert.Null(tenants.GetQueryFilter());
        Assert.False(typeof(TenantScopedEntity).IsAssignableFrom(tenants.ClrType));
    }
}
