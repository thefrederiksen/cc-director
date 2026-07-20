using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using CcDirector.Core.Account;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Account;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// HTTP wire tests for the HOSTED human account sign-in on the reachable front-door callback (mission #474):
/// <c>POST /account/sign-in-callback</c> on a hosted Gateway MINTS a tenant-scoped device key instead of storing
/// a single-owner credential. These tests boot ONLY <see cref="AccountSignInCallbackEndpoint"/> with the hosted
/// dependencies and drive it over a real socket.
///
/// THE SECURITY PROPERTY, and the whole reason these exist: the mint is inherited from the ONE proven function
/// <see cref="HostedEnrollmentEndpoint.Enroll"/>, so this callback must NOT introduce a second, weaker token path.
/// Each proof therefore checks BOTH halves of a refusal - the status AND that no cookie was set and no tenant was
/// minted - because a 401 that still minted a tenant would satisfy a status-only assertion while having given away
/// the thing the boundary protects.
///
///   A valid token        -> 200, a tenant-scoped cc-gateway-token cookie, the account's own tenant.
///   A forged token        -> 401, NO cookie, NOTHING minted.
///   An expired token      -> 401, NO cookie, NOTHING minted.
///   A wrong-audience token -> 401, NO cookie, NOTHING minted.
///   NotEntitled           -> 402, NO cookie, NOTHING minted.
///   Unknown entitlement   -> 503 retry, NO cookie, NOTHING minted.
///
/// The account access token is used only to verify the subject and is discarded - it is never echoed in the
/// response (security rule DT-05).
/// </summary>
public sealed class HostedAccountSignInCallbackTests : IDisposable
{
    private const string Audience = "authenticated";
    private const string Issuer = "https://test.example.supabase.co/auth/v1";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _devPath = Path.Combine(Path.GetTempPath(), $"hac-dev-{Guid.NewGuid():N}.json");
    private readonly TestEs256Key _key = new();

    public void Dispose()
    {
        _harness.Dispose();
        _key.Dispose();
        if (File.Exists(_devPath)) File.Delete(_devPath);
    }

    /// <summary>Creates the payment-side entitlement table this Gateway only reads, and optionally seeds one active row.</summary>
    private GatewayDatabase OpenWithEntitlements(string subject, bool entitled)
    {
        var db = _harness.Open();
        using var ctx = db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS entitlements (" +
            "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
            "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
            "livemode INTEGER NULL)");
        if (entitled)
        {
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO entitlements (subject, status, livemode) VALUES ({0}, {1}, {2})",
                subject, EntitlementRegistry.StatusActive, true);
        }
        return db;
    }

    private void SeedEntitled(GatewayDatabase db, string subject)
    {
        using var ctx = db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw("INSERT INTO entitlements (subject, status, livemode) VALUES ({0}, {1}, {2})",
            subject, EntitlementRegistry.StatusActive, true);
    }

    private (DeviceRegistry devices, TenantRegistry tenants, HostedEnrollDependencies hosted) WireHosted(GatewayDatabase db)
    {
        var devices = new DeviceRegistry(_devPath);
        var tenants = new TenantRegistry(db);
        var validator = new JwtAccessTokenValidator(
            "test-signing-secret", timeProvider: null, publicKeySetJson: _key.PublicKeySetJson(),
            expectedAudience: Audience, expectedIssuer: Issuer, allowSymmetricHs256: false);
        var hosted = new HostedEnrollDependencies(devices, tenants, validator, new EntitlementRegistry(db, requireLivemode: false));
        return (devices, tenants, hosted);
    }

    private static async Task<(WebApplication app, HttpClient http)> StartHostedAsync(HostedEnrollDependencies? hosted, GatewaySignInService? signIn = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        AccountSignInCallbackEndpoint.Map(app, signIn, hosted);
        await app.StartAsync();

        // UseCookies=false so Set-Cookie is readable straight off the response headers (the harness never stores it).
        var http = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }

    /// <summary>The value of the cc-gateway-token cookie the response set, or null when it set none.</summary>
    private static string? GatewayCookieValue(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Set-Cookie", out var cookies)) return null;
        foreach (var c in cookies)
        {
            if (c.StartsWith(Util.AuthMiddleware.CookieName + "=", StringComparison.Ordinal))
            {
                var value = c.Substring(Util.AuthMiddleware.CookieName.Length + 1).Split(';')[0];
                return value.Length == 0 ? null : value;
            }
        }
        return null;
    }

    private static bool GatewayCookieIsHttpOnly(HttpResponseMessage resp) =>
        resp.Headers.TryGetValues("Set-Cookie", out var cookies)
        && cookies.Any(c => c.StartsWith(Util.AuthMiddleware.CookieName + "=", StringComparison.Ordinal)
                            && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));

    private static HttpContent Body(string accessToken, string deviceId) =>
        JsonContent.Create(new Dictionary<string, string> { ["access_token"] = accessToken, ["device_id"] = deviceId });

    private string Token(string subject) => _key.Token(subject, subject + "@example.com", Audience, Issuer);

    // -------- The positive control every refusal below is measured against. --------

    [Fact]
    public async Task ValidToken_MintsTenantScopedKey_SetsHttpOnlyCookie_NoTokenEchoed()
    {
        const string subject = "sub-alice";
        var db = OpenWithEntitlements(subject, entitled: true);
        var (devices, tenants, hosted) = WireHosted(db);
        var token = Token(subject);
        var (app, http) = await StartHostedAsync(hosted);
        try
        {
            var resp = await http.PostAsync(AccountSignInCallbackEndpoint.Path, Body(token, "dev-a"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            // The one credential the browser keeps is the tenant-scoped device key, in an HttpOnly cookie.
            var cookieKey = GatewayCookieValue(resp);
            Assert.False(string.IsNullOrEmpty(cookieKey));
            Assert.True(GatewayCookieIsHttpOnly(resp));

            // The cookie's key resolves to THIS account's tenant - the tunnel/cockpit reads the tenant from the key.
            Assert.False(string.IsNullOrEmpty(devices.TenantForKey(cookieKey!)));
            Assert.NotNull(tenants.LookupBySubject(subject));

            // Security (DT-05): the account access token is never echoed in the response body.
            var body = await resp.Content.ReadAsStringAsync();
            Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task TenantA_GetsOnlyItsOwnTenant_NotTenantB()
    {
        // Two distinct accounts sign in through the callback; each key resolves to its OWN tenant, never the
        // other's. This is the mint layer's contribution to "A is refused B's data": distinct, key-resolvable
        // tenants, so a later tenant-scoped read for A's key can never see B.
        var db = OpenWithEntitlements("sub-a", entitled: true);
        SeedEntitled(db, "sub-b");
        var (devices, _, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hosted);
        try
        {
            var respA = await http.PostAsync(AccountSignInCallbackEndpoint.Path, Body(Token("sub-a"), "dev-a"));
            var respB = await http.PostAsync(AccountSignInCallbackEndpoint.Path, Body(Token("sub-b"), "dev-b"));
            Assert.Equal(HttpStatusCode.OK, respA.StatusCode);
            Assert.Equal(HttpStatusCode.OK, respB.StatusCode);

            var tenantA = devices.TenantForKey(GatewayCookieValue(respA)!);
            var tenantB = devices.TenantForKey(GatewayCookieValue(respB)!);
            Assert.False(string.IsNullOrEmpty(tenantA));
            Assert.False(string.IsNullOrEmpty(tenantB));
            Assert.NotEqual(tenantA, tenantB);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    // -------- Bad tokens: each 401, NO cookie, NOTHING minted. --------

    [Fact]
    public async Task ForgedHs256Token_401_NoCookie_NothingMinted()
    {
        var db = OpenWithEntitlements("sub-attacker", entitled: true);
        var (_, tenants, hosted) = WireHosted(db);
        var forged = TestEs256Key.Hs256Token("test-signing-secret", "sub-attacker", Audience, Issuer);
        var (app, http) = await StartHostedAsync(hosted);
        try
        {
            var resp = await http.PostAsync(AccountSignInCallbackEndpoint.Path, Body(forged, "dev-a"));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject("sub-attacker"));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task ExpiredToken_401_NoCookie_NothingMinted()
    {
        var db = OpenWithEntitlements("sub-alice", entitled: true);
        var (_, tenants, hosted) = WireHosted(db);
        var expired = _key.ExpiredToken("sub-alice", "a@x.com", Audience, Issuer);
        var (app, http) = await StartHostedAsync(hosted);
        try
        {
            var resp = await http.PostAsync(AccountSignInCallbackEndpoint.Path, Body(expired, "dev-a"));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject("sub-alice"));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task WrongAudienceToken_401_NoCookie_NothingMinted()
    {
        var db = OpenWithEntitlements("sub-alice", entitled: true);
        var (_, tenants, hosted) = WireHosted(db);
        var wrongAud = _key.Token("sub-alice", "a@x.com", audience: "some-other-audience", issuer: Issuer);
        var (app, http) = await StartHostedAsync(hosted);
        try
        {
            var resp = await http.PostAsync(AccountSignInCallbackEndpoint.Path, Body(wrongAud, "dev-a"));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject("sub-alice"));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    // -------- The paid gate: 402 on NotEntitled, 503-retry on Unknown, NOTHING minted on either. --------

    [Fact]
    public async Task NotEntitled_402_NoCookie_NothingMinted()
    {
        // The read SUCCEEDS and finds no entitlement for this subject - knowledge, so it denies and mints nothing.
        var db = OpenWithEntitlements("sub-alice", entitled: false);   // table exists, no row for this subject
        var (_, tenants, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hosted);
        try
        {
            var resp = await http.PostAsync(AccountSignInCallbackEndpoint.Path, Body(Token("sub-alice"), "dev-a"));
            Assert.Equal(HttpStatusCode.PaymentRequired, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject("sub-alice"));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task UnknownEntitlement_503Retry_NoCookie_NothingMinted()
    {
        // The read FAILS (the entitlement table does not exist - a lost SELECT grant looks exactly like this).
        // It must retry, never 402 (a false lock-out of a payer) and above all never MINT (a silent give-away).
        var db = _harness.Open();   // no entitlements table created
        var (_, tenants, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hosted);
        try
        {
            var resp = await http.PostAsync(AccountSignInCallbackEndpoint.Path, Body(Token("sub-alice"), "dev-a"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            Assert.NotEqual(HttpStatusCode.PaymentRequired, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject("sub-alice"));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    // -------- Incomplete hand-back and the GET page. --------

    [Fact]
    public async Task MissingDeviceId_400_NothingMinted()
    {
        var db = OpenWithEntitlements("sub-alice", entitled: true);
        var (_, tenants, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hosted);
        try
        {
            // Body carries only the access token - the browser device id is missing, so the mint is failed loud.
            var body = JsonContent.Create(new Dictionary<string, string> { ["access_token"] = Token("sub-alice") });
            var resp = await http.PostAsync(AccountSignInCallbackEndpoint.Path, body);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject("sub-alice"));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Get_Hosted_ServesFragmentHandbackPage_NoTokenOnAnyUrl()
    {
        var db = OpenWithEntitlements("sub-alice", entitled: true);
        var (_, _, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hosted);
        try
        {
            var resp = await http.GetAsync(AccountSignInCallbackEndpoint.Path);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            // The served page reads the credential from the URL FRAGMENT (never the query), so no token rides a URL.
            Assert.Contains("location.hash", body, StringComparison.Ordinal);
            Assert.Contains("Completing sign-in", body, StringComparison.OrdinalIgnoreCase);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    // -------- The self-host control, asserted POSITIVELY: the hosted mint path is gated strictly on hosted. --------

    [Fact]
    public async Task SelfHost_HostedBodyShape_DoesNotMint_FailsLoud()
    {
        // With NO hosted dependencies (self-host), a body carrying {access_token, device_id} is NOT the self-host
        // hand-back shape (which needs the access+refresh pair), so it is failed loud and mints nothing. This is
        // the positive proof that the tenant-scoped mint runs ONLY on a hosted Gateway and never leaks into
        // self-host, where there is no tenant boundary at all.
        var signIn = new GatewaySignInService(SelfHostAccount());
        var (app, http) = await StartHostedAsync(hosted: null, signIn: signIn);
        try
        {
            var resp = await http.PostAsync(AccountSignInCallbackEndpoint.Path, Body(Token("sub-alice"), "dev-a"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.False(signIn.IsSignedIn());
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    private sealed class InMemoryTokenStore : IProtectedTokenStore
    {
        private DevThrottleTokens? _tokens;
        public bool HasTokens => _tokens is not null;
        public void Save(DevThrottleTokens tokens) => _tokens = tokens;
        public DevThrottleTokens? Load() => _tokens;
        public void Clear() => _tokens = null;
    }

    private static DevThrottleAccountService SelfHostAccount()
    {
        var previous = Environment.GetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, GatewayTestJwt.SigningSecret);
        try
        {
            var log = Path.Combine(Path.GetTempPath(), "cc-gw-hac-selfhost-" + Guid.NewGuid().ToString("N") + ".jsonl");
            return GatewayAccountFactory.Build(new InMemoryTokenStore(), log);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, previous);
        }
    }
}
