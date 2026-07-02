namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The body of <c>GET /account/credits</c> (issue #884): the signed-in account's credit balance the
/// Settings account section shows, refreshed after a hosted AI action. The Gateway reads the balance
/// from the cloud with its own stored account token; the Cockpit never holds the token or calls the
/// cloud (DT-05), so this contract carries NO token field.
///
/// Amounts are in micro-dollars (1_000_000 = $1), the cloud's native unit, so the client formats the
/// dollar value without any rounding drift.
/// </summary>
public sealed class AccountCreditsDto
{
    /// <summary>Whether the Gateway holds a valid DevThrottle credential (computed locally).</summary>
    public bool SignedIn { get; set; }

    /// <summary>The current balance in micro-dollars, when signed in. Null when not signed in.</summary>
    public long? BalanceMicros { get; set; }

    /// <summary>
    /// The magnitude (positive micro-dollars) of the most recent debit, when signed in and the ledger
    /// has one - used to show the last hosted action's cost inline. Null when there is no recent debit.
    /// </summary>
    public long? LastDebitMicros { get; set; }
}
