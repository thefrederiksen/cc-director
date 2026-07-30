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

    public string HourKey { get; }

    // Model is the only nullable member of a row: null means the owning Director had recorded no model for
    // that session when the turn folded, which is the honest state and never a lookup failure. Repo is the
    // repo name (the grouping key); Checkout is the local working directory the turn ran in, retained beside
    // it so the path is not lost when worktrees and clones collapse into one repo row.
    public readonly List<(string Hour, string SessionId, string Modality, string Surface, bool IsVoice, string Repo, string Checkout, string? Model, bool Wingman, long Turns, long Chars)> Rows = new();
    public readonly List<(string Agent, bool IsVoice, long Turns, long Chars)> AgentRows = new();
    public readonly List<(string Agent, long Turns, long Chars)> AgentDrivenRows = new();
    public readonly List<(string SessionId, string Modality, string Surface, long Turns, long Chars)> HighWater = new();
    public readonly List<(string SessionId, long Turns, long Chars)> AgentDrivenHighWater = new();
    public readonly List<string> NewWingmanSessions = new();
    public readonly List<string> NewSeeded = new();
    public readonly List<(string Display, IdentityKind Kind)> NewIdentities = new();
    public readonly List<(string Display, string SessionId, IdentityKind Kind)> NewIdentitySessions = new();
    public string? StampAgentsSince;

    // Token spend (issue #1637). Model is nullable for the same reason it is on Rows: the spend attributes to
    // the model the session was recorded running, which is null until its records name one.
    public readonly List<(string Hour, string? Model, long Input, long Output, long CacheRead, long CacheCreation)> TokenRows = new();
    public readonly List<(string SessionId, long Input, long Output, long CacheRead, long CacheCreation)> TokenHighWater = new();

    public bool IsEmpty => Rows.Count == 0 && AgentRows.Count == 0 && AgentDrivenRows.Count == 0
        && HighWater.Count == 0 && AgentDrivenHighWater.Count == 0 && NewWingmanSessions.Count == 0
        && NewSeeded.Count == 0 && NewIdentities.Count == 0 && NewIdentitySessions.Count == 0
        && TokenRows.Count == 0 && TokenHighWater.Count == 0
        && StampAgentsSince is null;
}
