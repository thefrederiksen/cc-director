namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// The single, owner-visible, review-gated register of every DELIBERATE cross-tenant capability on the
/// hosted Gateway - the small set of operations that legitimately reach across tenant partitions (they hold
/// a <see cref="SystemScope"/> token, or enter the reserved <see cref="Core.Tenancy.TenantId.System"/>
/// scope) instead of a single account's tenant.
///
/// The point of the list is that a cross-tenant read can NEVER be done anonymously: it must NAME itself
/// here. Adding an entry is a one-line, review-visible act carrying the same bar as a schema change - it is
/// how the owner sees, in one place, exactly who may read across tenants. The build-time architecture gate
/// (<c>TenantGateArchitectureTests</c>) enforces the other half: the reserved System scope is entered ONLY
/// at the sanctioned composition-root site, so a new cross-tenant reach cannot ship without both appearing
/// here AND being sanctioned on that gate.
///
/// G8 increment 1 (this) seeds the list with the already-sealed fleet-wide Director listing
/// (<see cref="SystemScope"/>, devthrottle #2023) and enforces the named-only invariant statically. It does
/// NOT change runtime behavior: nothing consults this list at runtime yet. Increment 2's worker seam
/// (<c>TenantScopedSweep.RunAsSystemCapability</c>) will call <see cref="IsAllowed"/> before entering the
/// System scope for a deliberate cross-tenant pass.
/// </summary>
public static class SystemCapabilityAllowlist
{
    /// <summary>
    /// The fleet-wide, cross-tenant Director/admin listing, sealed behind <see cref="SystemScope"/>
    /// (devthrottle #2023): <c>DirectorRegistry.ListDirectors(SystemScope)</c>. A request handler holds no
    /// token and physically cannot call it. This is the first named cross-tenant capability.
    /// </summary>
    public const string FleetDirectorList = "fleet-director-list";

    /// <summary>
    /// The startup / built-in seeding pass that the composition root (<c>GatewayHost</c>) runs inside the
    /// reserved <see cref="Core.Tenancy.TenantId.System"/> scope. It is a non-account WRITE (seeding built-in
    /// workflows, importing/re-arming legacy state), not a cross-tenant read, but it is the ONE sanctioned
    /// site that enters the System scope directly, so it is named here for completeness and audited by the
    /// architecture gate.
    /// </summary>
    public const string StartupSystemSeeding = "startup-system-seeding";

    private static readonly HashSet<string> _names = new(StringComparer.Ordinal)
    {
        FleetDirectorList,
        StartupSystemSeeding,
    };

    /// <summary>Every named cross-tenant / System-scope capability, in one review-gated place.</summary>
    public static IReadOnlyCollection<string> Names => _names;

    /// <summary>
    /// True when <paramref name="capabilityName"/> is a registered cross-tenant capability. A caller that
    /// cannot name an allowlisted capability may not read across tenants (the worker seam will enforce this
    /// at the point it enters the System scope; increment 2).
    /// </summary>
    public static bool IsAllowed(string? capabilityName)
        => capabilityName is not null && _names.Contains(capabilityName);
}
