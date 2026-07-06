using CcDirector.Core.Account;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Account;

/// <summary>
/// The outcome of the Gateway's account-membership check (issue #1079). <see cref="IsMember"/> is
/// true only when a presented, already-authorization-valid cloud token belongs to the account this
/// Gateway is signed in to. <see cref="Reason"/> carries a short, NON-identifying explanation for
/// diagnostics - it never contains an email address, a raw subject id, or any token contents.
/// </summary>
/// <param name="IsMember">True only when the presented token's account matches this Gateway's own account.</param>
/// <param name="Reason">A short, personally-identifying-information-free reason for the accept/refuse decision.</param>
public sealed record AccountMembershipResult(bool IsMember, string Reason);

/// <summary>
/// Answers the one question that stands between a signature-valid cloud token and trusting it as an
/// inbound credential (epic #1069, issue #1079): does this token belong to MY account? Issue #1074's
/// <see cref="JwtAccessTokenValidator.ValidateForAuthorization"/> already confirms a presented token
/// is correctly signed, unexpired, and minted for the expected audience/issuer, and extracts its
/// stable subject (account id). This component takes that validation result and accepts it only when
/// its subject equals the subject of the account THIS Gateway is signed in to - so a token that is
/// perfectly valid but was minted for a DIFFERENT DevThrottle account is refused, and a signed-out
/// Gateway accepts no cloud session at all.
///
/// The Gateway's own account subject is read locally from the cached credential
/// (<see cref="DevThrottleAccountService.GetAccountSubject"/>), the same source the outbound calls
/// use - the check makes NO network call. It logs only the accept/refuse decision and a
/// non-identifying reason; no email, no subject id, and no token contents ever reach the log
/// (security: no personally identifiable information in logs).
///
/// This is only the account-equality decision. Wiring it into the request pipeline
/// (<c>AuthMiddleware</c>) is a separate step (issue #1c) and is not done here.
/// </summary>
public sealed class GatewayAccountMembership
{
    private readonly DevThrottleAccountService _account;

    /// <summary>
    /// Creates the membership check over the Gateway-hosted credential service that holds this
    /// Gateway's own signed-in account.
    /// </summary>
    /// <param name="account">The Gateway's DevThrottle credential service (source of this Gateway's own account subject).</param>
    public GatewayAccountMembership(DevThrottleAccountService account)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
    }

    /// <summary>
    /// Decides whether the presented, already-authorization-valid cloud token belongs to this
    /// Gateway's account. Returns accept only when the token's validated subject equals this Gateway's
    /// own account subject. Refuses when the presented token is not authorization-valid, when this
    /// Gateway has no signed-in account, or when the subjects differ. Makes no network call.
    /// </summary>
    /// <param name="presentedToken">The presented cloud token's #1074 authorization-validation result, carrying its subject.</param>
    public AccountMembershipResult Check(AuthorizationTokenValidation presentedToken)
    {
        if (presentedToken is null)
            throw new ArgumentNullException(nameof(presentedToken));

        // Defence in depth: the caller is expected to pass an already-valid result, but a not-valid
        // result (or one with no subject) can never be a member - there is no account to match on.
        if (!presentedToken.IsValid || string.IsNullOrEmpty(presentedToken.Subject))
        {
            FileLog.Write("[GatewayAccountMembership] Check: refuse - the presented token is not authorization-valid (no subject to match)");
            return new AccountMembershipResult(false, "presented token is not authorization-valid");
        }

        var ownSubject = _account.GetAccountSubject();
        if (string.IsNullOrEmpty(ownSubject))
        {
            FileLog.Write("[GatewayAccountMembership] Check: refuse - this Gateway has no signed-in account");
            return new AccountMembershipResult(false, "gateway is not signed in to any account");
        }

        var isMember = string.Equals(ownSubject, presentedToken.Subject, StringComparison.Ordinal);
        if (isMember)
        {
            FileLog.Write("[GatewayAccountMembership] Check: accept - the presented token's account matches this Gateway's account");
            return new AccountMembershipResult(true, "presented token account matches this gateway's account");
        }

        FileLog.Write("[GatewayAccountMembership] Check: refuse - the presented token's account does not match this Gateway's account");
        return new AccountMembershipResult(false, "presented token account does not match this gateway's account");
    }
}
