using System.Net;
using System.Net.Http;
using System.Text;
using CcDirector.Core.Account;
using CcDirector.Gateway.Governance;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tests for the periodic hosted-AI spend sweep (issue #1771, spine item 3). The claims that matter: only
/// debits are mirrored, as a positive magnitude; a top-up or a zero is dropped; an unparseable transaction
/// time is left null so the store skips it (a disclosed undercount over double-counting money); re-observing
/// the rolling window mirrors nothing new; and a signed-out Gateway records nothing rather than fabricating.
/// </summary>
public sealed class HostedAiSpendSweepTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private AccountHostedAiSpendStore NewStore() => new(_h.Open());

    [Fact]
    public void MapDebits_keeps_only_debits_as_positive_magnitude()
    {
        var recent = new List<CloudCreditTransaction>
        {
            new("debit", -1500, "2026-07-18T01:00:00Z"),
            new("credit", 5000, "2026-07-18T02:00:00Z"), // a top-up: dropped
            new("debit", -250, "2026-07-18T03:00:00Z"),
            new("debit", 0, "2026-07-18T04:00:00Z"),      // not negative: dropped
        };

        var debits = HostedAiSpendSweep.MapDebits(recent);

        Assert.Equal(2, debits.Count);
        Assert.All(debits, d => Assert.Equal(AccountHostedAiSpendStore.DebitKind, d.Kind));
        Assert.Equal(1500, debits[0].AmountMicros); // stored positive
        Assert.Equal(250, debits[1].AmountMicros);
    }

    [Fact]
    public void MapDebits_leaves_time_null_when_absent_or_unparseable()
    {
        var recent = new List<CloudCreditTransaction>
        {
            new("debit", -100, null),
            new("debit", -200, "not-a-date"),
        };

        var debits = HostedAiSpendSweep.MapDebits(recent);

        Assert.Equal(2, debits.Count);
        Assert.All(debits, d => Assert.Null(d.TransactionCreatedUtc));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    public void ParseUtc_returns_null_for_absent_or_unparseable(string? s)
    {
        Assert.Null(HostedAiSpendSweep.ParseUtc(s));
    }

    [Fact]
    public void ParseUtc_reads_an_iso_timestamp_as_utc()
    {
        var t = HostedAiSpendSweep.ParseUtc("2026-07-18T01:23:45Z");
        Assert.NotNull(t);
        Assert.Equal(DateTimeKind.Utc, t!.Value.Kind);
        Assert.Equal(new DateTime(2026, 7, 18, 1, 23, 45, DateTimeKind.Utc), t.Value);
    }

    [Fact]
    public void Mapped_debits_mirror_into_the_store_and_dedup_on_re_observe()
    {
        var store = NewStore();
        var recent = new List<CloudCreditTransaction>
        {
            new("debit", -1500, "2026-07-18T01:00:00Z"),
            new("debit", -250, "2026-07-18T03:00:00Z"),
            new("debit", -99, null), // no time: the store skips it (cannot de-dup)
        };

        var first = store.RecordObservedDebits(HostedAiSpendSweep.MapDebits(recent));
        Assert.Equal(2, first); // the null-time debit is skipped

        // Re-observing the same rolling window mirrors nothing new.
        var second = store.RecordObservedDebits(HostedAiSpendSweep.MapDebits(recent));
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task RunOnceAsync_signed_out_records_nothing()
    {
        var store = NewStore();
        var sweep = new HostedAiSpendSweep(
            accessToken: () => null, // signed out - an expected state, never a fabricated figure
            credits: new AccountCreditsClient(new HttpClient(new ThrowingHandler()), baseUrl: "http://stub"),
            store: store);

        await sweep.RunOnceAsync(CancellationToken.None);

        Assert.Empty(store.List());
    }

    [Fact]
    public async Task RunOnceAsync_mirrors_debits_from_the_ledger()
    {
        var store = NewStore();
        var body = "{\"data\":{\"balance_micros\":9000,\"transactions\":[" +
                   "{\"kind\":\"debit\",\"amount_micros\":-1500,\"created_at\":\"2026-07-18T01:00:00Z\"}," +
                   "{\"kind\":\"credit\",\"amount_micros\":5000,\"created_at\":\"2026-07-18T02:00:00Z\"}]}}";
        var sweep = new HostedAiSpendSweep(
            accessToken: () => "tok",
            credits: new AccountCreditsClient(new HttpClient(new CannedHandler(body)), baseUrl: "http://stub"),
            store: store);

        await sweep.RunOnceAsync(CancellationToken.None);

        var rows = store.List();
        Assert.Single(rows); // only the debit, not the top-up
        Assert.Equal(1500, rows[0].AmountMicros);
    }

    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly string _body;
        public CannedHandler(string body) { _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("must not be called when signed out");
    }
}
