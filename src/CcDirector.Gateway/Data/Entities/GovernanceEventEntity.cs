namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One immutable state transition of a session or a run, in the <c>governance_events</c> table - the
/// append-only event ledger of the governance outcome spine (issue #1771, spine item 2). A row is written
/// once and never updated or deleted: the ledger is the durable record of WHEN a subject entered a state,
/// which is what lets a report compute active / idle / waiting DURATIONS. The merged run row
/// (<see cref="WorkflowRunEntity"/>) carries only started/completed timestamps; the questions "how long was
/// this session idle" and "how long did the fleet wait on a human this week" have no answer without this
/// ledger.
///
/// This is NOT the in-memory doorbell ring (<c>Events.DirectorEventLog</c>, issue #330), which is a capped
/// debug snapshot with no persistence. This table is the persisted governance timeline.
///
/// A transition is about exactly one subject, named by <see cref="SubjectKind"/>:
///  - a "session" transition keys on <see cref="SessionId"/> (the canonical Director-minted session GUID,
///    the same id run participants and every per-session statistic key on);
///  - a "run" transition keys on <see cref="RunId"/> (a value reference to <see cref="WorkflowRunEntity.Id"/> -
///    NOT a foreign key, because a run outlives archive/cleanup and the ledger must survive it).
/// The other key may also be stamped as a denormalized join hint (a session transition can carry the RunId
/// of the run the session was working, so a per-run duration rollup needs no participant lookup), but the
/// subject's own key is always present.
/// </summary>
public sealed class GovernanceEventEntity : TenantScopedEntity
{
    /// <summary>Primary key, minted in code - never a database default.</summary>
    public Guid Id { get; set; }

    /// <summary>What kind of thing transitioned: "session" or "run". The subject's own key
    /// (<see cref="SessionId"/> for a session, <see cref="RunId"/> for a run) is required for that kind.</summary>
    public string SubjectKind { get; set; } = "";

    /// <summary>The canonical fleet session GUID. Required for a session subject; an optional denormalized
    /// join hint on a run subject. Never any identifier other than the Director-minted session GUID.</summary>
    public string? SessionId { get; set; }

    /// <summary>The workflow run this transition belongs to (value reference to <see cref="WorkflowRunEntity.Id"/>,
    /// never a foreign key). Required for a run subject; an optional denormalized join hint on a session
    /// subject (the run the session was working when it transitioned).</summary>
    public Guid? RunId { get; set; }

    /// <summary>The state entered: active, idle, waiting-on-human, waiting-on-permission, blocked, recovered.</summary>
    public string State { get; set; } = "";

    /// <summary>Why the transition happened, in plain words (e.g. "permission prompt: bash", "owner replied").
    /// NEVER prompt content - the ledger records control flow, not what anyone typed (issue #1771 principle).</summary>
    public string? Reason { get; set; }

    /// <summary>When the transition actually happened. Caller-supplied (a Director reports a transition it
    /// observed a moment ago); defaults to the append time when the caller does not know it.</summary>
    public DateTime OccurredUtc { get; set; }

    /// <summary>When the Gateway appended the row. Server-stamped, never caller-supplied - it is the tie-breaker
    /// that orders two transitions sharing an <see cref="OccurredUtc"/>, and the audit fact of when we learned.</summary>
    public DateTime RecordedUtc { get; set; }
}
