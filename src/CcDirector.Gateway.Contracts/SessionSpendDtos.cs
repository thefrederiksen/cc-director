namespace CcDirector.Gateway.Contracts;

/// <summary>
/// How a session's usage is billed - the LABEL that keeps the three spend buckets separate (issue #1771,
/// spine item 3). Subscription traffic is never turned into a dollar figure; only metered traffic carries a
/// dollar cost.
/// </summary>
public static class SessionBillingMode
{
    /// <summary>Covered by a subscription (a Claude Max/Pro plan, a ChatGPT plan): no marginal dollar cost,
    /// so NEVER a dollar figure. The usage counts in tokens, labelled as subscription-included.</summary>
    public const string SubscriptionIncluded = "subscription-included";

    /// <summary>Billed per token in real dollars (an API key): a metered dollar cost may be attached when a
    /// price is known.</summary>
    public const string Metered = "metered";

    /// <summary>Billing mode not determined. Treated like subscription for the no-fabricated-dollars rule
    /// (no dollar figure), and disclosed as unknown so it is never silently counted as free or as metered.</summary>
    public const string Unknown = "unknown";

    public static readonly string[] All = { SubscriptionIncluded, Metered, Unknown };
}

/// <summary>
/// A session's cumulative effort and honest spend. Three separate, labelled things: raw tokens (always the
/// truth of what the model processed), the billing-mode label, and metered dollars (only for metered traffic
/// with a known price). <see cref="TokensCaptured"/> discloses whether additive token spend was available at
/// all - false for drivers that report only a context gauge, whose token sums are UNKNOWN, not zero.
/// </summary>
public sealed class SessionSpendDto
{
    public string SessionId { get; set; } = "";
    public string AgentKind { get; set; } = "";
    public string? Model { get; set; }
    public string? RepoPath { get; set; }

    /// <summary>False when the driver reports no additive token spend (context gauge only): the token sums
    /// are then UNKNOWN, and a report must disclose the gap rather than read them as zero.</summary>
    public bool TokensCaptured { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }

    /// <summary>"subscription-included", "metered", or "unknown" - see <see cref="SessionBillingMode"/>.</summary>
    public string BillingMode { get; set; } = "";

    /// <summary>Metered dollar cost in micro-dollars, for metered traffic with a known price only; null means
    /// no dollar figure (subscription, unknown, or no price) - never a fabricated zero.</summary>
    public long? MeteredCostMicros { get; set; }

    public DateTime FirstObservedUtc { get; set; }
    public DateTime LastObservedUtc { get; set; }
}

/// <summary>
/// Body of a session-spend record/refresh. A Director reads the driver's cumulative usage locally and pushes
/// it here; the Gateway upserts the one row for the session (cumulative totals overwrite - they are running
/// sums, not deltas to add). The metered-dollar column is deliberately not settable here: per-session dollars
/// wait on the #1608 rate card, and subscription traffic has no dollar figure at all.
/// </summary>
public sealed class RecordSessionSpendRequest
{
    public string? SessionId { get; set; }
    public string? AgentKind { get; set; }
    public string? Model { get; set; }
    public string? RepoPath { get; set; }

    /// <summary>False when the driver reports no additive token spend (a context-gauge-only driver): the token
    /// sums are then recorded as UNKNOWN coverage, not as real zeros.</summary>
    public bool TokensCaptured { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }

    /// <summary>"subscription-included", "metered", or "unknown". A coding-agent session on a subscription is
    /// "subscription-included" (never "unknown"), so a reader sees WHY there is no per-session dollar figure.</summary>
    public string? BillingMode { get; set; }
}

/// <summary>
/// A coverage summary over a set of sessions - the disclosure every spend report must carry so a low number
/// is never mistaken for a good one (issue #1771). Says how much of the fleet's spend is actually captured in
/// dollars versus tokens-only versus not captured at all.
/// </summary>
public sealed class SpendCoverageDto
{
    /// <summary>Total sessions in the slice.</summary>
    public int Sessions { get; set; }

    /// <summary>Sessions whose additive token spend was captured (<see cref="SessionSpendDto.TokensCaptured"/>).</summary>
    public int SessionsWithTokens { get; set; }

    /// <summary>Sessions with a metered dollar figure.</summary>
    public int SessionsWithMeteredDollars { get; set; }

    /// <summary>Sessions whose spend is subscription-included (counted in tokens, no dollar figure).</summary>
    public int SessionsSubscriptionIncluded { get; set; }

    /// <summary>Sessions whose additive spend could NOT be captured (context-gauge-only drivers) - the gap
    /// a report must show so the totals are read honestly.</summary>
    public int SessionsWithoutTokenCapture { get; set; }
}
