using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Discovery;

/// <summary>
/// One Director leaving the registry, WITH the tenant that owned it. This is the payload of
/// <see cref="DirectorRegistry.OnDirectorRemoved"/>.
///
/// It exists because the event used to be typed <c>Action&lt;string&gt;</c>. The registry's key has been
/// composite - (tenant, director id) - since issue #1847, so every removal path already knows whose
/// Director left; the bare-string signature then THREW THAT AWAY at the event boundary, and no subscriber
/// could recover it. The roster cache's forget consequently swept every tenant's entry whose id matched,
/// which - because the tunnel Hello lets the client choose its own director id - let one account destroy
/// another account's cached roster on demand.
///
/// The lesson generalises past this one bug: a type that cannot represent what the caller knows destroys
/// that knowledge silently, and every consumer downstream inherits the loss. Carrying the owner in the
/// payload means an ownerless removal cannot be expressed, so a subscriber cannot forget to scope by it.
/// </summary>
/// <param name="Tenant">The tenant whose partition the entry was removed from.</param>
/// <param name="DirectorId">The director id, as keyed in that tenant's partition. Client-chosen, so it is
/// unique only WITHIN a tenant - never treat it as a fleet-wide identity.</param>
public readonly record struct DirectorRemoval(TenantId Tenant, string DirectorId);
