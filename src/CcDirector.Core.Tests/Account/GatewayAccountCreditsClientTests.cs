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
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"signedIn\":true,\"balanceMicros\":5000000,\"lastDebitMicros\":1200}");
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
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"signedIn\":true,\"balanceMicros\":0}");
        var client = new GatewayAccountCreditsClient(new HttpClient(handler));

        var credits = await client.GetCreditsAsync(new GatewayConfig { Url = GatewayUrl });

        Assert.True(credits.SignedIn);
        Assert.Equal(0, credits.BalanceMicros);
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
