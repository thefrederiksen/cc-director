using System.Net;
using CcDirector.Core.Account;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// HTTP wire tests for the reachable front-door sign-in CALLBACK (epic #1069, issue #1080): <c>GET
/// /account/sign-in-callback</c>. This is the routable address the cloud sign-in page redirects the user's
/// OWN browser back to, so a person on ANOTHER machine completes sign-in in their own browser. It boots only
/// <see cref="AccountSignInCallbackEndpoint"/> on an ephemeral port and proves:
/// <list type="bullet">
/// <item>a callback carrying both tokens leaves the Gateway signed in and lands on a signed-in page;</item>
/// <item>a callback missing the credential is failed loud (not signed in), never storing a half-credential;</item>
/// <item>neither the access JWT nor the refresh token is ever echoed back in the response (security rule DT-05);</item>
/// <item>a host with no sign-in flow reports an explicit "not available" rather than pretending to capture.</item>
/// </list>
/// </summary>
public sealed class AccountSignInCallbackEndpointTests
{
    private sealed class InMemoryTokenStore : IProtectedTokenStore
    {
        private DevThrottleTokens? _tokens;
        public bool HasTokens => _tokens is not null;
        public void Save(DevThrottleTokens tokens) => _tokens = tokens;
        public DevThrottleTokens? Load() => _tokens;
        public void Clear() => _tokens = null;
    }

    private static DevThrottleAccountService MakeAccount()
    {
        var previous = Environment.GetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, GatewayTestJwt.SigningSecret);
        try
        {
            var authEventsLog = Path.Combine(Path.GetTempPath(), "cc-gw-acct-signin-callback-" + Guid.NewGuid().ToString("N") + ".jsonl");
            return GatewayAccountFactory.Build(new InMemoryTokenStore(), authEventsLog);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, previous);
        }
    }

    private static async Task<(WebApplication app, HttpClient http)> StartAsync(GatewaySignInService? signIn)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        AccountSignInCallbackEndpoint.Map(app, signIn);
        await app.StartAsync();

        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }

    // A callback carrying a valid token pair leaves the Gateway signed in and shows a signed-in page.
    [Fact]
    public async Task Get_Callback_WithTokenPair_SignsInAndReportsSignedIn_NoTokenEchoed()
    {
        var jwt = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1));
        const string refreshToken = "REFRESH-TOKEN-PLAINTEXT-MARKER-1080";
        var signIn = new GatewaySignInService(MakeAccount());
        Assert.False(signIn.IsSignedIn());

        var (app, http) = await StartAsync(signIn);
        try
        {
            var url = $"{AccountSignInCallbackEndpoint.Path}?access_token={Uri.EscapeDataString(jwt)}&refresh_token={Uri.EscapeDataString(refreshToken)}";
            var resp = await http.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            // Acceptance criterion 1 / 4: the Gateway is now signed in after the remote hand-back.
            Assert.True(signIn.IsSignedIn());

            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("signed in", body, StringComparison.OrdinalIgnoreCase);
            // Security (DT-05): the response never carries the access JWT or the refresh token.
            Assert.DoesNotContain(jwt, body, StringComparison.Ordinal);
            Assert.DoesNotContain(refreshToken, body, StringComparison.Ordinal);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // A callback missing the credential is failed loud: not signed in, no half-credential stored, no token.
    [Fact]
    public async Task Get_Callback_MissingTokens_FailsLoud_StaysSignedOut()
    {
        var signIn = new GatewaySignInService(MakeAccount());

        var (app, http) = await StartAsync(signIn);
        try
        {
            var resp = await http.GetAsync(AccountSignInCallbackEndpoint.Path);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            Assert.False(signIn.IsSignedIn());

            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("did not complete", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // No sign-in flow on this host: report it explicitly, capture nothing, echo no token.
    [Fact]
    public async Task Get_Callback_NoSignInFlow_ReportsNotAvailable()
    {
        var (app, http) = await StartAsync(signIn: null);
        try
        {
            const string bogus = "TOKEN-THAT-MUST-NOT-BE-STORED-OR-ECHOED-1080";
            var url = $"{AccountSignInCallbackEndpoint.Path}?access_token={bogus}&refresh_token={bogus}";
            var resp = await http.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("not available", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(bogus, body, StringComparison.Ordinal);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }
}
