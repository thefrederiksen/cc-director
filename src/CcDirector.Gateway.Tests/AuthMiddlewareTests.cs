using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Proves the AuthMiddleware credential check accepts a per-device key on BOTH the Bearer header and
/// the cookie (issue #908). The cookie path matters because a browser WebSocket - the live terminal
/// stream - cannot set an Authorization header, so a phone authenticated by its own device key must be
/// able to open the stream via the cookie exactly as it calls the Bearer-authenticated endpoints.
/// </summary>
public sealed class AuthMiddlewareTests
{
    private const string SharedToken = "shared-machine-token";

    private static DeviceRegistry TempRegistry() =>
        new(Path.Combine(Path.GetTempPath(), "cc-authmw-" + Guid.NewGuid().ToString("N") + ".json"));

    private static HttpContext WithCookie(string value)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = $"{AuthMiddleware.CookieName}={value}";
        return ctx;
    }

    private static HttpContext WithRawCookieHeader(string value)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = value;
        return ctx;
    }

    private static HttpContext WithBearer(string value)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Authorization"] = $"Bearer {value}";
        return ctx;
    }

    [Fact]
    public void Cookie_with_a_valid_device_key_is_accepted()
    {
        var devices = TempRegistry();
        var key = devices.Register("phone-1", "PHONE").DeviceKey;
        Assert.True(AuthMiddleware.HasValidToken(WithCookie(key), SharedToken, devices));
    }

    [Fact]
    public void Cookie_with_the_shared_token_is_still_accepted()
    {
        var devices = TempRegistry();
        Assert.True(AuthMiddleware.HasValidToken(WithCookie(SharedToken), SharedToken, devices));
    }

    [Fact]
    public void Cookie_with_an_unknown_value_is_rejected()
    {
        var devices = TempRegistry();
        Assert.False(AuthMiddleware.HasValidToken(WithCookie("not-a-real-key"), SharedToken, devices));
    }

    [Fact]
    public void Duplicate_cookie_values_accept_a_later_valid_device_key()
    {
        var devices = TempRegistry();
        var key = devices.Register("browser-1088", "Chrome on Windows", "browser", "browser").DeviceKey;
        var ctx = WithRawCookieHeader($"{AuthMiddleware.CookieName}=stale-login-cookie; {AuthMiddleware.CookieName}={key}");

        Assert.True(AuthMiddleware.HasValidToken(ctx, SharedToken, devices));
    }

    [Fact]
    public void Duplicate_cookie_values_accept_a_later_encoded_valid_device_key()
    {
        var devices = TempRegistry();
        var key = devices.Register("browser-encoded-1088", "Chrome on Windows", "browser", "browser").DeviceKey;
        var encoded = Uri.EscapeDataString(key);
        var ctx = WithRawCookieHeader($"{AuthMiddleware.CookieName}=stale-login-cookie; other=1; {AuthMiddleware.CookieName}={encoded}");

        Assert.True(AuthMiddleware.HasValidToken(ctx, SharedToken, devices));
    }

    [Fact]
    public void Cookie_device_key_is_rejected_when_no_device_registry_is_configured()
    {
        var devices = TempRegistry();
        var key = devices.Register("phone-2", "PHONE").DeviceKey;
        // A null registry disables per-device-key auth, so the same cookie no longer validates.
        Assert.False(AuthMiddleware.HasValidToken(WithCookie(key), SharedToken, null));
    }

    [Fact]
    public void Bearer_with_a_valid_device_key_is_accepted()
    {
        var devices = TempRegistry();
        var key = devices.Register("phone-3", "PHONE").DeviceKey;
        Assert.True(AuthMiddleware.HasValidToken(WithBearer(key), SharedToken, devices));
    }
}
