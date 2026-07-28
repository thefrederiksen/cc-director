namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One tenant's on/off choice for a BUILT-IN skill: a single (tenant, skill id) -&gt; enabled row in
/// the <c>skill_tenant_overrides</c> table. The built-ins live ONCE in the shared library partition
/// and are read-only to tenants, so a tenant switching one off cannot touch the shared head row - the
/// choice lands here, in the tenant's own partition, and the read paths fold it over the library
/// value. An ABSENT row means "no choice made" - the skill serves with the library's shipped state,
/// which is exactly what a brand-new tenant gets with zero provisioning.
///
/// User-owned skills do NOT use this table: their head row lives in the tenant's own partition, so
/// the head-row switch remains their single source of truth. One skill id has exactly one authority
/// for its switch - the head row when the tenant owns it, this override when the library does.
///
/// KEYING mirrors <see cref="WorkflowTenantOverrideEntity"/>: the skill id is namespaced per tenant,
/// so the primary key is the COMPOSITE (tenant_id, SkillId). <c>tenant_id</c> and the deny-by-default
/// global query filter are inherited from <see cref="TenantScopedEntity"/>.
/// </summary>
public sealed class SkillTenantOverrideEntity : TenantScopedEntity
{
    /// <summary>The built-in skill this choice applies to (e.g. "move-session"). Part of the composite
    /// primary key with <c>tenant_id</c>; ordinally compared (SQLite BINARY / Postgres "C").</summary>
    public string SkillId { get; set; } = "";

    /// <summary>The tenant's choice: false leaves the built-in out of this tenant's briefings and
    /// refuses its default fetch FOR THIS TENANT ONLY; true restores it. Never deletes anything.</summary>
    public bool Enabled { get; set; }

    /// <summary>Who flipped it - a governance change has an actor, always.</summary>
    public string UpdatedBy { get; set; } = "";

    /// <summary>When the choice was last written (UTC).</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
