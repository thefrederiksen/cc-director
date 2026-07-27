namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The body of <c>GET /account/credits</c> (issue #884): the signed-in account's credit balance the
/// Settings account section shows, refreshed after a hosted AI action. The Gateway reads the balance
/// from the cloud with its own stored account token; the Cockpit never holds the token or calls the
/// cloud (DT-05), so this contract carries NO token field.
///
/// Amounts are in micro-dollars (1_000_000 = $1), the cloud's native unit, so the client formats the
/// dollar value without any rounding drift.
///
/// Issue #984 split TWO facts this contract used to conflate into <see cref="SignedIn"/> alone: whether the
/// CALLER is signed in, and whether a BALANCE could be read. On the hosted Gateway those differ - the caller
/// is signed in and the Gateway holds no credential of theirs to read a balance with - and with only one
/// boolean the endpoint had to pick one, so it served a false "not signed in" to paying customers on a
/// billing surface. <see cref="BalanceAvailable"/> and <see cref="Message"/> carry the second fact, so the
/// client can render the truth without deciding what any of it means (CLAUDE.md rule 7).
/// </summary>
public sealed class AccountCreditsDto
{
    /// <summary>
    /// Whether the CALLER is signed in to DevThrottle. NOT "whether this Gateway can read their balance" -
    /// see <see cref="BalanceAvailable"/> for that. True on the hosted Gateway for any enrolled tenant, even
    /// when no balance can be read for them.
    /// </summary>
    public bool SignedIn { get; set; }

    /// <summary>
    /// Whether <see cref="BalanceMicros"/> carries a real balance read from the cloud. False means the
    /// balance is UNKNOWN and <see cref="Message"/> says why; it never means the balance is zero, and a
    /// client must not render it as one.
    /// </summary>
    public bool BalanceAvailable { get; set; }

    /// <summary>
    /// The finished, user-facing sentence to show when <see cref="BalanceAvailable"/> is false, computed on
    /// the Gateway and rendered verbatim. Null when a balance was read. The client never composes its own -
    /// composing one locally is exactly how a hosted customer came to be shown "not signed in" on a page
    /// about their money.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// The current balance in micro-dollars when <see cref="BalanceAvailable"/> is true. Null otherwise -
    /// unknown, never a fabricated zero.
    /// </summary>
    public long? BalanceMicros { get; set; }

    /// <summary>
    /// The magnitude (positive micro-dollars) of the most recent debit, when a balance was read and the
    /// ledger has one - used to show the last hosted action's cost inline. Null when there is no recent debit.
    /// </summary>
    public long? LastDebitMicros { get; set; }
}
