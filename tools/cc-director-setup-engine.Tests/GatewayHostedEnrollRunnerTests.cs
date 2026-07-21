using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests for the HOSTED gateway join - the leg that gives <c>POST /devices/enroll-hosted</c> a client. They
/// prove the hosted enrollment logic without a real browser, a live hosted gateway, or real disk writes: an
/// injected sign-in stands in for the browser hand-back, a capturing fake HTTP handler stands in for the
/// hosted gateway, and a capturing action stands in for the config.json persist.
///
/// What is proven, and which production line each proof protects:
///  - The hosted call presents the ACCOUNT ACCESS TOKEN as its Bearer and goes to <c>/devices/enroll-hosted</c>
///    (protects the <c>http.DefaultRequestHeaders.Authorization = Bearer accountAccessToken</c> +
///    <c>PostAsJsonAsync("devices/enroll-hosted", ...)</c> lines in <c>EnrollAtHostedGatewayAsync</c>). This is
///    the one place hosted does NOT mirror self-host: there is no device-registry exchange and no cloud device
///    key - the account token itself is the authorization - so the test also asserts NO cloud register call is
///    made and the cloud device key never appears.
///  - Success persists the hosted URL + the issued key (protects the <c>_persist(hostedUrl, body.DeviceKey)</c> line).
///  - A 401, a non-2xx, and a 2xx carrying no device key each BLOCK with a clear reason and persist NOTHING
///    (protects the three guards ahead of that persist), so a machine can never be left pointed at the hosted
///    gateway holding a key the hosted gateway did not issue.
/// </summary>
[Collection(HostedGatewayUrlCollection.Name)]
public class GatewayHostedEnrollRunnerTests
{
    private const string DeviceId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string MachineName = "WORKSTATION-HOSTED";
    private const string AccountToken = "account-access-token-xyz";

    private static readonly DevThrottleTokens GoodTokens = new(AccountToken, "refresh-token-xyz");

    /// <summary>One captured request: where it went, what it carried, and - the point of this file - what it
    /// presented as its Bearer.</summary>
    private sealed record Captured(string Url, string Path, string Body, string? Bearer);

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder, List<Captured> log)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            log.Add(new Captured(request.RequestUri!.ToString(), request.RequestUri.AbsolutePath, body,
                request.Headers.Authorization?.Parameter));
            return responder(request);
        }
    }

    private static HttpResponseMessage Raw(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Obj(HttpStatusCode status, object body) =>
        Raw(status, JsonSerializer.Serialize(body));

    private static (GatewayAccountEnrollRunner runner, List<(string url, string key)> saved, List<Captured> reqs)
        BuildRunner(Func<HttpRequestMessage, HttpResponseMessage> responder,
                    Func<CancellationToken, Task<DevThrottleTokens>>? signIn = null)
    {
        var saved = new List<(string, string)>();
        var reqs = new List<Captured>();
        var runner = new GatewayAccountEnrollRunner(
            signIn: signIn ?? (_ => Task.FromResult(GoodTokens)),
            handlerFactory: () => new CapturingHandler(responder, reqs),
            persist: (url, key) => saved.Add((url, key)));
        return (runner, saved, reqs);
    }

    [Fact]
    public async Task SignInAndEnrollHosted_PresentsTheAccountTokenToEnrollHostedAndPersists()
    {
        var (runner, saved, reqs) = BuildRunner(_ => Obj(HttpStatusCode.OK, new { deviceKey = "hosted-key-abc", deviceCount = 1 }));

        var result = await runner.SignInAndEnrollHostedAsync(DeviceId, MachineName, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("hosted-key-abc", result.Value!.DeviceKey);

        // It went to the hosted enrollment seam, carrying the ACCOUNT token as its Bearer.
        var enroll = Assert.Single(reqs, r => r.Path == "/devices/enroll-hosted");
        Assert.Equal(AccountToken, enroll.Bearer);
        Assert.Contains(DeviceId, enroll.Body);
        Assert.Contains(MachineName, enroll.Body);

        // Hosted skips the device-registry exchange entirely: no cloud registration, and no /mobile/enroll.
        Assert.DoesNotContain(reqs, r => r.Path.EndsWith("/devices/register"));
        Assert.DoesNotContain(reqs, r => r.Path == "/mobile/enroll");
        Assert.Single(reqs);

        // The hosted URL and the issued key are persisted exactly once.
        var save = Assert.Single(saved);
        Assert.Equal(HostedGateway.DefaultUrl, save.url);
        Assert.Equal("hosted-key-abc", save.key);
    }

    [Fact]
    public async Task SignInAndEnrollHosted_TokenRejected_BlocksAndDoesNotPersist()
    {
        var (runner, saved, _) = BuildRunner(_ => Raw(HttpStatusCode.Unauthorized, "{\"error\":\"the account token is not valid\"}"));

        var result = await runner.SignInAndEnrollHostedAsync(DeviceId, MachineName, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("did not accept your sign-in", result.ErrorMessage);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task SignInAndEnrollHosted_NonSuccessStatus_BlocksAndDoesNotPersist()
    {
        var (runner, saved, _) = BuildRunner(_ => Raw(HttpStatusCode.BadRequest, "{\"error\":\"deviceId is required\"}"));

        var result = await runner.SignInAndEnrollHostedAsync(DeviceId, MachineName, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("refused the enrollment", result.ErrorMessage);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task SignInAndEnrollHosted_SuccessWithNoDeviceKey_BlocksAndDoesNotPersist()
    {
        var (runner, saved, _) = BuildRunner(_ => Obj(HttpStatusCode.OK, new { deviceCount = 1 }));

        var result = await runner.SignInAndEnrollHostedAsync(DeviceId, MachineName, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("returned no device key", result.ErrorMessage);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task SignInAndEnrollHosted_Unreachable_BlocksAndDoesNotPersist()
    {
        var (runner, saved, _) = BuildRunner(_ => throw new HttpRequestException("no route to host"));

        var result = await runner.SignInAndEnrollHostedAsync(DeviceId, MachineName, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Could not reach the DevThrottle hosted gateway", result.ErrorMessage);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task SignInAndEnrollHosted_SignInCancelled_BlocksAndMakesNoCall()
    {
        var (runner, saved, reqs) = BuildRunner(
            _ => throw new InvalidOperationException("no HTTP expected"),
            signIn: _ => throw new OperationCanceledException());

        var result = await runner.SignInAndEnrollHostedAsync(DeviceId, MachineName, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(reqs);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task EnrollWithHostedGateway_WithoutSignInFirst_BlocksAndMakesNoCall()
    {
        var (runner, saved, reqs) = BuildRunner(_ => throw new InvalidOperationException("no HTTP expected"));

        var result = await runner.EnrollWithHostedGatewayAsync(DeviceId, MachineName, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("sign in", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(reqs);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task SignInAndEnrollHosted_HonoursTheOperatorUrlOverride()
    {
        using var _override = new HostedGatewayUrlOverride("https://hosted.test:8443");
        var (runner, saved, reqs) = BuildRunner(_ => Obj(HttpStatusCode.OK, new { deviceKey = "hosted-key-override" }));

        var result = await runner.SignInAndEnrollHostedAsync(DeviceId, MachineName, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("https://hosted.test:8443/devices/enroll-hosted", Assert.Single(reqs).Url);
        Assert.Equal("https://hosted.test:8443", Assert.Single(saved).url);
    }
}

/// <summary>
/// The hosted gateway address is resolved from a PROCESS-WIDE environment variable, so every test that sets or
/// depends on it shares one collection - xUnit runs a collection's classes one at a time, which keeps a test
/// that points the override at a stub host from leaking into a test that expects the shipped address.
/// </summary>
[CollectionDefinition(Name)]
public class HostedGatewayUrlCollection
{
    public const string Name = "hosted-gateway-url";
}

/// <summary>Sets <c>DEVTHROTTLE_HOSTED_GATEWAY_URL</c> for the life of the scope and restores whatever was
/// there before, so an override test cannot change what the next test sees.</summary>
internal sealed class HostedGatewayUrlOverride : IDisposable
{
    private readonly string? _previous;

    public HostedGatewayUrlOverride(string? value)
    {
        _previous = Environment.GetEnvironmentVariable(HostedGateway.UrlEnvironmentVariable);
        Environment.SetEnvironmentVariable(HostedGateway.UrlEnvironmentVariable, value);
    }

    public void Dispose() =>
        Environment.SetEnvironmentVariable(HostedGateway.UrlEnvironmentVariable, _previous);
}

/// <summary>
/// Tests for <see cref="HostedGateway.ResolveUrl"/> - the production line that decides WHICH hosted gateway a
/// machine enrolls against. A typo in the operator override must fail loudly rather than quietly sending the
/// machine to production, so the throw is the guard under test.
/// </summary>
[Collection(HostedGatewayUrlCollection.Name)]
public class HostedGatewayTests
{
    [Fact]
    public void ResolveUrl_NoOverride_UsesTheShippedAddress()
    {
        using var _ = new HostedGatewayUrlOverride(null);
        Assert.Equal(HostedGateway.DefaultUrl, HostedGateway.ResolveUrl());
    }

    [Fact]
    public void ResolveUrl_Override_UsesItWithoutATrailingSlash()
    {
        using var _ = new HostedGatewayUrlOverride("https://hosted.test:8443/");
        Assert.Equal("https://hosted.test:8443", HostedGateway.ResolveUrl());
    }

    [Fact]
    public void ResolveUrl_OverrideIsNotAnAbsoluteUrl_Throws()
    {
        using var _ = new HostedGatewayUrlOverride("hosted.test");
        var ex = Assert.Throws<InvalidOperationException>(() => HostedGateway.ResolveUrl());
        Assert.Contains(HostedGateway.UrlEnvironmentVariable, ex.Message);
    }
}
