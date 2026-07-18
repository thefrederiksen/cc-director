namespace CcDirector.Gateway.Contracts;

/// <summary>
/// A Mission: the named unit of work a pod of sessions is collectively chartered to accomplish
/// (see docs/new_architecture/mission-as-first-class-unit-of-work.md). A Mission is its OWN
/// persisted record - not merely an attachment field on a session - so it survives a Manager
/// restart and later anchors the cockpit map. Sessions ATTACH to a Mission by its
/// <see cref="MissionId"/>; that attachment (not the spawn tree) is what binds a pod together.
///
/// Role cardinality (one Architect, one Manager, N Workers) is enforced by the derived role model
/// (see <see cref="SessionRoles"/>); the Mission record deliberately does NOT store role seats.
/// </summary>
public sealed class MissionDto
{
    /// <summary>Stable identity of the Mission, minted at creation. This is the value sessions attach by.</summary>
    public Guid MissionId { get; set; }

    /// <summary>Human-friendly name of the Mission (e.g. "Session Lifecycle").</summary>
    public string MissionName { get; set; } = "";

    /// <summary>The parent Mission this one nests under (a tree of Missions), or null for a root Mission.</summary>
    public Guid? ParentMissionId { get; set; }

    /// <summary>ADDITIVE (Workflows mission, phase 4, issue #1771): the workflow run opened beside
    /// this Mission when it was created through the Gateway - a mission is a run of the built-in
    /// "mission" workflow. Null on reads that do not resolve it and on missions predating the spine.
    /// Existing clients ignore it.</summary>
    public Guid? WorkflowRunId { get; set; }
}

/// <summary>
/// Body of POST /missions on a Director's Control API: create a new Mission record.
/// </summary>
public sealed class NewMissionRequest
{
    /// <summary>Required. The Mission's human-friendly name. A blank name is rejected with HTTP 400.</summary>
    public string? MissionName { get; set; }

    /// <summary>Optional parent Mission this one nests under; null (default) creates a root Mission.</summary>
    public Guid? ParentMissionId { get; set; }
}

/// <summary>
/// Body of POST /sessions/{id}/mission: attach a session to a Mission. <see cref="MissionId"/> is the
/// Mission the session attaches to; a null/absent value DETACHES the session (clears its attachment),
/// mirroring how <see cref="SetRoleRequest"/> clears an explicit role.
/// </summary>
public sealed class SetMissionRequest
{
    public Guid? MissionId { get; set; }
}
