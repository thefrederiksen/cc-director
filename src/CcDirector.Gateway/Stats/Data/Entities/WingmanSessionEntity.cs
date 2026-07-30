namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// Membership in the all-time set of sessions that ever had voice mode on (<c>wingman_session</c>).
///
/// Carried forward UNCHANGED from SQLite schema version 5, including the composite primary key
/// (<c>tenant</c>, <c>session_id</c>).
///
/// DELIBERATELY NEVER PRUNED. This is a requirement to preserve, not a bug to fix. It is NOT
/// <c>COUNT(DISTINCT session_id)</c> over the delta table: that is exact only while every contributing row is
/// still present, so it stops being exact the moment pruning starts, and it cannot see a pre-cutover session
/// at all.
///
/// Writes are insert-if-absent - ON CONFLICT DO NOTHING, never a read-then-insert.
/// </summary>
public sealed class WingmanSessionEntity
{
    /// <summary>The owning tenant (<c>tenant</c>). Part of the primary key.</summary>
    public string Tenant { get; set; } = "";

    /// <summary>The session (<c>session_id</c>). Part of the primary key.</summary>
    public string SessionId { get; set; } = "";
}
