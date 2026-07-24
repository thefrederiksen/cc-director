namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One tenant's on/off choice for a BUILT-IN workflow: a single (tenant, workflow id) -&gt; enabled row
/// in the <c>workflow_tenant_overrides</c> table (Shared Workflow Library phase 2, devthrottle_internal
/// issue 514). The built-ins live ONCE in the shared library partition and are read-only to tenants,
/// so a tenant flipping one off cannot touch the shared head row - the flip lands here, in the
/// tenant's own partition, and the read paths fold it over the library value. An ABSENT row means
/// "no choice made" - the workflow serves with the library's shipped state, which is exactly what a
/// brand-new tenant gets with zero provisioning.
///
/// User-owned workflows do NOT use this table: their head row lives in the tenant's own partition, so
/// the existing head-row switch remains their single source of truth. One workflow id has exactly one
/// authority for its switch - the head row when the tenant owns it, this override when the library does.
///
/// KEYING mirrors <see cref="TenantSettingEntity"/>: the workflow id is namespaced per tenant, so the
/// primary key is the COMPOSITE (tenant_id, WorkflowId). <c>tenant_id</c> and the deny-by-default
/// global query filter are inherited from <see cref="TenantScopedEntity"/>.
/// </summary>
public sealed class WorkflowTenantOverrideEntity : TenantScopedEntity
{
    /// <summary>The built-in workflow this choice applies to (e.g. "mission"). Part of the composite
    /// primary key with <c>tenant_id</c>; ordinally compared (SQLite BINARY / Postgres "C").</summary>
    public string WorkflowId { get; set; } = "";

    /// <summary>The tenant's choice: false hides the built-in from briefings and refuses default
    /// conduct reads and new runs FOR THIS TENANT ONLY; true restores it. Never deletes anything.</summary>
    public bool Enabled { get; set; }

    /// <summary>Who flipped it (a governance change has an actor, always - the run-acceptance posture).</summary>
    public string UpdatedBy { get; set; } = "";

    /// <summary>When the choice was last written (UTC).</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
