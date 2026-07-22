using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Governance;

/// <summary>
/// The Gateway's account-level hosted-AI spend store (issue #1771, spine item 3) over the
/// <c>account_hosted_ai_spend</c> table. It mirrors the REAL dollars the account spent on DevThrottle's
/// hosted AI services (text-to-speech, transcription, wingman translation) from the cloud credit-debit
/// ledger, so the weekly report can show an honest "Hosted-AI services: $X this week" line.
///
/// It is ACCOUNT-LEVEL by contract: a mirrored debit carries no session id and no run id, because the cloud
/// ledger attributes a debit to neither. Never pin one to a session or run.
///
/// De-duplication without a stable id: the cloud returns a rolling "recent transactions" window, so re-reads
/// re-observe the same debit. <see cref="RecordObservedDebits"/> mirrors only debits it has not already
/// stored, matched on (kind, amount, transaction-time). A debit the cloud returns WITHOUT a transaction time
/// is skipped - it cannot be de-duplicated, and an honest disclosed undercount beats a silent double-count of
/// real money.
///
/// Threading matches the rest of the data layer: single writer, write lock, fresh pooled context per operation.
/// </summary>
public sealed class AccountHostedAiSpendStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <summary>The cloud ledger entry kind this store mirrors - only debits are hosted-AI spend.</summary>
    public const string DebitKind = "debit";

    /// <summary>The hard ceiling on one list read; the default page is 500.</summary>
    public const int MaxListLimit = 5000;

    public AccountHostedAiSpendStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Mirror a batch of observed credit-ledger debits, skipping any already stored (dedup on kind + amount +
    /// transaction-time) and any without a transaction time (cannot dedup) or that are not positive debits.
    /// Returns the number of NEW debits mirrored. Idempotent: re-observing the cloud's rolling window is safe.
    /// </summary>
    public int RecordObservedDebits(IReadOnlyList<ObservedAccountDebit> debits)
    {
        if (debits is null || debits.Count == 0)
            return 0;

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var now = DateTime.UtcNow;
            var tenant = ctx.ActiveTenant!;
            var added = 0;

            foreach (var debit in debits)
            {
                if (debit is null)
                    continue;
                // Only positive-magnitude debits with a transaction time are mirrorable - a null time cannot
                // be de-duplicated, and a top-up or a zero is not hosted-AI spend.
                if (!string.Equals(debit.Kind, DebitKind, StringComparison.Ordinal))
                    continue;
                if (debit.AmountMicros <= 0)
                    continue;
                if (debit.TransactionCreatedUtc is null)
                    continue;

                var txTime = DateTime.SpecifyKind(debit.TransactionCreatedUtc.Value, DateTimeKind.Utc);

                var already = ctx.AccountHostedAiSpend.Any(
                    e => e.Kind == DebitKind &&
                         e.AmountMicros == debit.AmountMicros &&
                         e.TransactionCreatedUtc == txTime);
                if (already)
                    continue;

                ctx.AccountHostedAiSpend.Add(new AccountHostedAiSpendEntity
                {
                    TenantId = tenant,
                    AmountMicros = debit.AmountMicros,
                    Kind = DebitKind,
                    TransactionCreatedUtc = txTime,
                    ObservedUtc = now,
                });
                added++;
            }

            if (added > 0)
                ctx.SaveChanges();

            FileLog.Write($"[AccountHostedAiSpendStore] RecordObservedDebits: observed {debits.Count}, mirrored {added} new");
            return added;
        }
    }

    /// <summary>
    /// The total mirrored hosted-AI service spend over a window (by transaction time,
    /// <paramref name="sinceUtc"/> inclusive, <paramref name="untilUtc"/> exclusive) - the account-level
    /// figure the weekly report shows. Summed in the database.
    /// </summary>
    public AccountHostedAiSpendSummaryDto SumOverWindow(DateTime sinceUtc, DateTime untilUtc)
    {
        var since = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc);
        var until = DateTime.SpecifyKind(untilUtc, DateTimeKind.Utc);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var rows = ctx.AccountHostedAiSpend.AsNoTracking()
                .Where(e => e.TransactionCreatedUtc >= since && e.TransactionCreatedUtc < until)
                .ToList();
            return new AccountHostedAiSpendSummaryDto
            {
                TotalMicros = rows.Sum(e => e.AmountMicros),
                DebitCount = rows.Count,
                SinceUtc = since,
                UntilUtc = until,
            };
        }
    }

    /// <summary>Mirrored debits, newest transaction first, over an optional window. Ordered and bounded.</summary>
    public IReadOnlyList<AccountHostedAiSpendDto> List(
        DateTime? sinceUtc = null, DateTime? untilUtc = null, int limit = 500)
    {
        var take = Math.Clamp(limit, 1, MaxListLimit);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            IQueryable<AccountHostedAiSpendEntity> query = ctx.AccountHostedAiSpend.AsNoTracking();
            if (sinceUtc.HasValue)
            {
                var since = DateTime.SpecifyKind(sinceUtc.Value, DateTimeKind.Utc);
                query = query.Where(e => e.TransactionCreatedUtc >= since);
            }
            if (untilUtc.HasValue)
            {
                var until = DateTime.SpecifyKind(untilUtc.Value, DateTimeKind.Utc);
                query = query.Where(e => e.TransactionCreatedUtc < until);
            }

            return query
                .OrderByDescending(e => e.TransactionCreatedUtc)
                .Take(take)
                .ToList()
                .Select(ToDto)
                .ToList();
        }
    }

    private static AccountHostedAiSpendDto ToDto(AccountHostedAiSpendEntity e) => new()
    {
        AmountMicros = e.AmountMicros,
        Kind = e.Kind,
        TransactionCreatedUtc = e.TransactionCreatedUtc,
        ObservedUtc = e.ObservedUtc,
    };
}
