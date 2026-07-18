namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One mirrored hosted-AI service debit - real account-level dollars the account spent on DevThrottle's
/// hosted AI services (text-to-speech, transcription, wingman translation), from the cloud credit-debit
/// ledger (issue #1771, spine item 3). Account-level only: it carries NO session id and NO run id, because
/// the cloud ledger attributes a debit to neither and a fabricated attribution would be a lie.
/// </summary>
public sealed class AccountHostedAiSpendDto
{
    /// <summary>The debit magnitude in micro-dollars (1_000_000 = $1), positive.</summary>
    public long AmountMicros { get; set; }

    /// <summary>The cloud ledger entry kind ("debit").</summary>
    public string Kind { get; set; } = "";

    /// <summary>When the cloud recorded the transaction.</summary>
    public DateTime TransactionCreatedUtc { get; set; }

    /// <summary>When the Gateway observed and mirrored it.</summary>
    public DateTime ObservedUtc { get; set; }
}

/// <summary>
/// The account-level hosted-AI service spend over a window - the honest, disclosed "Hosted-AI services: $X"
/// figure the weekly report shows (issue #1771). It is NEVER blended into per-session or per-run spend: this
/// is a separate account-level axis, and the report labels it as such.
/// </summary>
public sealed class AccountHostedAiSpendSummaryDto
{
    /// <summary>Total mirrored hosted-AI service spend in the window, in micro-dollars.</summary>
    public long TotalMicros { get; set; }

    /// <summary>How many debit entries the total is composed of.</summary>
    public int DebitCount { get; set; }

    /// <summary>The window start (inclusive) the total was computed over.</summary>
    public DateTime SinceUtc { get; set; }

    /// <summary>The window end (exclusive) the total was computed over.</summary>
    public DateTime UntilUtc { get; set; }
}

/// <summary>
/// One observed credit-ledger debit handed to the Gateway from a credit-ledger snapshot, for mirroring into
/// the account-level spend record. The Gateway de-duplicates against what it has already mirrored (by kind +
/// amount + transaction time), so re-reading the cloud's rolling "recent transactions" window is safe.
/// A debit the cloud returns without a transaction time is skipped (it cannot be de-duplicated), rather than
/// risk double-counting real money.
/// </summary>
public sealed class ObservedAccountDebit
{
    /// <summary>The debit magnitude in micro-dollars, positive.</summary>
    public long AmountMicros { get; set; }

    /// <summary>The cloud ledger entry kind ("debit").</summary>
    public string Kind { get; set; } = "";

    /// <summary>When the cloud recorded the transaction; null means the entry is skipped (cannot de-dup).</summary>
    public DateTime? TransactionCreatedUtc { get; set; }
}
