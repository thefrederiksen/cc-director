namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// The last cumulative agent-to-agent counts seen for a live session (<c>agent_driven_highwater</c>),
/// high-watered exactly like the human buckets - only the increase counts, and a reported count that DROPPED
/// is fresh activity from zero.
///
/// Carried forward UNCHANGED from SQLite schema version 5, including the composite primary key
/// (<c>tenant</c>, <c>session_id</c>). Keyed by SESSION ALONE (plus tenant), unlike
/// <see cref="SessionHighwaterEntity"/> which is keyed by session AND modality AND surface - the shapes
/// genuinely disagree, which is one of the two reasons this lane has its own tables at all.
///
/// A READ-MODIFY-WRITE PATH: every write must be an explicit ON CONFLICT DO UPDATE, never a change-tracked
/// read-then-save. See <see cref="SessionHighwaterEntity"/>.
/// </summary>
public sealed class AgentDrivenHighwaterEntity
{
    /// <summary>The owning tenant (<c>tenant</c>). Part of the primary key.</summary>
    public string Tenant { get; set; } = "";

    /// <summary>The session (<c>session_id</c>). Part of the primary key.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The highest agent-driven turn count seen (<c>turns</c>).</summary>
    public long Turns { get; set; }

    /// <summary>The highest agent-driven character count seen (<c>chars</c>).</summary>
    public long Chars { get; set; }
}
