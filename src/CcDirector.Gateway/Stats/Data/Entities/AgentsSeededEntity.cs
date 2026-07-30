namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// Membership in the set of sessions whose already-counted turns have been attributed to their agent
/// (<c>agents_seeded</c>).
///
/// Carried forward UNCHANGED from SQLite schema version 5, including the composite primary key
/// (<c>tenant</c>, <c>session_id</c>).
///
/// THIS IS LIVE BEHAVIOUR, NOT MIGRATION SCAFFOLDING, and the distinction nearly cost the owner's agent
/// numbers once already. On a fresh store the first-fold back-fill contributes nothing, because a new
/// session's high-water is empty - so this table looks like dead weight. But
/// <see cref="SessionHighwaterEntity"/> PERSISTS across a Gateway restart, and without this set the first
/// fold after a restart would back-fill every live session a SECOND time and double every agent's turns. It
/// survives because of what it DOES, not because of what its name suggests it was for.
///
/// Writes are insert-if-absent - ON CONFLICT DO NOTHING, never a read-then-insert.
/// </summary>
public sealed class AgentsSeededEntity
{
    /// <summary>The owning tenant (<c>tenant</c>). Part of the primary key.</summary>
    public string Tenant { get; set; } = "";

    /// <summary>The session (<c>session_id</c>). Part of the primary key.</summary>
    public string SessionId { get; set; } = "";
}
