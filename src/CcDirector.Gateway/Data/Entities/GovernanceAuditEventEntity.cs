namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One structured governance audit event, in the <c>governance_audit_events</c> table - the intervention and
/// permission/sandbox audit trail of the governance outcome spine (issue #1771, spine item 4). Append-only:
/// a row is written once and never changed, because an audit trail you can rewrite is not an audit trail.
///
/// This is a DISTINCT ledger from <see cref="GovernanceEventEntity"/> (the activity/duration timeline). This
/// one records DECISIONS and INTERVENTIONS, in two categories (<see cref="Category"/>):
///  - "intervention" - the agent needed a human and what the human did: the need/ask, and a human rescue,
///    redirect, or cancel, and the resolution.
///  - "permission" - permission/sandbox posture: a permission request and its decision, the effective
///    sandbox/approval mode observed, and the start/end brackets of an elevated (dangerous) run (its duration
///    is derived from the pair, so the ledger stays purely event-based).
///
/// Everything here is an EXPLICITLY-RECORDED event, NEVER inferred from a prose transcript (issue #1771), and
/// <see cref="Detail"/> is a short control-flow note (a permission name, a mode, a redirect reason) - NEVER
/// prompt content. The <see cref="Actor"/> is the audit "who": the human identity on a human decision, the
/// agent on an agent event.
/// </summary>
public sealed class GovernanceAuditEventEntity : GatewayMintedKeyEntity
{
    /// <summary>The canonical fleet session GUID the event belongs to (audit events are session-scoped).
    /// The same id run participants, the event ledger, and session spend key on.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The workflow run the session was working, when known (value reference to
    /// <see cref="WorkflowRunEntity.Id"/>, never a foreign key - the audit trail outlives the run).</summary>
    public Guid? RunId { get; set; }

    /// <summary>"intervention" or "permission".</summary>
    public string Category { get; set; } = "";

    /// <summary>The specific event within the category (e.g. "needed", "human-rescued", "permission-requested",
    /// "permission-granted", "mode-observed", "elevated-run-started"). Validated against the category.</summary>
    public string EventType { get; set; } = "";

    /// <summary>Who acted: a human identity ("human:&lt;user&gt;") on a human decision, or the agent seat on an
    /// agent event. Required on human decisions and permission decisions - "who decided" is an audit fact that
    /// must never be null there.</summary>
    public string? Actor { get; set; }

    /// <summary>A short control-flow note describing the specific subject: the permission requested, the
    /// effective mode, the redirect reason. NEVER prompt content - the audit records what was decided, not
    /// what anyone typed.</summary>
    public string? Detail { get; set; }

    /// <summary>When the event actually happened. Caller-supplied; defaults to the append time.</summary>
    public DateTime OccurredUtc { get; set; }

    /// <summary>When the Gateway appended the row (server-stamped) - the tie-breaker that orders two events
    /// sharing an <see cref="OccurredUtc"/>, and the audit fact of when we learned.</summary>
    public DateTime RecordedUtc { get; set; }
}
