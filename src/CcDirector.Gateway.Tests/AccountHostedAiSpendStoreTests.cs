using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Governance;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Store tests for account-level hosted-AI spend (issue #1771, spine item 3). The claims that matter:
/// re-observing the cloud's rolling window is idempotent (dedup on kind + amount + transaction-time); a
/// debit with no transaction time is skipped (cannot dedup - undercount beats double-counting real money);
/// non-debit or non-positive entries are ignored; the window sum is honest; and the store is tenant-scoped.
/// </summary>
public sealed class AccountHostedAiSpendStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private AccountHostedAiSpendStore NewStore() => new(_h.Open());

    private static readonly DateTime T0 = new(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc);

    private static ObservedAccountDebit Debit(long micros, DateTime? at) => new()
    {
        AmountMicros = micros,
        Kind = AccountHostedAiSpendStore.DebitKind,
        TransactionCreatedUtc = at,
    };

    [Fact]
    public void RecordObservedDebits_mirrors_new_debits()
    {
        var store = NewStore();
        var added = store.RecordObservedDebits(new List<ObservedAccountDebit>
        {
            Debit(556, T0),
            Debit(1200, T0.AddMinutes(5)),
        });

        Assert.Equal(2, added);
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void Re_observing_the_rolling_window_is_idempotent()
    {
        var store = NewStore();
        store.RecordObservedDebits(new List<ObservedAccountDebit> { Debit(556, T0), Debit(1200, T0.AddMinutes(5)) });
        // The cloud returns the same two debits again next snapshot, plus a new one.
        var addedSecond = store.RecordObservedDebits(new List<ObservedAccountDebit>
        {
            Debit(556, T0),
            Debit(1200, T0.AddMinutes(5)),
            Debit(999, T0.AddMinutes(10)),
        });

        Assert.Equal(1, addedSecond); // only the genuinely new debit
        Assert.Equal(3, store.List().Count);
    }

    [Fact]
    public void A_debit_without_a_transaction_time_is_skipped()
    {
        var store = NewStore();
        // No created_at means it cannot be de-duplicated - skip it rather than risk double-counting money.
        var added = store.RecordObservedDebits(new List<ObservedAccountDebit> { Debit(556, at: null) });

        Assert.Equal(0, added);
        Assert.Empty(store.List());
    }

    [Fact]
    public void Non_debit_and_non_positive_entries_are_ignored()
    {
        var store = NewStore();
        var added = store.RecordObservedDebits(new List<ObservedAccountDebit>
        {
            new() { Kind = "credit", AmountMicros = 5_000_000, TransactionCreatedUtc = T0 }, // a top-up, not spend
            new() { Kind = AccountHostedAiSpendStore.DebitKind, AmountMicros = 0, TransactionCreatedUtc = T0 },
            new() { Kind = AccountHostedAiSpendStore.DebitKind, AmountMicros = -10, TransactionCreatedUtc = T0 },
        });

        Assert.Equal(0, added);
        Assert.Empty(store.List());
    }

    [Fact]
    public void SumOverWindow_totals_the_debits_in_range()
    {
        var store = NewStore();
        store.RecordObservedDebits(new List<ObservedAccountDebit>
        {
            Debit(556, T0),
            Debit(1200, T0.AddHours(1)),
            Debit(9999, T0.AddDays(2)), // outside the one-day window below
        });

        var summary = store.SumOverWindow(T0.AddMinutes(-1), T0.AddDays(1));
        Assert.Equal(556 + 1200, summary.TotalMicros);
        Assert.Equal(2, summary.DebitCount);
    }

    [Fact]
    public void The_store_is_tenant_scoped()
    {
        var alpha = new AccountHostedAiSpendStore(_h.Open(new FixedTenantContext(new TenantId("alpha"))));
        var beta = new AccountHostedAiSpendStore(_h.Open(new FixedTenantContext(new TenantId("beta"))));

        alpha.RecordObservedDebits(new List<ObservedAccountDebit> { Debit(500, T0) });
        beta.RecordObservedDebits(new List<ObservedAccountDebit> { Debit(700, T0) });

        Assert.Single(alpha.List());
        Assert.Single(beta.List());
        Assert.Equal(500, alpha.List()[0].AmountMicros);
        Assert.Equal(700, beta.SumOverWindow(T0.AddDays(-1), T0.AddDays(1)).TotalMicros);
    }
}
