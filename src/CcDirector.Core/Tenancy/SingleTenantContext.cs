namespace CcDirector.Core.Tenancy;

/// <summary>
/// The single-tenant implementation of <see cref="ITenantContext"/>. Every unit of work resolves to
/// <see cref="TenantId.Local"/>, so this is the N=1 case and nothing about behavior changes. A resolver
/// can replace this behind the same seam later; this type stays exactly what the single-tenant core runs.
/// </summary>
public sealed class SingleTenantContext : ITenantContext
{
    /// <inheritdoc />
    public TenantId Current => TenantId.Local;
}
