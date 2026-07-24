using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Activity;

/// <summary>
/// The activity ledger's 30-day retention (the owner's ruling, 2026-07-24: raw activity events are kept 30
/// days, not forever - deciding retention now instead of "later" is the point). Runs through the per-tenant
/// worker seam (<see cref="TenantScopedSweep"/>): on hosted it enters each tenant's scope and purges that
/// tenant's expired rows in isolation; on self-host it fires once under Local. Driven by a timer in
/// <c>GatewayHost</c>.
///
/// Long-term learning ("the Gateway brain") is served by deriving aggregates BEFORE rows age out - a future
/// feature, deliberately not built here; this sweep only enforces the raw-event window.
/// </summary>
public sealed class ActivityRetentionSweep : TenantScopedSweep
{
    /// <summary>How long a raw activity event lives (the owner's 30-day ruling).</summary>
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);

    private readonly ActivityEventStore _store;

    public ActivityRetentionSweep(HostedTenantBoundary boundary, TenantRegistry tenants, ActivityEventStore store)
        : base(boundary, tenants)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Purge every tenant's events older than the retention window. One pass over the census.</summary>
    public async Task SweepAsync(CancellationToken ct = default)
    {
        var cutoffUtc = DateTime.UtcNow - RetentionPeriod;
        var total = 0;
        await ForEachTenantAsync(() =>
        {
            total += _store.PurgeOlderThan(cutoffUtc);
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        if (total > 0)
            FileLog.Write($"[ActivityRetentionSweep] purged {total} events older than {cutoffUtc:O}");
    }
}
