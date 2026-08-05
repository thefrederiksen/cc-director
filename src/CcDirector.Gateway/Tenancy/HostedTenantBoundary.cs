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

        // Tenant-boundary hardening (release 2026-07-31, finding CR-7): a HOSTED process whose boundary is
        // built over anything but the AsyncLocalTenantContext could only ever answer Local - every resolution
        // it performed would silently collapse the account partitions into one. That is a wiring defect, not
        // a state to operate in, so it fails LOUD at construction rather than open at resolution.
        if (GatewayHostedMode.IsHosted && _ambient is null)
            throw new InvalidOperationException(
                "This Gateway is running in hosted mode, but the tenant boundary was constructed without the " +
                "per-account ambient tenant context (AsyncLocalTenantContext). A boundary wired this way can " +
                "only resolve the Local partition, which on hosted collapses every account into one. Construct " +
                "it over the hosted tenant context.");
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
        {
            ThrowIfHostedWithoutAmbientContext();
            return TenantId.Local;
        }

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
        {
            ThrowIfHostedWithoutAmbientContext();
            return TenantId.Local;
        }

        var identity = ctx?.Items.TryGetValue(Util.AuthMiddleware.AuthenticatedDeviceItemKey, out var value) == true
            ? value as DeviceCredentialIdentity
            : null;
        if (identity is not null)
            return ResolveIdentity(identity);

        // Remove-the-network-port phase 1b: the caller may be a SESSION rather than a device. A session key
        // carries its tenant on the registry row - written from the tenant its Director's tunnel bound to at
        // Hello, which came from that Director's authenticated device key - so it is a verified binding of
        // exactly the same provenance, one hop further along. Read from the identity the auth gate resolved,
        // never from the request.
        var session = Util.AuthMiddleware.CallingSession(ctx);
        if (session is null)
            return null;

        var tenant = session.Tenant;
        return !tenant.IsValid || tenant.IsLocal || tenant.IsSystem ? null : tenant;
    }

    /// <summary>
    /// The resolution-time half of the CR-7 guard. The constructor already refuses a hosted process wiring a
    /// Local-only boundary; this catches the one remaining path - a boundary built while the process was NOT
    /// hosted, resolved after hosted mode turned on (the environment variable is read live). Resolving Local
    /// there would silently collapse every account into one partition, so it throws instead.
    /// </summary>
    private static void ThrowIfHostedWithoutAmbientContext()
    {
        if (GatewayHostedMode.IsHosted)
            throw new InvalidOperationException(
                "This Gateway is running in hosted mode, but the tenant boundary has no per-account ambient " +
                "tenant context, so it could only resolve the Local partition. Refusing to resolve: on hosted " +
                "this would collapse every account into one partition.");
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
