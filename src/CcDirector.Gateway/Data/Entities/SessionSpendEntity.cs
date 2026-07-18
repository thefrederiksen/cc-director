namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// A session's cumulative effort and honest spend, in the <c>session_spend</c> table - the driver-normalized
/// spend layer of the governance outcome spine (issue #1771, spine item 3). One row per fleet session, keyed
/// on the canonical Director-minted session GUID - the SAME id <c>workflow_runs.Participants[].SessionId</c>
/// uses, so a run's spend is the sum over the sessions that joined it (the run -> effort join governance needs).
///
/// The row keeps THREE things separate and labelled, and never blends them - the governance rule that a low
/// number must never be mistaken for a good one (issue #1771):
///  1. RAW TOKENS (<see cref="InputTokens"/> etc.) - the additive token sums, captured for any driver that
///     reports cumulative usage. This is always the truth of "how much did the model process".
///  2. SUBSCRIPTION-INCLUDED vs METERED - <see cref="BillingMode"/> LABELS which bucket the raw tokens fall
///     in. Subscription traffic has no marginal dollar cost, so it is never turned into a dollar figure.
///  3. METERED DOLLARS (<see cref="MeteredCostMicros"/>) - a real dollar cost ONLY for metered (API-key)
///     traffic with a known price. Micro-dollars (integer smallest-units), never a bare decimal. NULL means
///     "no dollar figure" (subscription, unknown mode, or no price) - which is NOT the same as zero.
///
/// Coverage is explicit: <see cref="TokensCaptured"/> is false for a driver that reports only a context
/// gauge (Codex, Pi) and not additive spend. When it is false the token sums are not real zeros - they are
/// UNKNOWN, and every report must disclose the gap rather than read it as "this agent was free".
/// </summary>
public sealed class SessionSpendEntity : TenantScopedEntity
{
    /// <summary>The canonical fleet session GUID - the natural primary key (one row per session, one agent
    /// kind per session for its life). The same id run participants and the event ledger key on.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The agent family that ran this session ("claude", "codex", "pi", ...). Spend is never
    /// compared across families without <see cref="TokensCaptured"/> disclosed - additive spend is not
    /// available for every driver.</summary>
    public string AgentKind { get; set; } = "";

    /// <summary>The model the session was last observed running (the driver's current-model report, which can
    /// change mid-session). Null when the driver does not report a model.</summary>
    public string? Model { get; set; }

    /// <summary>The repository the session worked in, when known - for repo-sliced spend reporting.</summary>
    public string? RepoPath { get; set; }

    /// <summary>True when the driver reports additive cumulative token usage (capability TokenUsage). When
    /// false (Codex, Pi - context gauge only) the token sums below are UNKNOWN, not zero: disclose the gap.</summary>
    public bool TokensCaptured { get; set; }

    /// <summary>Cumulative input tokens. Meaningful only when <see cref="TokensCaptured"/> is true.</summary>
    public long InputTokens { get; set; }

    /// <summary>Cumulative output tokens. Meaningful only when <see cref="TokensCaptured"/> is true.</summary>
    public long OutputTokens { get; set; }

    /// <summary>Cumulative cache-read input tokens. Meaningful only when <see cref="TokensCaptured"/> is true.</summary>
    public long CacheReadTokens { get; set; }

    /// <summary>Cumulative cache-creation input tokens. Meaningful only when <see cref="TokensCaptured"/> is true.</summary>
    public long CacheCreationTokens { get; set; }

    /// <summary>Which bucket the raw tokens fall in: "subscription-included", "metered", or "unknown". This
    /// LABEL is what keeps subscription usage from ever being rendered as a dollar figure.</summary>
    public string BillingMode { get; set; } = "";

    /// <summary>The metered dollar cost in micro-dollars (integer smallest-units), for METERED traffic with a
    /// known price ONLY. NULL for subscription, unknown mode, or when no price is available - a null is "no
    /// dollar figure", deliberately distinct from a real zero. Never fabricated for subscription traffic.</summary>
    public long? MeteredCostMicros { get; set; }

    /// <summary>When this session's spend was first recorded.</summary>
    public DateTime FirstObservedUtc { get; set; }

    /// <summary>When the cumulative totals were last refreshed from the driver.</summary>
    public DateTime LastObservedUtc { get; set; }
}
