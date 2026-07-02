using System.Net;
using System.Net.Http;
using System.Text;
using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Issue #884: the account credit-balance client parses the cloud
/// <c>{ data: { balance_micros, transactions[] } }</c> shape (captured live from production) and
/// authenticates with the account JWT. These tests prove the parse and the HTTP call without a network.
/// </summary>
public sealed class AccountCreditsClientTests
{
    // The exact live shape (from GET /api/v1/account/credits): balance in micro-dollars + a ledger.
    private const string LiveBody =
        "{\"data\":{\"balance_micros\":9993644,\"transactions\":[" +
        "{\"id\":\"a\",\"created_at\":\"2026-07-02T20:31:18Z\",\"kind\":\"debit\",\"amount_micros\":-556,\"balance_after_micros\":9993644}," +
        "{\"id\":\"b\",\"created_at\":\"2026-07-01T10:00:00Z\",\"kind\":\"credit\",\"amount_micros\":5000000,\"balance_after_micros\":9994200}" +
        "]}}";

    [Fact]
    public void Parse_LiveShape_ReadsBalanceAndTransactions()
    {
        var credits = AccountCreditsClient.Parse(LiveBody);

        Assert.Equal(9993644, credits.BalanceMicros);
        Assert.Equal(2, credits.Recent.Count);
        Assert.Equal("debit", credits.Recent[0].Kind);
        Assert.Equal(-556, credits.Recent[0].AmountMicros);
        Assert.Equal("credit", credits.Recent[1].Kind);
        Assert.Equal(5000000, credits.Recent[1].AmountMicros);
    }

    [Fact]
    public void Parse_NoTransactions_ReadsBalance_EmptyLedger()
    {
        var credits = AccountCreditsClient.Parse("{\"data\":{\"balance_micros\":0}}");
        Assert.Equal(0, credits.BalanceMicros);
        Assert.Empty(credits.Recent);
    }

    [Theory]
    [InlineData("{}")]                                  // no data envelope
    [InlineData("{\"data\":{}}")]                       // no balance
    [InlineData("[]")]                                  // not an object
    public void Parse_MalformedShape_Throws(string body)
        => Assert.Throws<InvalidOperationException>(() => AccountCreditsClient.Parse(body));

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public string? SeenAuth { get; private set; }
        public string? SeenUrl { get; private set; }
        public StubHandler(HttpStatusCode status, string body) { _status = status; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            SeenAuth = request.Headers.Authorization?.ToString();
            SeenUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task GetCreditsAsync_Success_ReturnsBalance_AndBearsTheJwt()
    {
        var handler = new StubHandler(HttpStatusCode.OK, LiveBody);
        var client = new AccountCreditsClient(new HttpClient(handler), baseUrl: "https://example.test");

        var credits = await client.GetCreditsAsync("the.jwt");

        Assert.Equal(9993644, credits.BalanceMicros);
        Assert.Equal("Bearer the.jwt", handler.SeenAuth);
        Assert.Equal("https://example.test/api/v1/account/credits", handler.SeenUrl);
    }

    [Fact]
    public async Task GetCreditsAsync_Non2xx_Throws()
    {
        var client = new AccountCreditsClient(new HttpClient(new StubHandler(HttpStatusCode.Unauthorized, "{}")), baseUrl: "https://example.test");
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetCreditsAsync("jwt"));
    }

    [Fact]
    public async Task GetCreditsAsync_NoToken_Throws()
    {
        var client = new AccountCreditsClient(new HttpClient(new StubHandler(HttpStatusCode.OK, LiveBody)), baseUrl: "https://example.test");
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetCreditsAsync(""));
    }
}
