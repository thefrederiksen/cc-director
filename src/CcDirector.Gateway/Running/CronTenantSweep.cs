using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Running;

/// <summary>
/// Fires the cron engine ONCE PER TENANT through the tenancy worker seam (G8 increment 2,
/// <see cref="TenantScopedSweep"/>). The cron drain reads the tenant-scoped <c>cron_jobs</c> store, so a
/// background timer - which has no ambient tenant - could not fire it on hosted without failing closed every
/// tick; that is why hosted cron was previously DISABLED rather than tenant-aware. Running it through the
/// seam enters each tenant's ambient scope in turn, so hosted cron now runs safely and tenant-isolated: one
/// tenant's due jobs are evaluated against only that tenant's <c>cron_jobs</c> rows. On self-host the seam
/// fires the body exactly once under <see cref="Core.Tenancy.TenantId.Local"/>, identical to the single
/// pre-seam fire.
/// </summary>
internal sealed class CronTenantSweep : TenantScopedSweep
{
    private readonly CronEngine _cronEngine;

    public CronTenantSweep(HostedTenantBoundary boundary, TenantRegistry tenants, CronEngine cronEngine)
        : base(boundary, tenants)
    {
        _cronEngine = cronEngine ?? throw new ArgumentNullException(nameof(cronEngine));
    }

    /// <summary>Fire all due cron jobs for every tenant (hosted) or the single Local tenant (self-host). The
    /// per-tenant fan-out is isolated: a failure firing one tenant's jobs does not abort the others.</summary>
    public Task SweepAsync(CancellationToken ct = default)
        => ForEachTenantAsync(() => _cronEngine.EvaluateDueAsync(ct), ct);
}
