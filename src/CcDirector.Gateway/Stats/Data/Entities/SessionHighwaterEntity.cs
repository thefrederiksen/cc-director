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
}
