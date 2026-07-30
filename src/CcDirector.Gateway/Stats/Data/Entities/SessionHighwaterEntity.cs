namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// The last per-bucket counts seen for a live session (<c>session_highwater</c>), so only the INCREASE is
/// folded. This is what makes re-reading a roster safe and what lets counts survive a Director or Gateway
/// restart without double counting. A reported count that DROPPED (a Director restarted this session id) is
/// fresh activity from zero, never a negative.
///
/// Carried forward UNCHANGED from SQLite schema version 5, including the composite primary key
/// (<c>tenant</c>, <c>session_id</c>, <c>modality</c>, <c>surface</c>). The tenant joined that key in version
/// 5 for a reason worth restating: without it, two tenants pushing the same bare session id collide on the
/// key and one silently overwrites the other's high-water.
///
/// THIS IS A READ-MODIFY-WRITE PATH AND IT IS WHY THE UPSERT RULING EXISTS. Every write to it must be an
/// explicit ON CONFLICT DO UPDATE, never a change-tracked read-then-save: change tracking is a lost-update
/// generator under concurrent Postgres that single-writer SQLite never exposed.
///
/// AND IT IS WHY <see cref="PreviousTurns"/> AND <see cref="PreviousChars"/> EXIST. The general rule this
/// store is built on: NEVER LEARN WHAT YOU CHANGED FROM YOUR OWN PRIOR BELIEF - LEARN IT FROM THE RESPONSE OF
/// WHATEVER ARBITRATES. The arbiter of this row is the database, so the raise statement parks the value the
/// row held immediately BEFORE the raise into these two columns and returns both halves in the same atomic
/// statement. The writer then appends exactly the difference the database says it made, rather than the
/// difference it believed it was making. See <c>GatewayStatsWriter</c> for the whole argument.
/// </summary>
public sealed class SessionHighwaterEntity
{
    /// <summary>The owning tenant (<c>tenant</c>). Part of the primary key.</summary>
    public string Tenant { get; set; } = "";

    /// <summary>The session (<c>session_id</c>). Part of the primary key.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The input modality (<c>modality</c>). Part of the primary key.</summary>
    public string Modality { get; set; } = "";

    /// <summary>The input surface (<c>surface</c>). Part of the primary key.</summary>
    public string Surface { get; set; } = "";

    /// <summary>The highest turn count seen for this bucket (<c>turns</c>).</summary>
    public long Turns { get; set; }

    /// <summary>The highest character count seen for this bucket (<c>chars</c>).</summary>
    public long Chars { get; set; }

    /// <summary>What <see cref="Turns"/> held immediately before the most recent raise
    /// (<c>previous_turns</c>). Written by the raise statement itself so the same statement can return both
    /// halves; zero on the row's first insert, which makes the first raise's difference the whole reported
    /// count. Not a statistic anybody reads - it is how a writer learns what IT changed.</summary>
    public long PreviousTurns { get; set; }

    /// <summary>What <see cref="Chars"/> held immediately before the most recent raise
    /// (<c>previous_chars</c>). See <see cref="PreviousTurns"/>.</summary>
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
