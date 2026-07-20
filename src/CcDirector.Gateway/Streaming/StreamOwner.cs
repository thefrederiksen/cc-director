using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Issue #1923: the identity that OWNS a registered up-stream - the tenant whose browser request opened it,
/// paired with the Director the Gateway sent the open command to.
///
/// Why this pair, and not one or the other. The tenant alone is not enough on a self-host install (everything
/// is <see cref="TenantId.Local"/>, so it would authorize nothing), and the Director id alone is not enough on
/// hosted, because a Director id is chosen by the client in its Hello message and the Director registry is
/// keyed on (tenant, director id) - two accounts can each own a Director calling itself the same thing. The
/// PAIR is the identity that is both server-resolved on the receiving side (the tenant comes from the
/// authenticated device key at Hello, never from client input) and known on the registering side (the request
/// tenant that located the session, and that session's owning Director).
/// </summary>
/// <param name="Tenant">The isolation boundary the stream belongs to.</param>
/// <param name="DirectorId">The Director the stream was opened on, and the only one allowed to stream it up.</param>
public readonly record struct StreamOwner(TenantId Tenant, string DirectorId)
{
    /// <summary>
    /// True when <paramref name="caller"/> is the same identity as this owner. The Director id is compared
    /// case-insensitively, matching how <c>DirectorHub.Hello</c> compares a re-claim of a bound id; the tenant
    /// is compared exactly (tenant values are canonical, never case-folded).
    /// </summary>
    public bool Matches(StreamOwner caller) =>
        Tenant.IsValid
        && caller.Tenant.IsValid
        && string.Equals(Tenant.Value, caller.Tenant.Value, StringComparison.Ordinal)
        && !string.IsNullOrEmpty(DirectorId)
        && string.Equals(DirectorId, caller.DirectorId, StringComparison.OrdinalIgnoreCase);

    /// <summary>A log-safe rendering: the tenant is hashed (never raw), the Director id is shortened.</summary>
    public string ToLogString() =>
        $"tenant={Tenant.ToLogString()}, director={(string.IsNullOrEmpty(DirectorId) ? "(none)" : DirectorId)}";
}

/// <summary>
/// Issue #1923: thrown when a caller tries to stream frames into an up-stream it does not own. This is a
/// REFUSAL, not a silent drop: a drop would be indistinguishable from the legitimate "the stream id is
/// unknown because the browser already left" no-op, and it would hide a real misconfiguration from operators.
/// The hub turns this into a hub error the calling Director sees.
/// </summary>
public sealed class StreamOwnershipDeniedException : Exception
{
    public StreamOwnershipDeniedException(string message) : base(message) { }
}
