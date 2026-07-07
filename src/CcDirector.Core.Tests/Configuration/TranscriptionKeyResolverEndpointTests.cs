using System.Net;
using System.Text;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Issue #506: on a Gateway, <see cref="TranscriptionKeyResolver.ResolveEndpointAsync"/> fetches the WHOLE
/// routing target (base URL + model + key) from the Gateway's <c>/transcription/routing</c> endpoint -
/// it no longer resolves the base URL from compile-time constants. These tests pin that the Director
/// consumes the Gateway's routing, that an older Gateway without the endpoint surfaces a clear
/// "update your Gateway" message (no silent baked-in URL), and that standalone still resolves locally.
/// All AI is DevThrottle-hosted - there is no bring-your-own provider.
/// </summary>
public sealed class TranscriptionKeyResolverEndpointTests
{
    // A configurable fake Gateway. Answers GET /transcription/routing with a chosen payload and
    // status, records every URL requested, and (by default) stamps the routing marker header so the
    // resolver can tell "key missing" (header present) from "older Gateway" (header absent).
    private sealed class RoutingHandler : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = new();
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string Body { get; init; } = "";
        public bool StampRoutingHeader { get; init; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.NotNull(request.RequestUri);
            RequestedUrls.Add(request.RequestUri.ToString());

            var resp = new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            };
            if (StampRoutingHeader)
                resp.Headers.Add("X-Transcription-Routing", "1");
            return Task.FromResult(resp);
        }
    }

    private static GatewayConfig Gateway() =>
        new() { Url = "http://gateway.test:7878", Token = "tok" };

    private static string RoutingJson(string baseUrl, string model, string key) =>
        $"{{\"mode\":\"devthrottle\",\"transport\":\"batch\",\"baseUrl\":\"{baseUrl}\",\"model\":\"{model}\",\"key\":\"{key}\"}}";

    [Fact]
    public async Task ResolveEndpoint_OnGateway_ConsumesGatewayRouting()
    {
        // The Gateway serves the whole target; the Director uses exactly what it is given.
        var handler = new RoutingHandler
        {
            Body = RoutingJson("https://devthrottle.com/api/v1", "whisper-large-v3", "dt_live_xyz"),
        };
        var http = new HttpClient(handler);
        var resolver = new TranscriptionKeyResolver(Gateway, http);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.NotNull(ep);
        Assert.Equal("https://devthrottle.com/api/v1", ep.BaseUrl);
        Assert.Equal("dt_live_xyz", ep.ApiKey);
        Assert.Equal("whisper-large-v3", ep.Model);
        // The on-Gateway path hits the routing endpoint, never the local URL constants.
        Assert.Single(handler.RequestedUrls);
        Assert.EndsWith("/transcription/routing", handler.RequestedUrls[0]);
    }

    [Fact]
    public async Task ResolveEndpoint_OnGateway_UsesGatewayBaseUrl_NotLocalConstant()
    {
        // A custom URL the Director could never have baked in proves the URL came from the Gateway.
        var handler = new RoutingHandler
        {
            Body = RoutingJson("https://proxy.example.test/v9", "whisper-large-v3", "dt_live_x"),
        };
        var http = new HttpClient(handler);
        var resolver = new TranscriptionKeyResolver(Gateway, http);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.NotNull(ep);
        Assert.Equal("https://proxy.example.test/v9", ep.BaseUrl);
        Assert.NotEqual(TranscriptionEndpointResolver.DevThrottleBaseUrl, ep.BaseUrl);
    }

    [Fact]
    public async Task ResolveEndpoint_OnGateway_KeyNotSet_ReturnsNull_AndShowsAccountMessage()
    {
        // Gateway has the route (marker header present) but no key -> 404.
        var handler = new RoutingHandler
        {
            Status = HttpStatusCode.NotFound,
            Body = "{\"error\":\"no DevThrottle key set\",\"mode\":\"devthrottle\"}",
            StampRoutingHeader = true,
        };
        var http = new HttpClient(handler);
        var resolver = new TranscriptionKeyResolver(Gateway, http);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.Null(ep);
        // The "key not set" message names where to set it, NOT the update-your-Gateway message.
        Assert.Contains("DevThrottle", resolver.UnavailableMessage);
        Assert.DoesNotContain("out of date", resolver.UnavailableMessage);
    }

    [Fact]
    public async Task ResolveEndpoint_OlderGatewayWithoutRoutingEndpoint_ReturnsNull_AndShowsUpdateMessage()
    {
        // An older Gateway never mapped the route: a framework 404 with NO routing marker header.
        // No silent fallback to a baked-in URL - the user is told to update the Gateway.
        var handler = new RoutingHandler
        {
            Status = HttpStatusCode.NotFound,
            Body = "Not Found",
            StampRoutingHeader = false,
        };
        var http = new HttpClient(handler);
        var resolver = new TranscriptionKeyResolver(Gateway, http);

        var ep = await resolver.ResolveEndpointAsync();

        Assert.Null(ep);
        Assert.Contains("out of date", resolver.UnavailableMessage);
        Assert.Contains("Update your Gateway", resolver.UnavailableMessage);
    }

    [Fact]
    public async Task ResolveEndpoint_Standalone_UsesLocalVaultKey()
    {
        // No gateway configured: the LOCAL key vault is the single key store (issue #839), so a
        // DevThrottle key seeded in the local vault serves the standalone path, resolved locally.
        var vaultPath = Path.Combine(Path.GetTempPath(), "ccd-resolvertest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vault = new KeyVault(vaultPath);
            vault.Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_local123");
            var standalone = new GatewayConfig();
            var resolver = new TranscriptionKeyResolver(() => standalone, localVault: vault);

            var ep = await resolver.ResolveEndpointAsync();

            Assert.NotNull(ep);
            Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, ep.BaseUrl);
            Assert.Equal("dt_live_local123", ep.ApiKey);
            Assert.Equal(TranscriptionEndpointResolver.DevThrottleModel, ep.Model);
        }
        finally
        {
            try { if (File.Exists(vaultPath)) File.Delete(vaultPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task ResolveEndpoint_Standalone_NoLocalKey_ReturnsNull()
    {
        // Standalone with an empty local vault yields no key (issue #839: the vault is the only store).
        var vaultPath = Path.Combine(Path.GetTempPath(), "ccd-resolvertest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var standalone = new GatewayConfig();
            var resolver = new TranscriptionKeyResolver(() => standalone, localVault: new KeyVault(vaultPath));

            var ep = await resolver.ResolveEndpointAsync();

            Assert.Null(ep);
        }
        finally
        {
            try { if (File.Exists(vaultPath)) File.Delete(vaultPath); } catch { /* best effort */ }
        }
    }
}
