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

    /// <summary>What <see cref="Turns"/> held immediately before the most recent raise
    /// (<c>previous_turns</c>), so the raise statement can return what it changed rather than leaving the
    /// writer to infer it. See <see cref="SessionHighwaterEntity.PreviousTurns"/>.</summary>
    public long PreviousTurns { get; set; }

    /// <summary>What <see cref="Chars"/> held immediately before the most recent raise
    /// (<c>previous_chars</c>).</summary>
    public long PreviousChars { get; set; }

    /// <summary>Which INCARNATION of this session's tally the row is counting (<c>generation</c>). It advances
    /// by one every time the store adopts a RESET - a Director restarting this session id and counting from
    /// zero again. A writer sends the generation it believed the row was on; a reading whose belief comes from
    /// an older generation is a straggler from a life that has already ended, and it contributes nothing.
    ///
    /// Without it, a delayed pre-reset reading is indistinguishable from ordinary growth after the reset, and
    /// it is counted a second time - permanently, because nothing rewrites an appended delta, and by an amount
    /// that scales with the pre-reset watermark. See <c>GatewayStatsWriter</c>.</summary>
    public long Generation { get; set; }
}
