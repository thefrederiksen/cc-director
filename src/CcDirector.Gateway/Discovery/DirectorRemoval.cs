using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Discovery;

/// <summary>
/// One Director leaving the registry, WITH the tenant that owned it. This is the payload of
/// <see cref="DirectorRegistry.OnDirectorRemoved"/>.
///
/// It exists because the event used to be typed <c>Action&lt;string&gt;</c>. The registry's key has been
/// composite - (tenant, director id) - since issue #1847, so every removal path already resolves whose
/// Director left; the bare-string signature then dropped that at the event boundary, and no subscriber
/// could recover it.
///
/// The lesson generalises past this one seam: a type that cannot represent what the caller knows destroys
/// that knowledge silently, and every consumer downstream inherits the loss with nowhere to put a
/// correction. Carrying the owner in the payload means an unscoped removal cannot be expressed at all, so
/// a subscriber cannot forget to scope by it.
/// </summary>
/// <param name="Tenant">The tenant whose partition the entry was removed from.</param>
/// <param name="DirectorId">The director id, as keyed in that tenant's partition. It is unique only WITHIN
/// a tenant - never treat it as a fleet-wide identity.</param>
public readonly record struct DirectorRemoval(TenantId Tenant, string DirectorId);
