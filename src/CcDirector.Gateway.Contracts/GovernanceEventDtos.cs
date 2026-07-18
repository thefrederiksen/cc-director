namespace CcDirector.Gateway.Contracts;

/// <summary>The kinds of subject a governance event describes. A session transition keys on the session GUID;
/// a run transition keys on the workflow run id.</summary>
public static class GovernanceEventSubject
{
    public const string Session = "session";
    public const string Run = "run";

    public static readonly string[] All = { Session, Run };
}

/// <summary>
/// The state vocabulary of the append-only ledger (issue #1771, spine item 2). These are the transitions a
/// duration report reads: the gap between entering "active" and the next state is active time; between
/// "waiting-on-human" and "recovered" is attention-burden wait. Deliberately small and shared across session
/// and run subjects - a run can be active, blocked, or waiting-on-human just as a session can.
/// </summary>
public static class GovernanceEventState
{
    /// <summary>Doing work - the agent is producing.</summary>
    public const string Active = "active";

    /// <summary>Alive but not producing - no active turn, not waiting on anyone.</summary>
    public const string Idle = "idle";

    /// <summary>Stopped, needs the owner - the attention-burden state.</summary>
    public const string WaitingOnHuman = "waiting-on-human";

    /// <summary>Stopped on a permission/approval prompt - waiting for a grant, not a human decision on the work.</summary>
    public const string WaitingOnPermission = "waiting-on-permission";

    /// <summary>Stuck - an error, a loop, an unmet dependency - not making progress and not waiting on a person.</summary>
    public const string Blocked = "blocked";

    /// <summary>Came back from a wait or a block - the close of a waiting/blocked interval.</summary>
    public const string Recovered = "recovered";

    public static readonly string[] All =
    {
        Active, Idle, WaitingOnHuman, WaitingOnPermission, Blocked, Recovered,
    };
}

/// <summary>
/// One immutable transition on the governance ledger. Append-only: there is no update or delete, so this DTO
/// is both the write acknowledgement and the read row.
/// </summary>
public sealed class GovernanceEventDto
{
    public Guid Id { get; set; }

    /// <summary>"session" or "run" - see <see cref="GovernanceEventSubject"/>.</summary>
    public string SubjectKind { get; set; } = "";

    /// <summary>The canonical fleet session GUID (set for a session subject; an optional join hint on a run subject).</summary>
    public string? SessionId { get; set; }

    /// <summary>The workflow run id (set for a run subject; an optional join hint on a session subject).</summary>
    public Guid? RunId { get; set; }

    /// <summary>The state entered - see <see cref="GovernanceEventState"/>.</summary>
    public string State { get; set; } = "";

    /// <summary>Why, in plain words. Never prompt content.</summary>
    public string? Reason { get; set; }

    /// <summary>When the transition happened.</summary>
    public DateTime OccurredUtc { get; set; }

    /// <summary>When the Gateway recorded it (server-stamped).</summary>
    public DateTime RecordedUtc { get; set; }
}

/// <summary>
/// Body of an append to the ledger. One request records one transition. <see cref="OccurredUtc"/> is optional:
/// when the caller does not supply it, the Gateway stamps the append time. The recorded time is always
/// server-stamped and never accepted from the caller.
///
/// Exactly one subject key is required, matching <see cref="SubjectKind"/>: a "session" event needs
/// <see cref="SessionId"/>; a "run" event needs <see cref="RunId"/>. The other key is optional (a denormalized
/// join hint).
/// </summary>
public sealed class AppendGovernanceEventRequest
{
    public string? SubjectKind { get; set; }
    public string? SessionId { get; set; }
    public Guid? RunId { get; set; }
    public string? State { get; set; }
    public string? Reason { get; set; }

    /// <summary>When the transition happened; null to let the Gateway stamp the append time.</summary>
    public DateTime? OccurredUtc { get; set; }
}

/// <summary>
/// Body of a batched append: many transitions in one call. The Gateway writes them in one unit of work.
/// A single invalid entry rejects the whole batch (the ledger never lands a half-batch), so a caller reporting
/// a burst of transitions learns of a bad row rather than silently dropping it.
/// </summary>
public sealed class AppendGovernanceEventsBatchRequest
{
    public List<AppendGovernanceEventRequest> Events { get; set; } = new();
}
