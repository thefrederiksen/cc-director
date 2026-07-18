using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Governance;

/// <summary>
/// The Gateway's per-session spend store (issue #1771, spine item 3) over the <c>session_spend</c> table -
/// one row per fleet session keyed on the canonical session GUID, so a run's effort is the sum over the
/// sessions that joined it (via <c>workflow_runs.Participants</c>).
///
/// It keeps three things separate and labelled and never blends them:
///  - RAW TOKENS - the additive token sums, the truth of what the model processed. Captured for any driver
///    that reports cumulative usage; recorded as UNKNOWN coverage (not zero) for a context-gauge-only driver.
///  - BILLING MODE - the label that says which bucket the tokens fall in. A coding-agent subscription session
///    is "subscription-included", which is WHY it carries no per-session dollar figure.
///  - METERED DOLLARS - left NULL in this phase on purpose: per-session dollars need the #1608 rate card, and
///    subscription traffic has no marginal dollar cost to record. The column exists so #1608 can fill it
///    without a schema change; a null is "no dollar figure", never a fabricated zero.
///
/// Cumulative totals OVERWRITE on record (they are running sums from the driver, not deltas), so a refresh is
/// an idempotent upsert. Threading matches the rest of the data layer: single writer, write lock, fresh
/// pooled context per operation.
/// </summary>
public sealed class SessionSpendStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <summary>The hard ceiling on one list read; the default page is 500.</summary>
    public const int MaxListLimit = 5000;

    public SessionSpendStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Upsert a session's cumulative spend. The totals overwrite (running sums, not deltas). Never sets the
    /// metered-dollar column - that is not caller-supplied. Returns the stored row.
    /// </summary>
    public SessionSpendDto Record(RecordSessionSpendRequest request)
    {
        if (request is null)
            throw new GovernanceValidationException("A spend body is required.");
        if (string.IsNullOrWhiteSpace(request.SessionId))
            throw new GovernanceValidationException("A spend record needs a sessionId.");
        if (string.IsNullOrWhiteSpace(request.AgentKind))
            throw new GovernanceValidationException("A spend record needs an agentKind.");

        var billingMode = (request.BillingMode ?? "").Trim().ToLowerInvariant();
        if (!SessionBillingMode.All.Contains(billingMode, StringComparer.Ordinal))
            throw new GovernanceValidationException(
                $"'{request.BillingMode}' is not a billing mode. Legal values: " +
                string.Join(", ", SessionBillingMode.All) + ".");

        if (request.InputTokens < 0 || request.OutputTokens < 0 ||
            request.CacheReadTokens < 0 || request.CacheCreationTokens < 0)
            throw new GovernanceValidationException("Token counts cannot be negative.");

        var sessionId = request.SessionId.Trim();
        var agentKind = request.AgentKind.Trim();

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var now = DateTime.UtcNow;
            var entity = ctx.SessionSpend.FirstOrDefault(s => s.SessionId == sessionId);
            if (entity is null)
            {
                entity = new SessionSpendEntity
                {
                    SessionId = sessionId,
                    TenantId = ctx.ActiveTenant!,
                    FirstObservedUtc = now,
                };
                ctx.SessionSpend.Add(entity);
            }

            entity.AgentKind = agentKind;
            entity.Model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim();
            entity.RepoPath = string.IsNullOrWhiteSpace(request.RepoPath) ? null : request.RepoPath.Trim();
            entity.TokensCaptured = request.TokensCaptured;
            entity.InputTokens = request.InputTokens;
            entity.OutputTokens = request.OutputTokens;
            entity.CacheReadTokens = request.CacheReadTokens;
            entity.CacheCreationTokens = request.CacheCreationTokens;
            entity.BillingMode = billingMode;
            // MeteredCostMicros is intentionally never set here - see the class remarks.
            entity.LastObservedUtc = now;

            ctx.SaveChanges();
            FileLog.Write($"[SessionSpendStore] Record: session={sessionId}, agent={agentKind}, " +
                          $"tokensCaptured={request.TokensCaptured}, billing={billingMode}");
            return ToDto(entity);
        }
    }

    /// <summary>One session's spend, or null.</summary>
    public SessionSpendDto? Get(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;
        var id = sessionId.Trim();
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionSpend.AsNoTracking().FirstOrDefault(s => s.SessionId == id);
            return entity is null ? null : ToDto(entity);
        }
    }

    /// <summary>Session spend rows, newest-observed first, optionally filtered by agent kind, billing mode,
    /// and last-observed time window (<paramref name="sinceUtc"/> inclusive, <paramref name="untilUtc"/>
    /// exclusive). Ordered and bounded in the database.</summary>
    public IReadOnlyList<SessionSpendDto> List(
        string? agentKind = null, string? billingMode = null,
        DateTime? sinceUtc = null, DateTime? untilUtc = null, int limit = 500)
    {
        var take = Math.Clamp(limit, 1, MaxListLimit);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            IQueryable<SessionSpendEntity> query = ctx.SessionSpend.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(agentKind))
            {
                var kind = agentKind.Trim();
                query = query.Where(s => s.AgentKind == kind);
            }
            if (!string.IsNullOrWhiteSpace(billingMode))
            {
                var mode = billingMode.Trim().ToLowerInvariant();
                query = query.Where(s => s.BillingMode == mode);
            }
            if (sinceUtc.HasValue)
            {
                var since = DateTime.SpecifyKind(sinceUtc.Value, DateTimeKind.Utc);
                query = query.Where(s => s.LastObservedUtc >= since);
            }
            if (untilUtc.HasValue)
            {
                var until = DateTime.SpecifyKind(untilUtc.Value, DateTimeKind.Utc);
                query = query.Where(s => s.LastObservedUtc < until);
            }

            return query
                .OrderByDescending(s => s.LastObservedUtc)
                .Take(take)
                .ToList()
                .Select(ToDto)
                .ToList();
        }
    }

    /// <summary>
    /// The coverage disclosure over a last-observed window: how many sessions have captured token spend, a
    /// metered dollar figure, subscription-included billing, and how many have NO token capture at all (the
    /// context-gauge-only gap). The weekly report shows this so a low spend number is never read as a good one.
    /// </summary>
    public SpendCoverageDto Coverage(DateTime? sinceUtc = null, DateTime? untilUtc = null)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            IQueryable<SessionSpendEntity> query = ctx.SessionSpend.AsNoTracking();
            if (sinceUtc.HasValue)
            {
                var since = DateTime.SpecifyKind(sinceUtc.Value, DateTimeKind.Utc);
                query = query.Where(s => s.LastObservedUtc >= since);
            }
            if (untilUtc.HasValue)
            {
                var until = DateTime.SpecifyKind(untilUtc.Value, DateTimeKind.Utc);
                query = query.Where(s => s.LastObservedUtc < until);
            }

            var rows = query.ToList();
            return new SpendCoverageDto
            {
                Sessions = rows.Count,
                SessionsWithTokens = rows.Count(s => s.TokensCaptured),
                SessionsWithMeteredDollars = rows.Count(s => s.MeteredCostMicros.HasValue),
                SessionsSubscriptionIncluded =
                    rows.Count(s => s.BillingMode == SessionBillingMode.SubscriptionIncluded),
                SessionsWithoutTokenCapture = rows.Count(s => !s.TokensCaptured),
            };
        }
    }

    private static SessionSpendDto ToDto(SessionSpendEntity e) => new()
    {
        SessionId = e.SessionId,
        AgentKind = e.AgentKind,
        Model = e.Model,
        RepoPath = e.RepoPath,
        TokensCaptured = e.TokensCaptured,
        InputTokens = e.InputTokens,
        OutputTokens = e.OutputTokens,
        CacheReadTokens = e.CacheReadTokens,
        CacheCreationTokens = e.CacheCreationTokens,
        BillingMode = e.BillingMode,
        MeteredCostMicros = e.MeteredCostMicros,
        FirstObservedUtc = e.FirstObservedUtc,
        LastObservedUtc = e.LastObservedUtc,
    };
}
