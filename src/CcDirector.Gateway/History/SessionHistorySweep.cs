using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.History;

/// <summary>
/// The background pass of the work-history feature (issue #2194), on the per-tenant worker seam. Each
/// pass, per tenant:
///
///  1. THE SILENCE RULE: every open row not refreshed within <see cref="InterruptedThreshold"/> is
///     concluded "interrupted". The recorder refreshes a live session's row at least every
///     <see cref="SessionHistoryRecorder.FreshnessInterval"/> (5 minutes), so the threshold has three
///     missed heartbeats of slack - a network blip or Gateway restart never rules a live session
///     interrupted, because its Director re-pushes within seconds of reconnecting and the row reopens
///     even if it did.
///  2. Generate up to <see cref="MaxSessionSummariesPerPass"/> owed session summaries and up to
///     <see cref="MaxRollupsPerPass"/> stale roll-ups (see <see cref="SessionHistorySummarizer"/> for
///     the cost decisions). The caps bound each pass's spend; the sweep catches up over passes.
///  3. Retention: rows older than <see cref="Retention"/> are pruned.
///
/// Timer cadence and lifecycle are owned by GatewayHost (the ActivityRetentionSweep pattern).
/// </summary>
public sealed class SessionHistorySweep : TenantScopedSweep
{
    /// <summary>How long an open row may go unrefreshed before the Gateway concludes "interrupted".</summary>
    public static readonly TimeSpan InterruptedThreshold = TimeSpan.FromMinutes(15);

    /// <summary>How long history rows live (the API's 30-day range sits well inside this).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    /// <summary>How many days back roll-ups are maintained - the History page's widest range.</summary>
    public const int RollupWindowDays = 30;

    public const int MaxSessionSummariesPerPass = 3;
    public const int MaxRollupsPerPass = 2;

    private readonly ITenantContext _tenantContext;
    private readonly SessionHistoryStore _store;
    private readonly SessionTurnStore _turns;
    private readonly SessionHistorySummarizer? _summarizer;

    /// <param name="summarizer">Null when the Gateway has no model path (some self-host setups);
    /// endings and retention still run, summaries stay owed - the record never depends on the model.</param>
    public SessionHistorySweep(HostedTenantBoundary boundary, TenantRegistry tenants,
        ITenantContext tenantContext, SessionHistoryStore store, SessionTurnStore turns, SessionHistorySummarizer? summarizer)
        : base(boundary, tenants)
    {
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _turns = turns ?? throw new ArgumentNullException(nameof(turns));
        _summarizer = summarizer;
    }

    public async Task SweepAsync(CancellationToken ct = default)
    {
        await ForEachTenantAsync(async () =>
        {
            var now = DateTime.UtcNow;
            var tenant = _tenantContext.Current;

            _store.ConcludeInterrupted(now - InterruptedThreshold);

            if (_summarizer is not null)
            {
                await _summarizer.SummarizePendingAsync(tenant, MaxSessionSummariesPerPass, ct).ConfigureAwait(false);
                await _summarizer.RefreshRollupsAsync(tenant,
                    now.Date.AddDays(-(RollupWindowDays - 1)), now.Date, MaxRollupsPerPass, ct).ConfigureAwait(false);
            }

            _store.PurgeOlderThan(now - Retention);
            // The stored conversation lives exactly as long as the session-history row it belongs to.
            _turns.PurgeOlderThan(now - Retention);
        }, ct).ConfigureAwait(false);
    }
}
