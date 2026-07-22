using System;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Pairing;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// The auth-boundary tenant binder (Hosted Multi-Tenancy increment 1). It is the ONE place a boundary - the
/// Director tunnel's <c>Hello</c> and the device-key HTTP middleware - turns an AUTHENTICATED per-device key
/// into the tenant the stores resolve. The tenant is derived ONLY from the verified device key's binding
/// (<see cref="DeviceRegistry.TenantForKey"/>), never from anything the client claims in a payload.
///
/// On the hosted Gateway it enters the <see cref="AsyncLocalTenantContext"/>; on self-host it is inert (the
/// <see cref="SingleTenantContext"/> already answers <see cref="TenantId.Local"/>, so there is nothing to
/// enter and every authenticated caller is the single Local tenant). DENY-BY-DEFAULT: on hosted, a key with
/// no bound tenant resolves to null and the caller MUST deny - it never falls back to Local or to the
/// reserved SYSTEM tenant.
/// </summary>
public sealed class HostedTenantBoundary
{
    // Non-null ONLY on the hosted Gateway (the ITenantContext is the AsyncLocalTenantContext there). Null on
    // self-host, which is how IsHosted and the inert behavior are decided - no separate flag to drift.
    private readonly AsyncLocalTenantContext? _ambient;
    private readonly DeviceRegistry _devices;

    public HostedTenantBoundary(ITenantContext tenantContext, DeviceRegistry devices)
    {
        _ambient = tenantContext as AsyncLocalTenantContext;
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
    }

    /// <summary>True on the hosted Gateway (per-account, fail-closed); false on self-host (always Local).</summary>
    public bool IsHosted => _ambient is not null;

    /// <summary>
    /// Resolve the tenant for an AUTHENTICATED device key. On self-host every authenticated caller is the one
    /// Local tenant. On hosted the tenant is the key's binding, or NULL when the key is null/blank or has no
    /// bound tenant - a null is a DENY (never Local, never SYSTEM). The key must already have been proven
    /// valid by the auth layer; this only maps a proven key to its tenant.
    /// </summary>
    public TenantId? ResolveForDeviceKey(string? deviceKey)
    {
        if (_ambient is null)
            return TenantId.Local;

        var resolution = _devices.ResolveCredential(deviceKey);
        return resolution.Kind == DeviceCredentialResolutionKind.Active
            ? ResolveIdentity(resolution.Identity)
            : null;
    }

    /// <summary>
    /// Resolve the tenant of a REQUEST from the AUTHENTICATED device key the auth middleware stashed on it
    /// (Hosted Multi-Tenancy, session-serving). This is the read-side twin of the tunnel's Hello: the cockpit
    /// and mobile reach the read endpoints with the SAME per-device key (mirrored into the cc-gateway-token
    /// cookie) that the write path uses, so the read tenant resolves from that key - no account-token
    /// plumbing. On self-host every request is Local; on hosted it is the key's bound tenant, or null (a DENY:
    /// the endpoint must return 403, never fall back to Local or SYSTEM).
    /// </summary>
    public TenantId? ResolveRequestTenant(Microsoft.AspNetCore.Http.HttpContext ctx)
    {
        if (_ambient is null)
            return TenantId.Local;

        var identity = ctx?.Items.TryGetValue(Util.AuthMiddleware.AuthenticatedDeviceItemKey, out var value) == true
            ? value as DeviceCredentialIdentity
            : null;
        return ResolveIdentity(identity);
    }

    private static TenantId? ResolveIdentity(DeviceCredentialIdentity? identity)
    {
        if (identity is null || string.IsNullOrWhiteSpace(identity.TenantId))
            return null;

        var tenant = new TenantId(identity.TenantId);
        return !tenant.IsValid || tenant.IsLocal || tenant.IsSystem ? null : tenant;
    }

    /// <summary>
    /// Enter the scope for an already-resolved tenant (e.g. the tunnel's Hello-bound tenant on each push, or a
    /// resolved HTTP request). On self-host this is a no-op (Local is the ambient answer); on hosted it enters
    /// the ambient scope. The caller must have resolved a valid tenant first (the deny happened at resolution).
    /// </summary>
    public IDisposable EnterScope(TenantId tenant)
        => _ambient is null ? NoOpDisposable.Instance : _ambient.Enter(tenant);

    private sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        public void Dispose() { }
    }
}
