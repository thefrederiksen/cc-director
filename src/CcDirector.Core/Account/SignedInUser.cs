namespace CcDirector.Core.Account;

/// <summary>
/// The signed-in DevThrottle user surfaced INTO a session so the agent knows who the human is
/// (issue #1357). This is the identity the fleet preamble binds "me / my account / email me" to.
///
/// It carries the always-present <see cref="Email"/> (from the account token's claims) and the
/// optional <see cref="Nickname"/> the user set on their account. <see cref="DisplayName"/> is the
/// name to show: the nickname when the user set one, otherwise the email - so a user who never chose
/// a nickname is still named by their email rather than by nothing.
/// </summary>
/// <param name="Email">The signed-in user's email address (always present for a signed-in user).</param>
/// <param name="Nickname">The account nickname, or null when the user has not set one.</param>
public sealed record SignedInUser(string Email, string? Nickname)
{
    /// <summary>
    /// The name to display for this user: the nickname when set, otherwise the email. Never empty
    /// for a signed-in user, because <see cref="Email"/> is always present.
    /// </summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Nickname) ? Email : Nickname!;
}
