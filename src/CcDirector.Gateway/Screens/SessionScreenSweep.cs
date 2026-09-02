using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Screens;

/// <summary>
/// Retention for the terminal-screen store (the Terminal Rules mission,
/// <c>docs/missions/terminal-rules-2026-09-02/brief.md</c>): every stored screen received more than
/// <see cref="Retention"/> ago is deleted, per tenant, on the tenant-scoped worker seam.
///
/// SEVEN DAYS, and it is its own sweep rather than a line added to the session-history one. Two reasons,
/// and both are the point rather than a preference. First the owner set this retention separately from
/// session history's ninety days - a screen is bulky and loses its value fast - so one sweep running two
/// cutoffs would be a sweep whose name told you the wrong number. Second, the conversation store is
/// another mission's file (#2638) and this mission does not edit it.
///
/// Cadence and lifecycle are owned by GatewayHost, the same as every other sweep here.
/// </summary>
public sealed class SessionScreenSweep : TenantScopedSweep
{
    /// <summary>How long a stored screen lives. Seven days, set by the owner: long enough that the screen
    /// that stopped a session on Friday is still there on Monday, short enough that the store does not
    /// become an archive of terminals.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly SessionScreenStore _store;
    private readonly Func<DateTime> _nowUtc;

    public SessionScreenSweep(HostedTenantBoundary boundary, TenantRegistry tenants,
        SessionScreenStore store, Func<DateTime>? nowUtc = null)
        : base(boundary, tenants)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    /// <summary>One pass: delete each tenant's expired screens. Returns how many rows were removed across
    /// every tenant, so a caller - the host's timer, or a proof run - can state a NUMBER rather than
    /// report that a method was called.</summary>
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var removed = 0;
        var cutoff = _nowUtc() - Retention;
        await ForEachTenantAsync(() =>
        {
            removed += _store.PurgeOlderThan(cutoff);
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
        FileLog.Write($"[SessionScreenSweep] pass complete: removed {removed} screen(s) received before {cutoff:O} (retention {Retention.TotalDays:0} days)");
        return removed;
    }
}
