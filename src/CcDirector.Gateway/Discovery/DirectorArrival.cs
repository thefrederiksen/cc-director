using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Discovery;

/// <summary>
/// One Director entering the registry, together with the tenant that owns it. A Director identifier is unique only
/// within a tenant, so registry subscribers must receive both values to make tenant-scoped decisions.
/// </summary>
/// <param name="Tenant">The tenant whose partition received the Director.</param>
/// <param name="Director">The Director registered in that partition.</param>
public readonly record struct DirectorArrival(TenantId Tenant, DirectorDto Director);
