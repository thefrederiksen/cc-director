namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One durable activity-ledger event, in the <c>activity_events</c> table - the trustworthy-Working-start
/// plan's evidence spine (docs/PLAN-trustworthy-working-start-2026-07-24.md). Append-only: a row records
/// what a producer OBSERVED (a submission, a state transition, terminal output on a settled session, a
/// transcript-proven turn, a snooze lifecycle decision) and is never updated or rewritten.
///
/// This is a DISTINCT ledger from <see cref="GovernanceEventEntity"/> (the governance duration spine) and
/// <see cref="GovernanceAuditEventEntity"/> (decisions and interventions): those record governance outcomes;
/// this one records the low-level activity EVIDENCE the shadow Working classifier is judged against, at a
/// different cardinality and with a bounded 30-day retention (the others keep governance history).
///
/// KEY: composite <c>(tenant_id, EventId)</c>. <see cref="EventId"/> is CALLER-SUPPLIED (the producer mints
/// it once before its outbox so a retried batch replays the same identity), so per the caller-supplied-key
/// doctrine the tenant must be in the primary key: one tenant replaying or colliding on an id can never
/// squat on, overwrite, or learn about another tenant's row.
///
/// <see cref="BoundedScreenDiff"/> may contain terminal content, which can contain secrets: it is
/// tenant-scoped customer data, bounded at the store boundary, and must never be written to ordinary
/// process logs.
/// </summary>
public sealed class ActivityEventEntity : TenantScopedEntity
{
    /// <summary>The producer-minted event identity (idempotency key). Part of the composite primary key.</summary>
    public Guid EventId { get; set; }

    /// <summary>The producer's own monotonic sequence (0 on Gateway-origin events).</summary>
    public long DirectorSequence { get; set; }

    /// <summary>When the event actually happened (UTC, producer-stamped, clamped to the append time).</summary>
    public DateTime OccurredUtc { get; set; }

    /// <summary>When the Gateway appended the row (server-stamped) - the tie-breaker and the fact of when
    /// we learned.</summary>
    public DateTime RecordedUtc { get; set; }

    /// <summary>The Director the event belongs to ("gateway" on Gateway-origin events with no known owner).</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>The Director session the event is about.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>Which machine the session runs on, when known.</summary>
    public string? Machine { get; set; }

    /// <summary>The agent kind of the session's driver, when known.</summary>
    public string? AgentKind { get; set; }

    /// <summary>The agent's own context id at the time, when known.</summary>
    public string? ContextId { get; set; }

    /// <summary>What happened - a value of <c>ActivityEventTypes</c> (validated at the store).</summary>
    public string EventType { get; set; } = "";

    /// <summary>The state before the event, when the event is a transition.</summary>
    public string? PreviousState { get; set; }

    /// <summary>The state after the event, when the event is a transition.</summary>
    public string? NewState { get; set; }

    /// <summary>Why - a value of <c>ActivityCauses</c> (validated at the store).</summary>
    public string Cause { get; set; } = "";

    /// <summary>A short structured control-flow note. NEVER prompt or terminal content.</summary>
    public string? Detail { get; set; }

    /// <summary>Where the input came from on a submission event.</summary>
    public string? InputOrigin { get; set; }

    /// <summary>The send source on a submission event.</summary>
    public string? SendSource { get; set; }

    /// <summary>The detector mode that ruled, on detector events.</summary>
    public string? DetectorMode { get; set; }

    /// <summary>The detector version that ruled.</summary>
    public string? DetectorVersion { get; set; }

    /// <summary>How many terminal bytes were seen, on terminal-output evidence events.</summary>
    public long? OutputByteCount { get; set; }

    /// <summary>Normalized screen-body hash before the output burst.</summary>
    public string? BeforeScreenHash { get; set; }

    /// <summary>Normalized screen-body hash after the output burst.</summary>
    public string? AfterScreenHash { get; set; }

    /// <summary>The bounded normalized changed-row diff. Tenant-scoped customer data; may contain secrets;
    /// bounded at the store; never process-logged.</summary>
    public string? BoundedScreenDiff { get; set; }

    // What the prompt's door knew at entry (source logging, 2026-09-05) - turn-submitted rows only, null elsewhere.
    public string? Route { get; set; }
    public string? IdentityKind { get; set; }
    public string? TranscriptId { get; set; }
    public string? SpokenSpans { get; set; }
    public string? ContentSha256 { get; set; }
    public long? ContentLength { get; set; }
}
