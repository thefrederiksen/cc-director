namespace CcDirector.Core.Sessions;

/// <summary>
/// A Mission: the named unit of work a pod of sessions is collectively chartered to accomplish
/// (see docs/new_architecture/mission-as-first-class-unit-of-work.md). A Mission is its OWN persisted
/// record - not merely an attachment field on a session - so it survives a Director/Manager restart and
/// later anchors the cockpit map. Sessions ATTACH to a Mission by its <see cref="MissionId"/>.
///
/// Role cardinality (one Architect, one Manager, N Workers) is enforced by the derived role model, so a
/// Mission deliberately stores NO role seats - only its identity, name, and optional parent for nesting.
/// </summary>
public sealed class Mission
{
    /// <summary>Stable identity of the Mission, minted at creation. Sessions attach by this value.</summary>
    public Guid MissionId { get; set; }

    /// <summary>Human-friendly name of the Mission (e.g. "Session Lifecycle").</summary>
    public string MissionName { get; set; } = string.Empty;

    /// <summary>The parent Mission this one nests under (a tree of Missions), or null for a root Mission.</summary>
    public Guid? ParentMissionId { get; set; }

    /// <summary>UTC timestamp the Mission was created. Used for stable list ordering.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
