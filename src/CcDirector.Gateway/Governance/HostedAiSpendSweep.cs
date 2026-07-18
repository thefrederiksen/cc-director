using System.Globalization;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Governance;

/// <summary>
/// Fills the <c>account_hosted_ai_spend</c> table (issue #1771, spine item 3) by periodically mirroring the
/// account's real hosted-AI service debits from the cloud credit-debit ledger. A background timer on the
/// Gateway (the same shape as <see cref="Snooze.SnoozeExpirySweep"/>): every few minutes it reads the
/// signed-in account's recent ledger entries and hands the debits to <see cref="AccountHostedAiSpendStore"/>,
/// which de-duplicates the cloud's rolling window so over-polling is safe.
///
/// Honesty at the edges (no fabricated money):
///  - Signed out (no account credential) is an expected state, not an error: nothing is mirrored, and no
///    spend is invented.
///  - The store keeps the amount ACCOUNT-LEVEL - never pinned to a session or run - because the ledger
///    attributes a debit to neither.
///  - A debit the cloud returns without a parseable transaction time is left with a null time so the store
///    skips it: a disclosed undercount beats double-counting real dollars.
/// </summary>
public sealed class HostedAiSpendSweep : IDisposable
{
    /// <summary>How often the account ledger is re-read. The store dedups, so the exact cadence is not
    /// correctness - it only bounds how soon a new hosted-AI debit shows up in the report.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    /// <summary>A short settling delay before the first read, so startup finishes first.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    private readonly Func<string?> _accessToken;
    private readonly AccountCreditsClient _credits;
    private readonly AccountHostedAiSpendStore _store;

    private System.Threading.Timer? _timer;
    private int _busy; // 0 = idle, 1 = a tick is running (reentrancy guard)

    /// <param name="accessToken">Reads the Gateway-held account access token, or null/empty when signed out.
    /// The token is passed straight to the credits client and never logged.</param>
    /// <param name="credits">The cloud credits client (the injectable cloud egress seam).</param>
    /// <param name="store">The account-level hosted-AI spend store the debits are mirrored into.</param>
    public HostedAiSpendSweep(Func<string?> accessToken, AccountCreditsClient credits, AccountHostedAiSpendStore store)
    {
        _accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
        _credits = credits ?? throw new ArgumentNullException(nameof(credits));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Start the background sweep. Returns immediately; the first read runs after a short delay.</summary>
    public void Start()
    {
        _timer = new System.Threading.Timer(_ => Tick(), null, StartupDelay, SweepInterval);
        FileLog.Write($"[HostedAiSpendSweep] started: every {SweepInterval.TotalMinutes:0} min");
    }

    /// <summary>
    /// One read-and-mirror pass. Public so a test can drive it directly. A signed-out Gateway returns without
    /// mirroring (an expected state, never an error); a cloud failure throws (surfaced by the tick's log,
    /// never a fabricated figure).
    /// </summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var token = _accessToken();
        if (string.IsNullOrEmpty(token))
        {
            FileLog.Write("[HostedAiSpendSweep] no account credential -> skipping (signed out)");
            return;
        }

        var credits = await _credits.GetCreditsAsync(token, cancellationToken).ConfigureAwait(false);
        var debits = MapDebits(credits.Recent);
        var mirrored = _store.RecordObservedDebits(debits);
        FileLog.Write($"[HostedAiSpendSweep] observed {credits.Recent.Count} ledger entries, mirrored {mirrored} new debit(s)");
    }

    /// <summary>
    /// Map the cloud's recent ledger to observed debits: a debit is a negative-amount entry, stored as its
    /// positive magnitude. A top-up (positive) or a zero is not a hosted-AI cost and is dropped. The
    /// transaction time is parsed to UTC; an absent or unparseable time is left null so the store skips it
    /// (it cannot be de-duplicated) rather than risk double-counting real money.
    /// </summary>
    internal static IReadOnlyList<ObservedAccountDebit> MapDebits(IReadOnlyList<CloudCreditTransaction> recent)
    {
        var debits = new List<ObservedAccountDebit>();
        foreach (var tx in recent)
        {
            if (tx.AmountMicros >= 0)
                continue;
            debits.Add(new ObservedAccountDebit
            {
                Kind = AccountHostedAiSpendStore.DebitKind,
                AmountMicros = -tx.AmountMicros,
                TransactionCreatedUtc = ParseUtc(tx.CreatedAt),
            });
        }
        return debits;
    }

    /// <summary>Parse a cloud timestamp to UTC, or null when it is absent or unparseable.</summary>
    internal static DateTime? ParseUtc(string? createdAt)
    {
        if (string.IsNullOrWhiteSpace(createdAt))
            return null;
        if (DateTime.TryParse(createdAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return null;
    }

    private void Tick()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return; // a previous tick is still running - skip this one
        _ = TickAsync();
    }

    private async Task TickAsync()
    {
        try
        {
            await RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[HostedAiSpendSweep] tick FAILED: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
