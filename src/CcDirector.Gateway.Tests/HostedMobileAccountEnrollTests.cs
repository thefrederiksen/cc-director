using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using CcDirector.Core.Account;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// HTTP wire tests for the HOSTED human account sign-in on the phone/browser enrollment seam (mission #474):
/// <c>POST /m/enroll</c> on a hosted Gateway reads the human's account access token from the Authorization Bearer
/// header (a public pre-auth route, so the middleware does not pre-validate it as a device key) and MINTS a
/// tenant-scoped device key through the ONE mint <see cref="HostedEnrollmentEndpoint.Enroll"/> - never through a
/// second token path.
///
/// The proofs mirror the callback's: a valid token mints a tenant-scoped key and sets the session cookie; a
/// forged, expired, or wrong-audience Bearer each returns 401 and mints NOTHING; the paid gate returns 402 on
/// NotEntitled and 503-retry on Unknown and mints on NEITHER; and the self-host device-key-in-body path is
/// unchanged (the control, asserted positively). Every refusal checks BOTH halves - the status AND that no cookie
/// was set and no tenant was minted. The account token is never echoed (security rule DT-05).
/// </summary>
public sealed class HostedMobileAccountEnrollTests : IDisposable
{
    private const string Audience = "authenticated";
    private const string Issuer = "https://test.example.supabase.co/auth/v1";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _devPath = Path.Combine(Path.GetTempPath(), $"hma-dev-{Guid.NewGuid():N}.json");
    private readonly TestEs256Key _key = new();

    public void Dispose()
    {
        _harness.Dispose();
        _key.Dispose();
        if (File.Exists(_devPath)) File.Delete(_devPath);
    }

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

    /// <summary>A stand-in enrollment service; on the hosted branch it is never called, and on the self-host
    /// control it reports NotSignedIn (no cloud account), which is enough to prove the self-host path was taken.</summary>
    private MobileDeviceEnrollmentService UnusedService() =>
        new(account: null, new DeviceRegistryClient(new HttpClient()), new DeviceRegistry(_devPath));

    private async Task<(WebApplication app, HttpClient http)> StartHostedAsync(HostedEnrollDependencies? hosted)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        MobileEnrollmentEndpoint.Map(app, UnusedService(), hosted);
        await app.StartAsync();

        var http = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }

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

    /// <summary>POSTs /m/enroll with the account token in the Bearer header and the device id in the body.</summary>
    private static async Task<HttpResponseMessage> PostBearerAsync(HttpClient http, string? bearer, string deviceId, string platform = "android")
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/m/enroll")
        {
            Content = JsonContent.Create(new Dictionary<string, string?> { ["deviceId"] = deviceId, ["platform"] = platform, ["name"] = "phone" }),
        };
        if (bearer is not null) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await http.SendAsync(msg);
    }

    private string Token(string subject) => _key.Token(subject, subject + "@example.com", Audience, Issuer);

    [Fact]
    public async Task ValidBearerToken_ReturnsTenantScopedKey_SetsHttpOnlyCookie_NoTokenEchoed()
    {
        const string subject = "sub-alice";
        var db = OpenWithEntitlements(subject, entitled: true);
        var (devices, tenants, hosted) = WireHosted(db);
        var token = Token(subject);
        var (app, http) = await StartHostedAsync(hosted);
        try
        {
            var resp = await PostBearerAsync(http, token, "dev-a");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var cookieKey = GatewayCookieValue(resp);
            Assert.False(string.IsNullOrEmpty(cookieKey));
            Assert.True(GatewayCookieIsHttpOnly(resp));
            Assert.False(string.IsNullOrEmpty(devices.TenantForKey(cookieKey!)));
            Assert.NotNull(tenants.LookupBySubject(subject));

            var body = await resp.Content.ReadAsStringAsync();
            Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task TwoAccounts_GetDistinctTenants()
    {
        var db = OpenWithEntitlements("sub-a", entitled: true);
        SeedEntitled(db, "sub-b");
        var (devices, _, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hosted);
        try
        {
            var respA = await PostBearerAsync(http, Token("sub-a"), "dev-a");
            var respB = await PostBearerAsync(http, Token("sub-b"), "dev-b");
            Assert.Equal(HttpStatusCode.OK, respA.StatusCode);
            Assert.Equal(HttpStatusCode.OK, respB.StatusCode);
            var tenantA = devices.TenantForKey(GatewayCookieValue(respA)!);
            var tenantB = devices.TenantForKey(GatewayCookieValue(respB)!);
            Assert.False(string.IsNullOrEmpty(tenantA));
            Assert.NotEqual(tenantA, tenantB);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task ForgedHs256Bearer_401_NoCookie_NothingMinted()
    {
        var db = OpenWithEntitlements("sub-attacker", entitled: true);
        var (_, tenants, hosted) = WireHosted(db);
        var forged = TestEs256Key.Hs256Token("test-signing-secret", "sub-attacker", Audience, Issuer);
        var (app, http) = await StartHostedAsync(hosted);
        try
        {
            var resp = await PostBearerAsync(http, forged, "dev-a");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject("sub-attacker"));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task ExpiredBearer_401_NoCookie_NothingMinted()
    {
        var db = OpenWithEntitlements("sub-alice", entitled: true);
        var (_, tenants, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hosted);
        try
        {
            var resp = await PostBearerAsync(http, _key.ExpiredToken("sub-alice", "a@x.com", Audience, Issuer), "dev-a");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject("sub-alice"));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task WrongAudienceBearer_401_NoCookie_NothingMinted()
    {
        var db = OpenWithEntitlements("sub-alice", entitled: true);
        var (_, tenants, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hosted);
        try
        {
            var resp = await PostBearerAsync(http, _key.Token("sub-alice", "a@x.com", audience: "some-other-audience", issuer: Issuer), "dev-a");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject("sub-alice"));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task MissingBearer_401_NoCookie()
    {
        var db = OpenWithEntitlements("sub-alice", entitled: true);
        var (_, tenants, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hosted);
        try
        {
            var resp = await PostBearerAsync(http, bearer: null, "dev-a");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject("sub-alice"));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task NotEntitled_402_NoCookie_NothingMinted()
    {
        var db = OpenWithEntitlements("sub-alice", entitled: false);
        var (_, tenants, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hosted);
        try
        {
            var resp = await PostBearerAsync(http, Token("sub-alice"), "dev-a");
            Assert.Equal(HttpStatusCode.PaymentRequired, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject("sub-alice"));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task UnknownEntitlement_503Retry_NoCookie_NothingMinted()
    {
        var db = _harness.Open();   // no entitlements table -> the read fails -> Unknown
        var (_, tenants, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hosted);
        try
        {
            var resp = await PostBearerAsync(http, Token("sub-alice"), "dev-a");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            Assert.NotEqual(HttpStatusCode.PaymentRequired, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject("sub-alice"));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task MissingDeviceId_400_NothingMinted()
    {
        var db = OpenWithEntitlements("sub-alice", entitled: true);
        var (_, tenants, hosted) = WireHosted(db);
        var (app, http) = await StartHostedAsync(hosted);
        try
        {
            var resp = await PostBearerAsync(http, Token("sub-alice"), deviceId: "   ");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Null(GatewayCookieValue(resp));
            Assert.Null(tenants.LookupBySubject("sub-alice"));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task SelfHost_IgnoresBearer_TakesDeviceKeyInBodyPath_Unchanged()
    {
        // THE CONTROL, asserted positively. With NO hosted dependencies, /m/enroll is the self-host
        // device-key-in-body path: the Bearer account token is NOT treated as an account token, no tenant-scoped
        // mint runs, and the request flows to the enrollment service. With no cloud account wired that service
        // answers NotSignedIn (409) - which is precisely NOT the hosted mint's 200/401/402/503, so it proves the
        // hosted mint never engaged on self-host.
        var (app, http) = await StartHostedAsync(hosted: null);
        try
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, "/m/enroll")
            {
                Content = JsonContent.Create(new Dictionary<string, string?> { ["deviceKey"] = "dtd_some_cloud_key", ["deviceId"] = "dev-a", ["platform"] = "android" }),
            };
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("sub-alice"));
            var resp = await http.SendAsync(msg);

            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);   // NotSignedIn from the self-host service path
            Assert.Null(GatewayCookieValue(resp));
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }
}
