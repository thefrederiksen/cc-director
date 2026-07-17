namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// The base for every EF entity in the Gateway data layer. Carries the <see cref="TenantId"/> column that
/// scopes a row to one tenant, so the common tenant conventions - the <c>tenant_id</c> column and the
/// global query filter that keeps one tenant from reading another's rows - can be applied uniformly to
/// every entity that derives from this, rather than repeated per store.
///
/// On the local (single-tenant) install every row is written and read as <see cref="Core.Tenancy.TenantId.Local"/>
/// ("local"), so behavior is identical to a store with no tenant column at all. The value is set by the
/// store on write from the ambient <see cref="Core.Tenancy.ITenantContext"/>; a store method never takes a
/// tenant id as a parameter (the ambient context plus the global query filter is the mechanism).
/// </summary>
public abstract class TenantScopedEntity
{
    /// <summary>The tenant this row belongs to (mapped to the <c>tenant_id</c> column). Set on write from
    /// the ambient tenant context; never null or empty for a persisted row.</summary>
    public string TenantId { get; set; } = "";
}
