using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Stats.Data;

/// <summary>
/// Everything one observation wants to write, collected before ANY of it is written, so the in-memory mirror
/// is advanced only after the commit succeeds. Mutating the mirror as we go would mean a failed write leaves
/// the mirror believing a delta was recorded that is not in the store - it would never be folded again, and
/// the loss would be silent.
///
/// ONE BATCH IS ONE TENANT. The Director-statistics ingress is per tenant, so the producer stamps
/// <see cref="Tenant"/> once here and every row written and every mirror entry advanced by this batch is keyed
/// by it - never per row, and never guessed from a row's contents.
///
/// AN EMPTY BATCH IS AN IDLE POLL AND WRITES NOTHING. <see cref="IsEmpty"/> is what
/// <see cref="GatewayStatsWriter.Commit"/> tests before it creates a context or opens a transaction; that
/// silence is the property that keeps an unchanged roster poll free, and it is why the membership and identity
/// mirrors exist at all.
///
/// THIS BATCH CARRIES OBSERVATIONS, NOT DELTAS, AND THAT IS THE WHOLE POINT. It used to carry pre-computed
/// growth: the fold subtracted the store's counts from its own in-memory mirror and queued the difference as a
/// row to append. That made the fold's PRIVATE BELIEF the authority on what changed while the shared watermark
/// was arbitrated by the DATABASE, and those two cannot both be right - two hosted containers each measuring
/// growth from their own mirror append more in total than the watermark ever moves, and the all-time totals
/// drift upward with every interleave. So the fold now reports only what it SAW (the session's reported
/// cumulative counts) and what it BELIEVED the store held; the writer raises the watermark and the database
/// tells it what that raise actually changed. See <see cref="GatewayStatsWriter"/>.
/// </summary>
internal sealed class StatsWriteBatch
{
    public StatsWriteBatch(TenantId tenant, DateTime nowUtc, string hourKey)
    {
        Tenant = tenant;
        NowUtc = nowUtc;
        HourKey = hourKey;
    }

    /// <summary>The one tenant this whole batch belongs to (MTR-08).</summary>
    public TenantId Tenant { get; }

    public DateTime NowUtc { get; }

    /// <summary>The UTC clock hour every delta row this batch produces is filed under. One batch is one
    /// observation, so one hour serves all of them.</summary>
    public string HourKey { get; }

    /// <summary>
    /// One session bucket as OBSERVED: the cumulative counts the Director reported, the baseline this writer
    /// believed the store held, and the dimensions any row derived from it is filed under.
    ///
    /// <c>Believed*</c> is evidence, never authority. The database compares it with what the row ACTUALLY
    /// holds to tell two indistinguishable-looking cases apart: a reported count below the stored watermark is
    /// a genuine RESET (a Director restarted this session id and is counting fresh from zero) when the writer's
    /// baseline was current, and merely a STALE read that another writer has already overtaken when it was not.
    /// Neither the writer nor the fold gets to make that call - see <see cref="GatewayStatsWriter"/>.
    /// </summary>
    public readonly record struct BucketObservation(
        string SessionId, string Modality, string Surface, bool IsVoice,
        string Repo, string Checkout, string? Model, bool Wingman, string Agent,
        long ReportedTurns, long ReportedChars, long BelievedTurns, long BelievedChars);

    /// <summary>One session's agent-to-agent counts as observed (issue #1636), on its own lane. Same
    /// reported-plus-believed shape as <see cref="BucketObservation"/>, and same reason.</summary>
    public readonly record struct AgentDrivenObservation(
        string SessionId, string Agent,
        long ReportedTurns, long ReportedChars, long BelievedTurns, long BelievedChars);

    /// <summary>One session's cumulative token spend as observed (issue #1637). Four scalars, each
    /// high-watered independently, each reported alongside what this writer believed was stored.</summary>
    public readonly record struct TokenObservation(
        string SessionId, string? Model,
        long ReportedInput, long ReportedOutput, long ReportedCacheRead, long ReportedCacheCreation,
        long BelievedInput, long BelievedOutput, long BelievedCacheRead, long BelievedCacheCreation);

    /// <summary>
    /// One row of the FIRST-FOLD back-fill (issue #1633): turns this session had already counted before the
    /// per-agent tally existed, attributed to its agent.
    ///
    /// The only counts in this batch that are not derived from a watermark response, and legitimately so -
    /// nothing is being raised here, a standing total is being attributed once. What makes it safe under two
    /// writers is that the writer emits these rows ONLY when its own <c>agents_seeded</c> insert claimed the
    /// row, which the statement reports; a writer that lost that race attributes nothing.
    /// </summary>
    public readonly record struct AgentBackfillRow(string Agent, bool IsVoice, long Turns, long Chars);

    /// <summary>The session buckets observed this fold.</summary>
    public readonly List<BucketObservation> Buckets = new();

    /// <summary>The agent-to-agent counts observed this fold.</summary>
    public readonly List<AgentDrivenObservation> AgentDriven = new();

    /// <summary>The token spend observed this fold.</summary>
    public readonly List<TokenObservation> Tokens = new();

    /// <summary>Sessions being marked back-filled for the first time, each with the rows to attribute IF this
    /// writer is the one that claims the mark. Keyed by session id, in fold order.</summary>
    public readonly List<(string SessionId, List<AgentBackfillRow> Rows)> Seeding = new();

    public readonly List<string> NewWingmanSessions = new();
    public readonly List<(string Display, IdentityKind Kind)> NewIdentities = new();
    public readonly List<(string Display, string SessionId, IdentityKind Kind)> NewIdentitySessions = new();
    public string? StampAgentsSince;

    public bool IsEmpty => Buckets.Count == 0 && AgentDriven.Count == 0 && Tokens.Count == 0
        && Seeding.Count == 0 && NewWingmanSessions.Count == 0
        && NewIdentities.Count == 0 && NewIdentitySessions.Count == 0
        && StampAgentsSince is null;
}

/// <summary>
/// What one committed batch CHANGED, as the database reported it - the only thing the aggregator's mirror is
/// ever advanced from.
///
/// Returned rather than assumed for the reason this whole write path was reshaped: a writer that advances its
/// mirror to the value it PROPOSED ends up believing something the store does not hold the moment another
/// writer is in play, and every later fold measures growth from that fiction. The values here are what the
/// rows actually contain after the commit.
/// </summary>
internal sealed class StatsCommitResult
{
    /// <summary>The surrogate ids now standing for each display spelling this batch had to resolve, by kind.
    /// An id here may have been minted by THIS commit or by a writer that got there first - the statement
    /// reports which one won and this is that answer, never an assumption.</summary>
    public Dictionary<IdentityKind, IReadOnlyDictionary<string, long>> Identities { get; } = new();

    /// <summary>What <c>session_highwater</c> now holds for each bucket this batch raised.</summary>
    public List<(string SessionId, string Modality, string Surface, long Turns, long Chars)> SessionHighWater { get; } = new();

    /// <summary>What <c>agent_driven_highwater</c> now holds for each session this batch raised.</summary>
    public List<(string SessionId, long Turns, long Chars)> AgentDrivenHighWater { get; } = new();

    /// <summary>What <c>token_highwater</c> now holds for each session this batch raised.</summary>
    public List<(string SessionId, long Input, long Output, long CacheRead, long CacheCreation)> TokenHighWater { get; } = new();
}
