using System.Net;
using System.Net.Http;
using System.Text;
using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Proves the Director-side read-only Gateway credits client (issue #940, epic #937). It reads
/// <c>GET {gateway.url}/account/credits</c> with the optional Bearer token and surfaces the balance the
/// desktop hosted-AI readiness check gates on. Because the desktop must never block on an unreadable
/// balance, a signed-out / unreachable / erroring Gateway is reported as an unknown balance (null),
/// never thrown. Every test injects a fake handler so no real network call is made.
/// </summary>
public sealed class GatewayAccountCreditsClientTests
{
    private const string GatewayUrl = "http://127.0.0.1:7878";

    [Fact]
    public async Task GetCreditsAsync_NoGatewayUrl_ReturnsNotConfigured_AndMakesNoCall()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{}");
        var client = new GatewayAccountCreditsClient(new HttpClient(handler));

        var credits = await client.GetCreditsAsync(new GatewayConfig { Url = "" });

        Assert.False(credits.GatewayConfigured);
        Assert.Null(credits.BalanceMicros);       // unknown
        Assert.Null(handler.Request);              // no network call when no Gateway is configured
    }

    [Fact]
    public async Task GetCreditsAsync_SignedIn_ReadsBalance_AndSendsBearer()
    {
        // balanceAvailable is what gates the balance since issue #984 - "the caller is signed in" and "a
        // balance was read" became two fields because on the hosted Gateway they differ.
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"signedIn\":true,\"balanceAvailable\":true,\"balanceMicros\":5000000,\"lastDebitMicros\":1200}");
        var client = new GatewayAccountCreditsClient(new HttpClient(handler));

        var credits = await client.GetCreditsAsync(new GatewayConfig { Url = GatewayUrl, Token = "tok-123" });

        Assert.True(credits.GatewayConfigured);
        Assert.True(credits.Reachable);
        Assert.True(credits.SignedIn);
        Assert.Equal(5_000_000, credits.BalanceMicros);
        Assert.Null(credits.Error);

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal($"{GatewayUrl}/account/credits", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("tok-123", handler.Request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task GetCreditsAsync_SignedInZeroBalance_ReturnsZero_NotNull()
    {
        // A real zero balance (out of credits) is a KNOWN value the readiness check must gate on -
        // distinct from unknown/null.
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"signedIn\":true,\"balanceAvailable\":true,\"balanceMicros\":0}");
        var client = new GatewayAccountCreditsClient(new HttpClient(handler));

        var credits = await client.GetCreditsAsync(new GatewayConfig { Url = GatewayUrl });

        Assert.True(credits.SignedIn);
        Assert.Equal(0, credits.BalanceMicros);
    }

    [Fact]
    public async Task GetCreditsAsync_SignedInButBalanceUnavailable_KeepsTheCallerSignedIn_AndTreatsTheBalanceAsUnknown()
    {
        // Issue #984, the hosted shape. The Gateway now says, in one body, that the CALLER is signed in and
        // that no BALANCE could be read - a combination the old single boolean could not express, which is
        // why hosted customers were shown "not signed in" on a billing surface. The desktop must carry both
        // facts through: still signed in, balance UNKNOWN (never a fabricated zero, and never blocking - the
        // authoritative out-of-credits gate is the runtime 402), and the Gateway's own message preserved.
        var handler = new CapturingHandler(HttpStatusCode.OK,
            "{\"signedIn\":true,\"balanceAvailable\":false,\"message\":\"Your account is active and your credit balance is unaffected.\"}");
        var client = new GatewayAccountCreditsClient(new HttpClient(handler));

        var credits = await client.GetCreditsAsync(new GatewayConfig { Url = GatewayUrl });

        Assert.True(credits.Reachable);
        Assert.True(credits.SignedIn);
        Assert.Null(credits.BalanceMicros);
        Assert.Equal("Your account is active and your credit balance is unaffected.", credits.Error);
    }

    [Fact]
    public async Task GetCreditsAsync_BalanceNotAvailable_IgnoresAnyBalanceInTheBody()
    {
        // Defence in depth for the field that now gates the balance: if a body ever carries a figure while
        // declaring the balance unavailable, the declaration wins. A stale or partial number on a billing
        // surface is a worse answer than an honest unknown.
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"signedIn\":true,\"balanceAvailable\":false,\"balanceMicros\":123}");
        var client = new GatewayAccountCreditsClient(new HttpClient(handler));

        var credits = await client.GetCreditsAsync(new GatewayConfig { Url = GatewayUrl });

        Assert.Null(credits.BalanceMicros);
    }

    [Fact]
    public async Task GetCreditsAsync_SignedOut_BalanceUnknown()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"signedIn\":false}");
        var client = new GatewayAccountCreditsClient(new HttpClient(handler));

        var credits = await client.GetCreditsAsync(new GatewayConfig { Url = GatewayUrl });

        Assert.True(credits.Reachable);
        Assert.False(credits.SignedIn);
        Assert.Null(credits.BalanceMicros);   // never a fabricated zero when signed out
    }

    [Fact]
    public async Task GetCreditsAsync_NoToken_SendsNoAuthorizationHeader()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"signedIn\":false}");
        var client = new GatewayAccountCreditsClient(new HttpClient(handler));

        await client.GetCreditsAsync(new GatewayConfig { Url = GatewayUrl });

        Assert.Null(handler.Request!.Headers.Authorization);
    }

    [Fact]
    public async Task GetCreditsAsync_NonSuccess_BalanceUnknown_WithReason_DoesNotThrow()
    {
        var handler = new CapturingHandler(HttpStatusCode.BadGateway, "{\"error\":\"cloud unreachable\"}");
        var client = new GatewayAccountCreditsClient(new HttpClient(handler));

        var credits = await client.GetCreditsAsync(new GatewayConfig { Url = GatewayUrl });

        Assert.True(credits.GatewayConfigured);
        Assert.False(credits.Reachable);
        Assert.Null(credits.BalanceMicros);
        Assert.NotNull(credits.Error);
        Assert.Contains("502", credits.Error);
    }

    [Fact]
    public async Task GetCreditsAsync_TransportFailure_BalanceUnknown_DoesNotThrow()
    {
        var client = new GatewayAccountCreditsClient(new HttpClient(new ThrowingHandler()));

        var credits = await client.GetCreditsAsync(new GatewayConfig { Url = GatewayUrl });

        Assert.False(credits.Reachable);
        Assert.Null(credits.BalanceMicros);
        Assert.NotNull(credits.Error);
    }

    [Fact]
    public async Task GetCreditsAsync_TrailingSlashUrl_DoesNotDoubleSlash()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"signedIn\":false}");
        var client = new GatewayAccountCreditsClient(new HttpClient(handler));

        await client.GetCreditsAsync(new GatewayConfig { Url = GatewayUrl + "/" });

        Assert.Equal($"{GatewayUrl}/account/credits", handler.Request!.RequestUri!.ToString());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _responseBody;
        public CapturingHandler(HttpStatusCode status, string responseBody) { _status = status; _responseBody = responseBody; }
        public HttpRequestMessage? Request { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
