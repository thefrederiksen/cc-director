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

    /// <summary>
    /// The REVERSE invariant (deny-by-default for new tables). The forward test above only checks entities
    /// that ALREADY derive from <see cref="TenantScopedEntity"/> - so an author who maps a brand-new table
    /// and simply FORGETS to derive from the base would sail past it with no tenant_id and no filter, which is
    /// exactly the cross-tenant leak this guard exists to prevent. This test flips it: EVERY mapped table must
    /// be tenant-scoped, and the only tables allowed to be global are an explicit allowlist (today just the
    /// <c>tenants</c> mapping table). Adding a new global table is therefore a CONSCIOUS act - a line added to
    /// this allowlist with a reason - never a silent omission.
    ///
    /// Owned types are excluded: an owned type mapped to a JSON column (the cron/workflow "sub-doc -> JSON in
    /// a column" pattern) is not its own table - it rides inside its owner's row and its owner's tenant_id, so
    /// it neither needs nor has a tenant_id of its own.
    /// </summary>
    [Fact]
    public void EveryMappedTable_IsTenantScopedOrAnExplicitlyAllowlistedGlobalTable()
    {
        // The ONLY tables permitted to be global (un-scoped). Growing this set must be deliberate: a new
        // entry here is an author consciously declaring "this table is not tenant data".
        // Each entry here is a table that is deliberately NOT tenant-scoped, with the reason it cannot be:
        //
        //  - TenantEntity is the account-to-tenant mapping itself - the table the tenant filter's values COME
        //    FROM. It is read by account subject BEFORE any tenant is resolved, so scoping it would be circular.
        //
        //  - EntitlementEntity is the paid-entitlement record, read at enrollment for the same reason and at
        //    the same point: keyed by account subject, consulted BEFORE a tenant is minted, and specifically in
        //    order to decide whether a tenant may be minted at all. It is also READ-ONLY here - it is written
        //    by the payment side as the service role and this Gateway holds SELECT and nothing more - and it
        //    carries no tenant content: one subject, one subscription state, one period end.
        var allowedGlobalTables = new HashSet<Type> { typeof(TenantEntity), typeof(EntitlementEntity) };

        var model = Model();
        var offenders = model.GetEntityTypes()
            // Owned types (JSON sub-documents) are part of their owner's table, not tables in their own right.
            .Where(e => !e.IsOwned())
            .Where(e => !typeof(TenantScopedEntity).IsAssignableFrom(e.ClrType))
            .Where(e => !allowedGlobalTables.Contains(e.ClrType))
            .Select(e => e.ClrType.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These mapped tables are neither tenant-scoped nor an allowlisted global table, so a tenant " +
            "could read another tenant's rows: " + string.Join(", ", offenders) +
            ". Derive the entity from TenantScopedEntity, or - only if it is genuinely global mapping data - " +
            "add it to allowedGlobalTables with a reason.");
    }
}
