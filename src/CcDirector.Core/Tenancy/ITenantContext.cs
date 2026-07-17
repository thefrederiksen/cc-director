namespace CcDirector.Core.Tenancy;

/// <summary>
/// The tenancy seam: the one place that answers "which tenant is this request or connection for?" It is
/// resolved ONCE at ingress from the authenticated principal and then carried - never re-derived per
/// frame or per call, and never read from a client-supplied value.
///
/// The core ships exactly one implementation, <see cref="SingleTenantContext"/>, which always returns
/// <see cref="TenantId.Local"/>; that is the single-tenant (N=1) case and behaves as it does today. A
/// resolver that maps an authenticated principal to a real tenant can plug in behind this same
/// interface, so no consumer of <see cref="ITenantContext"/> has to change.
/// </summary>
public interface ITenantContext
{
    /// <summary>The tenant the current unit of work belongs to. Always a valid, resolved tenant.</summary>
    TenantId Current { get; }
}
