namespace CcDirector.Core.Account;

/// <summary>
/// The outcome of a refresh-token exchange (issue #876; the misconfigured signal is issue #911). Four
/// cases, because the caller must react differently to each:
/// <list type="bullet">
/// <item><see cref="Renewed"/> carries the fresh pair on success.</item>
/// <item><see cref="RefreshTokenRejected"/> true means the backend DEFINITIVELY refused the refresh
/// token (invalid, rotated away, or the session was revoked server-side) so the cached credential is
/// dead and must be cleared.</item>
/// <item><see cref="RefreshMisconfigured"/> true means the exchange is PERSISTENTLY broken by a
/// client-side misconfiguration - the required <c>apikey</c> was missing/empty, or the endpoint
/// answered 401 "No API key found in request" - which retrying alone will never fix. The refresh
/// token itself may still be perfectly good, so the caller keeps the cached credential but surfaces
/// the persistent failure instead of masking it as a transient outage forever (the exact #911 bug).</item>
/// <item>All unset means the exchange was merely UNAVAILABLE (offline, backend unreachable, rate
/// limited, or a 5xx server-side error) so the caller keeps the cached credential and retries later.</item>
/// </list>
/// Collapsing these into one signal was the pre-#876/#911 shape, and it made a revoked session, a
/// key misconfiguration, and a network blip indistinguishable.
/// </summary>
public sealed record TokenRefreshResult
{
    /// <summary>The renewed token pair, when the exchange succeeded. Null otherwise.</summary>
    public DevThrottleTokens? Renewed { get; }

    /// <summary>
    /// True when the backend definitively refused the refresh token (invalid, rotated away, or the
    /// session was revoked). The cached credential can never work again - the caller clears it.
    /// </summary>
    public bool RefreshTokenRejected { get; }

    /// <summary>
    /// True when the exchange is persistently broken by a client-side misconfiguration (a missing
    /// <c>apikey</c>, or a 401 "No API key found in request" from the endpoint). Retrying will not fix
    /// it; the caller keeps the cached credential but surfaces the persistent failure (issue #911).
    /// </summary>
    public bool RefreshMisconfigured { get; }

    private TokenRefreshResult(DevThrottleTokens? renewed, bool rejected, bool misconfigured)
    {
        Renewed = renewed;
        RefreshTokenRejected = rejected;
        RefreshMisconfigured = misconfigured;
    }

    /// <summary>The exchange succeeded and returned a fresh pair.</summary>
    public static TokenRefreshResult Success(DevThrottleTokens renewed) =>
        new(renewed ?? throw new ArgumentNullException(nameof(renewed)), rejected: false, misconfigured: false);

    /// <summary>The exchange could not run or complete (offline, unconfigured endpoint, rate limit, 5xx). Retry later.</summary>
    public static readonly TokenRefreshResult Unavailable = new(renewed: null, rejected: false, misconfigured: false);

    /// <summary>The backend definitively refused the refresh token. The cached credential is dead.</summary>
    public static readonly TokenRefreshResult Rejected = new(renewed: null, rejected: true, misconfigured: false);

    /// <summary>
    /// The exchange is persistently broken by a client-side <c>apikey</c> misconfiguration (issue
    /// #911). The cached credential is kept (the refresh token may be fine) but the persistent failure
    /// is surfaced rather than hidden as a transient outage.
    /// </summary>
    public static readonly TokenRefreshResult Misconfigured = new(renewed: null, rejected: false, misconfigured: true);
}

/// <summary>
/// Exchanges a refresh token for a fresh token pair against the DevThrottle backend. This is the
/// one network-touching seam in the credential service: the live exchange is supplied as an
/// implementation of this interface, and the offline logged-in check never calls it. Tests supply a
/// stub so the refresh paths can be proven without the live backend.
/// </summary>
public interface ITokenRefresher
{
    /// <summary>
    /// Attempts to renew the token pair using the current refresh token. Returns
    /// <see cref="TokenRefreshResult.Success"/> with the new pair when the exchange succeeds,
    /// <see cref="TokenRefreshResult.Rejected"/> when the backend definitively refuses the refresh
    /// token (the caller clears the dead credential), <see cref="TokenRefreshResult.Misconfigured"/>
    /// when the exchange is persistently broken by a missing/invalid <c>apikey</c> (the caller keeps
    /// the cached credential but surfaces the persistent failure, issue #911), or
    /// <see cref="TokenRefreshResult.Unavailable"/> when the exchange cannot run or complete for a
    /// transient reason (the caller keeps the cached credential and retries).
    /// </summary>
    Task<TokenRefreshResult> RefreshAsync(string refreshToken, CancellationToken ct = default);
}
