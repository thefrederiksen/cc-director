namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One session's durable work-history record, in the <c>session_history</c> table (issue #2194,
/// standing on the #1862 ledger design). One row per fleet session, keyed on the Director-minted
/// session GUID, written the FIRST time the Gateway sees the session on the push stream and kept
/// fresh WHILE IT RUNS - never produced at the end. A session killed by a power cut therefore still
/// leaves a usable record; exactly the sessions that end badly are the ones this table must not lose.
///
/// The ENDING is always a Gateway ruling (the dumb-client rule): a farewell path stamps
/// closed/finished/director-stopped, and the history sweep CONCLUDES "interrupted" for an open row
/// whose Director has been silent past the threshold - the absence of a goodbye is the evidence.
/// The ruling is revisable: a session that reappears on the stream reopens its row.
///
/// HONESTY (issue #2157): <see cref="StartedAtUtc"/> is the Director-measured creation time riding
/// every push - a real measurement, persisted so a Gateway restart does not erase when a session
/// began. <see cref="LastSeenUtc"/> is refreshed by the throttled recorder (at most once per
/// freshness interval per session, plus material changes), so for an interrupted session the end is
/// honestly "last seen", never an invented instant.
///
/// The summary fields hold the session's own sealed account (clean close) or the Gateway's
/// prompt-log-derived account (dirty end, MARKED partial). The list fields are JSON arrays of
/// strings in TEXT columns - structured enough to query through the API, portable across SQLite and
/// Postgres. Prompt-derived text is customer content: rows are tenant-scoped by the global query
/// filter exactly like the dictation transcripts that already store text in this database.
///
/// Retention: 90 days, pruned by the history sweep. The consumable range (the 30-day API) sits well
/// inside it.
/// </summary>
public sealed class SessionHistoryEntity : TenantScopedEntity
{
    /// <summary>The Director-minted session GUID - natural key, composite with the tenant. The id is
    /// CALLER-SUPPLIED (it arrives on the push stream), so a SessionId-only key would let one tenant
    /// squat an id for every other tenant - the session_spend reasoning exactly.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The Gateway-issued human session number (100-999), when known.</summary>
    public int? SessionNumber { get; set; }

    public string? SessionName { get; set; }
    public string? MachineName { get; set; }
    public string DirectorId { get; set; } = "";
    public string? RepoPath { get; set; }

    /// <summary>owner/repo resolved from the origin remote, when known. The roll-up grouping key.</summary>
    public string? RepoName { get; set; }

    public string? AgentKind { get; set; }
    public string? Model { get; set; }
    public string? MissionName { get; set; }

    /// <summary>The declared role (Architect/Manager/Worker...), when the session has one.</summary>
    public string? SessionRole { get; set; }

    /// <summary>Director-measured session creation time (SessionDto.CreatedAt) - a real measurement.</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>Last terminal activity as reported by the Director, when known.</summary>
    public DateTime? LastActivityUtc { get; set; }

    /// <summary>The last push in which the Gateway observed this session (throttled - see class doc).</summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>The session's last known activity state (Working/Idle/...), refreshed on the same
    /// throttle as <see cref="LastSeenUtc"/>. Display context for ended rows; open rows read live state
    /// from the roster, not from here.</summary>
    public string? LastActivityState { get; set; }

    /// <summary>Total input turns observed (operator plus agent-driven), from the pushed input stats.</summary>
    public long? TurnCount { get; set; }

    /// <summary>Completed AGENT turns (SessionDto.TurnCount, internal#625 phase 4): one flip to
    /// waiting-for-input equals one turn, counted incrementally on the Director. Distinct from
    /// <see cref="TurnCount"/>, which counts turns SUBMITTED to the session. Null when the owning
    /// Director predates the counter; a known value is never overwritten by null.</summary>
    public long? AgentTurnCount { get; set; }

    /// <summary>Total seconds the session spent waiting on the user, summed over CLOSED waiting
    /// stretches (SessionDto.CumulativeIdleSeconds, internal#625 phase 4), as last pushed. Null when
    /// the owning Director predates the clock.</summary>
    public double? CumulativeIdleSeconds { get; set; }

    /// <summary>The first user prompt, trimmed to one line - description source number two (#1862).
    /// Set once from the prompt-log ingest and never overwritten.</summary>
    public string? FirstPromptLine { get; set; }

    /// <summary>Null while open; one of <see cref="Contracts.SessionHistoryEndings"/> once ended.</summary>
    public string? EndingKind { get; set; }

    /// <summary>The folded wording for the ending. Stamped with the kind; clients render it verbatim.</summary>
    public string? EndingLabel { get; set; }

    public DateTime? EndedAtUtc { get; set; }

    /// <summary>Null until a summary exists; one of <see cref="Contracts.SessionHistorySummaryKinds"/>.</summary>
    public string? SummaryKind { get; set; }

    /// <summary>True when the summary describes a partial record - the session ended without a farewell.</summary>
    public bool SummaryIsPartial { get; set; }

    /// <summary>How many times the Gateway summariser has tried. Bounded (the sweep gives up and marks
    /// the summary unavailable after the cap) so a broken model path cannot bill forever.</summary>
    public int SummaryAttempts { get; set; }

    public string? SummaryText { get; set; }

    /// <summary>JSON array of strings; null when absent.</summary>
    public string? WhatWasBuiltJson { get; set; }

    /// <summary>JSON array of strings; null when absent.</summary>
    public string? LeftUnverifiedJson { get; set; }

    /// <summary>JSON array of strings; null when absent.</summary>
    public string? BranchesJson { get; set; }

    /// <summary>JSON array of strings; null when absent.</summary>
    public string? PullRequestsJson { get; set; }

    /// <summary>JSON array of strings; null when absent.</summary>
    public string? CommitsJson { get; set; }
}
