namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One stored conversation message for one session - the row the turn-push mission puts behind Chat,
/// the transcript view, and the wingman (<c>docs/missions/turn-push-2026-09-01/brief.md</c>). Pushed by
/// the owning Director, stored once, read by everyone; the Gateway never asks a Director for
/// conversation text again.
///
/// Keyed by (tenant, session, generation key, ordinal). The generation KEY is a fixed-width digest of the
/// transcript source's identity (for Claude Code the resolved transcript path) - a digest rather than the
/// path itself because the path can be a kilobyte and a Postgres index entry cannot; the readable source
/// text lives once, on the session's head row. The ordinal is the message's contiguous position in the
/// generation. Rows of an older generation stay until retention removes them, so a /clear does not erase
/// what was said before it.
/// </summary>
public sealed class SessionTurnEntity : TenantScopedEntity
{
    public string SessionId { get; set; } = "";

    /// <summary>The generation's fixed-width key (a SHA-256 hex digest of the source identity).</summary>
    public string Generation { get; set; } = "";

    public int Ordinal { get; set; }

    public string DirectorId { get; set; } = "";

    /// <summary>"User" or "Assistant".</summary>
    public string Role { get; set; } = "";

    /// <summary>The message's parts (text, thinking, tool use, tool result) as a JSON array of
    /// <c>HistoryPartDto</c> - the bulky sub-document in one column, the CronJob pattern.</summary>
    public string PartsJson { get; set; } = "[]";

    public DateTime? TimestampUtc { get; set; }

    public string? ContextId { get; set; }

    public bool IsMeta { get; set; }

    public bool IsSidechain { get; set; }

    /// <summary>When this Gateway received the row.</summary>
    public DateTime ReceivedAtUtc { get; set; }
}

/// <summary>
/// One row per session: which generation the session is currently on, how many contiguous messages of it
/// the Gateway holds (the watermark a Director resumes from), and the per-session facts a history read
/// needs that are not per message - the agent, whether it is supported, and the transcript-derived
/// history state the Director computed at push time. Retention is measured from this row's
/// <see cref="UpdatedAtUtc"/> and removes the session's turns with it, whole, so the stored prefix is
/// never cut in the middle.
/// </summary>
public sealed class SessionTurnHeadEntity : TenantScopedEntity
{
    public string SessionId { get; set; } = "";

    public string DirectorId { get; set; } = "";

    /// <summary>The current generation's fixed-width key.</summary>
    public string Generation { get; set; } = "";

    /// <summary>The current generation's source identity as the Director sent it (the transcript path).</summary>
    public string GenerationSource { get; set; } = "";

    /// <summary>When the Director first observed the current generation. A push for a generation whose
    /// start is OLDER than this does not switch the session - it is a late arrival from a source the
    /// session has already left.</summary>
    public DateTime GenerationStartedUtc { get; set; }

    /// <summary>The length of the contiguous prefix of <see cref="Generation"/> held - the next ordinal
    /// the Director should send.</summary>
    public int Count { get; set; }

    public string Agent { get; set; } = "";

    public bool IsSupported { get; set; } = true;

    public bool IsRawText { get; set; }

    public string? HistoryState { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Bumped on every write to this row and mapped as the concurrency token, so two Gateway
    /// processes cannot both act on the same stale head - the second write fails and re-decides.</summary>
    public long Revision { get; set; }
}
