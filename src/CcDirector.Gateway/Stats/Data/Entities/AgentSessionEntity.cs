namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// Membership in the all-time set of sessions seen for an agent (<c>agent_session</c>).
///
/// Carried forward UNCHANGED from SQLite schema version 5, including the composite primary key
/// (<c>agent_id</c>, <c>session_id</c>) and the fact that it has **NO TENANT COLUMN**. It is partitioned
/// INDIRECTLY through <see cref="AgentIdentityEntity.AgentId"/>, a surrogate minted per tenant; the full
/// reasoning is written out once on <see cref="RepoSessionEntity"/> and is identical here. Adding a tenant
/// column is a behaviour change and is outside this port's scope.
///
/// DELIBERATELY NEVER PRUNED, like every membership set. Writes are insert-if-absent - ON CONFLICT DO
/// NOTHING, never a read-then-insert.
/// </summary>
public sealed class AgentSessionEntity
{
    /// <summary>The surrogate agent id (<c>agent_id</c>). Part of the primary key, and the thing that carries
    /// the tenant indirectly.</summary>
    public long AgentId { get; set; }

    /// <summary>The session (<c>session_id</c>). Part of the primary key.</summary>
    public string SessionId { get; set; } = "";
}
