namespace CcDirector.Gateway.Util;

/// <summary>The sender a fanout will actually be attributed to, and whether the caller's claim was overridden.</summary>
public readonly record struct FanoutSender(string SessionId, bool Overridden);

/// <summary>
/// WHO a fanout is from (Remove-the-network-port mission, phase 2b - inspection finding 6).
///
/// <c>FanoutRequest</c> carries a caller-supplied <c>FromSessionId</c>, and it decides two things that are
/// both AUTHORITY: which team scope the broadcast is judged against, and which bucket the per-sender rate
/// limit counts into. Neither was compared with the authenticated caller. A session key could therefore
/// name another same-tenant session to borrow its team scope, or vary the id to sidestep its own rate
/// bucket. The newer <c>/fleet/broadcast</c> contract deliberately has no sender field for exactly this
/// reason; this applies the same rule to the older route rather than leaving two contracts with two
/// different answers.
///
/// A PURE FUNCTION, for the same reason <see cref="SessionKeyGuard"/> is one: the rule lives in exactly one
/// place, and it can be tested without standing up a Gateway and a Director. The endpoint that used to make
/// this decision inline could only be checked by a host-bound integration test, which is how it shipped
/// with no test at all.
///
/// A DEVICE KEY IS LEFT ALONE. The desktop and the phone act for the ACCOUNT rather than as a session, so
/// there is no session identity to pin them to, and forcing one would break the surfaces that legitimately
/// send on a chosen session's behalf. Tenant resolution is unaffected either way - it comes from
/// authentication, never from the body.
/// </summary>
public static class FanoutSenderPin
{
    /// <summary>
    /// Decide the sender to use.
    ///
    /// <paramref name="authenticatedSessionId"/> is the session the presented key BELONGS to, or null when
    /// the caller is not a session (a device key). <paramref name="claimedFromSessionId"/> is whatever the
    /// request body said.
    /// </summary>
    public static FanoutSender Resolve(string? authenticatedSessionId, string? claimedFromSessionId)
    {
        var authenticated = (authenticatedSessionId ?? "").Trim();
        var claimed = (claimedFromSessionId ?? "").Trim();

        // Not a session key: the caller acts for the account, so its claim stands as it always did.
        if (authenticated.Length == 0)
            return new FanoutSender(claimed, Overridden: false);

        // A session key is pinned to its own id, ALWAYS - including when it claimed nothing. Leaving an
        // empty claim empty would keep the rate-limit hole open, because an absent sender is its own
        // bucket and a caller could simply omit the field to escape its own.
        var overridden = claimed.Length > 0
            && !string.Equals(claimed, authenticated, StringComparison.OrdinalIgnoreCase);
        return new FanoutSender(authenticated, overridden);
    }
}
