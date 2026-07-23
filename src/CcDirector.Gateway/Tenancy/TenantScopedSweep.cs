using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// The worker seam of the structural tenancy gate (G8 increment 2). A background job has no HTTP request and
/// no SignalR connection, so nothing enters a tenant scope for it. Any background job that touches
/// tenant-scoped stores MUST run its per-tenant work through this base, which owns the fan-out over the tenant
/// census (<see cref="TenantRegistry"/>) and enters the ambient scope once PER TENANT. Inside an entered scope
/// the body calls the SAME stores as a request handler (<c>GatewayDatabase.CreateContext()</c>), which resolves
/// the current tenant exactly as a request does.
///
/// Two properties make this the ONLY safe way to run a hosted background sweep:
/// <list type="bullet">
/// <item>A body that forgot to enter a scope calls <c>CreateContext()</c> with no ambient tenant and FAILS
/// CLOSED at runtime (the <c>AsyncLocalTenantContext.Current</c> throw) - it can never silently read another
/// tenant's rows or default to Local.</item>
/// <item>The DT-TEN-3 architecture test (increment 2) makes a worker that touches a store WITHOUT going
/// through this base FAIL THE BUILD, so the skip is caught statically even when the offending path is disabled
/// on hosted and its runtime throw would never fire in test.</item>
/// </list>
///
/// Self-host is single-tenant and must not be burdened: when <see cref="HostedTenantBoundary.IsHosted"/> is
/// false the census is not enumerated at all - the body runs exactly ONCE under <see cref="TenantId.Local"/>
/// (<c>EnterScope</c> is a no-op and <c>Current</c> is already Local), identical to today's single fire.
/// </summary>
public abstract class TenantScopedSweep
{
    private readonly HostedTenantBoundary _boundary;
    private readonly TenantRegistry _tenants;

    protected TenantScopedSweep(HostedTenantBoundary boundary, TenantRegistry tenants)
    {
        _boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
    }

    /// <summary>
    /// Run <paramref name="body"/> once per tenant, each invocation inside that tenant's ambient scope.
    ///
    /// Self-host (<see cref="HostedTenantBoundary.IsHosted"/> false): runs the body EXACTLY ONCE under
    /// <see cref="TenantId.Local"/>; the census is never enumerated.
    ///
    /// Hosted: enumerates the tenant census and, for EACH tenant, enters that tenant's scope and awaits the
    /// body. The fan-out is per-tenant ISOLATED - one tenant's body throwing is caught and logged and does NOT
    /// abort the remaining tenants (mirroring the existing cron sweep's boundary try/catch). No tenant id,
    /// subject, or email is ever written to the log.
    ///
    /// Entitlement note (MTR-15 / the cancellation cutoff): a cancelled tenant's workers must not keep running.
    /// The cutoff adds the per-tenant entitlement-lease consultation as a filter in front of this fan-out (the
    /// lease is the shared signal); tenancy and entitlement stay separate mechanisms. Until the cutoff lands,
    /// this base is a pure tenant fan-out.
    /// </summary>
    /// <param name="body">The per-tenant work. It reads/writes through the ambient-scoped stores exactly as a
    /// request handler does; it must NOT capture or resolve a tenant of its own.</param>
    /// <param name="ct">Cancellation for a long census; checked between tenants so a shutdown is prompt.</param>
    protected async Task ForEachTenantAsync(Func<Task> body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (!_boundary.IsHosted)
        {
            // Self-host: one tenant, always resolved. EnterScope is a no-op and Current == Local, so this is
            // byte-for-byte the single fire the worker did before it was moved onto the seam.
            using (_boundary.EnterScope(TenantId.Local))
                await body().ConfigureAwait(false);
            return;
        }

        foreach (var tenant in _tenants.AllTenantIds())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using (_boundary.EnterScope(tenant))
                    await body().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Per-tenant isolation: one tenant's failure must never abort the sweep for the others. The
                // tenant id is deliberately NOT logged (it is not PII, but the log line stays identity-free by
                // policy); the exception type is enough to triage.
                FileLog.Write($"[TenantScopedSweep] per-tenant body failed, continuing with the next tenant ({ex.GetType().Name})");
            }
        }
    }
}
