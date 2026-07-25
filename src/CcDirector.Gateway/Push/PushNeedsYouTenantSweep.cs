using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Push;

/// <summary>
/// Drives the app-icon "needs you" dot ONCE PER TENANT through the tenancy worker seam
/// (<see cref="TenantScopedSweep"/>), which is what makes phone notifications work on the hosted Gateway.
///
/// The notifier reads two tenant-scoped things - the <c>push_subscriptions</c> store (whose phones) and the
/// tenant's own folded fleet (how many sessions need you) - so a bare background timer, which has no ambient
/// tenant, could not drive it on hosted without failing closed on every tick. That is precisely why the whole
/// notifier used to be wrapped in <c>if (!GatewayHostedMode.IsHosted)</c>: hosted push was not broken, it was
/// switched off. A phone talking to the hosted Gateway therefore got no dot when a session needed it, and - the
/// half that is easy to miss - never got the single falling-edge zero that CLEARS a dot, so a dot raised while
/// the phone was pointed at a desktop Gateway could linger with nothing waiting behind it.
///
/// Running it through the seam enters each tenant's scope in turn, so one tenant's pass counts only that
/// tenant's fleet and pushes only to that tenant's devices. The per-tenant fan-out is isolated by the base: a
/// failure for one tenant is logged and the remaining tenants still get their pass. On self-host the seam fires
/// the body exactly once under <see cref="Core.Tenancy.TenantId.Local"/> - the same single pass as before.
/// </summary>
internal sealed class PushNeedsYouTenantSweep : TenantScopedSweep
{
    private readonly WebPushNeedsYouNotifier _notifier;

    public PushNeedsYouTenantSweep(
        HostedTenantBoundary boundary,
        TenantRegistry tenants,
        WebPushNeedsYouNotifier notifier)
        : base(boundary, tenants)
    {
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
    }

    /// <summary>Run one dot decision for every tenant (hosted) or for the single Local tenant (self-host).</summary>
    public Task SweepAsync(CancellationToken ct = default)
        => ForEachTenantAsync(() => _notifier.RunOnceAsync(ct), ct);
}
