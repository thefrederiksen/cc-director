namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// A capability token that authorizes a FLEET-WIDE, cross-tenant read of live in-memory state - the
/// "system administrator" access mode. This is the deliberate counterpart to the normal-user path: a
/// request handler must NEVER hold one. A handler can only ever reach its own tenant's partition through
/// the tenant-scoped (tenant, id) accessors; a fleet-wide accessor takes a <see cref="SystemScope"/>, so
/// a handler physically cannot call it - there is no token to pass.
///
/// Fleet-wide access exists for the internal system passes (the "is there any fleet at all" guard,
/// aggregation, reconcile) and, later, an explicit operator/admin surface. The token is GRANTED ONCE, in
/// the composition root (<c>GatewayHost</c>), and injected only where a system pass legitimately needs it.
///
/// This is enforced two ways: the token cannot be constructed except through <see cref="Grant"/>, and a
/// guard test (<c>SystemScopeGuardTests</c>) fails the build if <see cref="Grant"/> is called anywhere
/// other than the composition root. So "who may read across tenants" is a single, auditable decision -
/// not something any handler can reach for by accident.
/// </summary>
public sealed class SystemScope
{
    private SystemScope() { }

    /// <summary>
    /// Mint the process's system capability. ONLY the composition root may call this; the guard test
    /// enforces the single call site. Every other component receives the token by injection, never by
    /// minting its own.
    /// </summary>
    public static SystemScope Grant() => new();
}
