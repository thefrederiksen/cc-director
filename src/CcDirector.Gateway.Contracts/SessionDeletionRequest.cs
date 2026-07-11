namespace CcDirector.Gateway.Contracts;

/// <summary>
/// POST /sessions/{sid}/request-deletion request body. Flags the session for asynchronous
/// removal by the owning Director's deletion reaper. The body is optional; when omitted the
/// session is flagged with no reason. The common caller is a session flagging ITSELF once an
/// unattended run has nothing left for the user.
/// </summary>
public sealed class SessionDeletionRequest
{
    /// <summary>Short human reason for the teardown (e.g. "jobs-auto: nothing to report"),
    /// surfaced in the roster tooltip while the session winds down. Optional.</summary>
    public string? Reason { get; set; }
}
