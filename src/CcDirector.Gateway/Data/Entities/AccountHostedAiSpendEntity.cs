namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One observed hosted-AI service debit, in the <c>account_hosted_ai_spend</c> table - the ACCOUNT-level
/// metered-dollar record of the governance spine (issue #1771, spine item 3). This is real money the account
/// spent on DevThrottle's hosted AI services (text-to-speech, transcription, wingman translation), mirrored
/// from the cloud credit-debit ledger (<c>GET /api/v1/account/credits</c>).
///
/// It is deliberately ACCOUNT-LEVEL and carries NO SessionId and NO RunId, ever. The cloud ledger attributes
/// a debit to neither - a transaction is only {kind, amount, created_at} - so pinning one to a session or run
/// would be a fabricated attribution. The weekly report reads this as a disclosed account-level line
/// ("Hosted-AI services: $X this week"), never blended into per-session or per-run spend.
///
/// This is a SEPARATE axis from <see cref="SessionSpendEntity"/>: that table is the coding sessions' token
/// effort (subscription-included on a Claude Max plan, so no debits and no per-session dollar figure until
/// the #1608 rate card); this table is the hosted-AI service spend that actually draws real dollars.
///
/// The source ledger has no stable transaction id, so re-reads of the rolling "recent transactions" window
/// would re-observe the same debit. De-duplication is by (tenant, kind, amount, transaction-time); a debit
/// the cloud returns without a created_at cannot be de-duplicated and is therefore skipped rather than risk
/// double-counting real money (the honest choice - an undercount that is disclosed, never a silent overcount).
/// </summary>
public sealed class AccountHostedAiSpendEntity : GatewayMintedKeyEntity
{
    /// <summary>The debit magnitude in micro-dollars (1_000_000 = $1), stored POSITIVE (the cloud returns a
    /// debit as a negative amount; we mirror its magnitude). Integer smallest-units, never a bare decimal.</summary>
    public long AmountMicros { get; set; }

    /// <summary>The cloud ledger entry kind, for provenance ("debit"). Only debits are mirrored here - this
    /// is a SPEND record, not the full ledger; top-ups are not hosted-AI spend.</summary>
    public string Kind { get; set; } = "";

    /// <summary>When the cloud recorded the transaction (its <c>created_at</c>). The de-duplication and the
    /// weekly-window bucketing both key on this, so a debit the cloud returns without it is skipped rather
    /// than double-counted.</summary>
    public DateTime TransactionCreatedUtc { get; set; }

    /// <summary>When the Gateway observed and mirrored this debit (server-stamped). Distinct from
    /// <see cref="TransactionCreatedUtc"/>: the audit fact of when we learned of the spend.</summary>
    public DateTime ObservedUtc { get; set; }
}
