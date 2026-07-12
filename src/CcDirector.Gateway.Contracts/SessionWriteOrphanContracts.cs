namespace CcDirector.Gateway.Contracts;

// Gateway Cleanup mission, Phase 0 (Wave 4a): the request and response shapes for the four director-level
// WRITE verbs moved onto the tunnel command surface - repo-delete, interrupted-dismiss, interrupted-remove,
// and backfill-numbers. These are ADDITIVE: the request DTOs name the exact route arguments (the DELETE
// /repos ?path=, the /interrupted route segments) that have no home on DirectorCommand, so they ride in the
// command payload; the response DTOs reproduce the exact anonymous-object wire shapes the old REST lambdas
// returned, so the tunnel verb and the re-pointed REST route serialize identical JSON. Kept in one new file
// so no shared Contracts file is edited.

/// <summary>
/// DELETE /repos request. Carries the required <c>path</c> query-string argument the old REST route took: the
/// repository to remove from the recent-repository registry. A null / blank path is the route's own
/// BadRequest, exactly as before.
/// </summary>
public sealed class RepoDeleteRequest
{
    /// <summary>The repository folder path to remove from the recent list.</summary>
    public string? Path { get; set; }
}

/// <summary>
/// DELETE /repos response. Byte-identical to the <c>{ removed }</c> object the REST route returned: true when
/// the registry held the path and dropped it, false when it did not (or when no registry was wired). Both are
/// a 200.
/// </summary>
public sealed class RepoDeleteResponse
{
    /// <summary>True when the path was in the registry and was removed; false otherwise.</summary>
    public bool Removed { get; set; }
}

/// <summary>
/// DELETE /interrupted/{deadDirectorId}/{deadPid} request. Carries the two route segments the old REST route
/// took: the dead Director's id and process id, identifying the claimed crash-journal recovery to dismiss.
/// </summary>
public sealed class InterruptedDismissRequest
{
    /// <summary>The dead Director's id whose crash journal is being dismissed.</summary>
    public string DeadDirectorId { get; set; } = "";

    /// <summary>The dead Director's process id whose crash journal is being dismissed.</summary>
    public int DeadPid { get; set; }
}

/// <summary>
/// DELETE /interrupted/{deadDirectorId}/{deadPid} response. Byte-identical to the <c>{ dismissed = true }</c>
/// object the REST route returned on success. A journal that does not exist is the route's 404 (carried as a
/// NotFound command result), not a body.
/// </summary>
public sealed class InterruptedDismissResponse
{
    /// <summary>Always true on success (the not-found case is a NotFound, not this body).</summary>
    public bool Dismissed { get; set; }
}

/// <summary>
/// DELETE /interrupted/{deadDirectorId}/{deadPid}/sessions/{sessionId} request. Carries the three route
/// segments the old REST route took: the dead Director's id and process id, plus the one recoverable session
/// id to remove from that journal (the rest of the journal stays).
/// </summary>
public sealed class InterruptedRemoveRequest
{
    /// <summary>The dead Director's id whose crash journal is being edited.</summary>
    public string DeadDirectorId { get; set; } = "";

    /// <summary>The dead Director's process id whose crash journal is being edited.</summary>
    public int DeadPid { get; set; }

    /// <summary>The recoverable session id to remove from that journal.</summary>
    public string SessionId { get; set; } = "";
}

/// <summary>
/// DELETE /interrupted/{deadDirectorId}/{deadPid}/sessions/{sessionId} response. Byte-identical to the
/// <c>{ removed = true }</c> object the REST route returned on success. A session that is not in the journal
/// is the route's 404 (carried as a NotFound command result), not a body.
/// </summary>
public sealed class InterruptedRemoveResponse
{
    /// <summary>Always true on success (the not-found case is a NotFound, not this body).</summary>
    public bool Removed { get; set; }
}

/// <summary>
/// POST /admin/backfill-numbers response. Byte-identical to the <c>{ assigned }</c> object the REST route
/// returned: the count of sessions newly given a rail number. Idempotent - a second call returns 0. Always a
/// 200 (the verb takes no input and has no failure branch).
/// </summary>
public sealed class BackfillNumbersResponse
{
    /// <summary>The count of sessions newly numbered by this call.</summary>
    public int Assigned { get; set; }
}
