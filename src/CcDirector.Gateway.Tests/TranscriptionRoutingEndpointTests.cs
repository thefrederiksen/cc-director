using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #506: HTTP wire test for the Gateway's <c>GET /transcription/routing</c> endpoint. Boots
/// only <see cref="TranscriptionRoutingEndpoint"/> on an ephemeral port with a temp-file vault and
/// a temp CC_DIRECTOR_ROOT (so the test owns the transcription_mode config). Proves the Gateway
/// composes the correct (mode, baseUrl, model, key) pair per mode server-side, and - the
/// security-critical invariant - NEVER returns the bring-your-own OpenAI key with the devthrottle.com
/// URL (or vice versa). In the "DirectorRoot" collection because it sets CC_DIRECTOR_ROOT.
/// </summary>
[Collection("DirectorRoot")]
public sealed class TranscriptionRoutingEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private string _vaultPath = null!;

    public TranscriptionRoutingEndpointTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-routing-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CcStorage.ConfigJson())!);
        _vaultPath = Path.Combine(Path.GetTempPath(), "cc-vault-routing-" + Guid.NewGuid().ToString("N") + ".json");

        var port = AllocateFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add(baseUrl);
        TranscriptionRoutingEndpoint.Map(_app, new KeyVault(_vaultPath));
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (File.Exists(_vaultPath)) File.Delete(_vaultPath); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void SeedVault(string name, string value) => new KeyVault(_vaultPath).Set(name, value);

    [Fact]
    public async Task Routing_DevThrottleMode_ComposesDevThrottlePair()
    {
        SeedVault(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_xyz");

        var resp = await _http.GetAsync("/transcription/routing");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal("devthrottle", root.GetProperty("mode").GetString());
        // Issue #513: DevThrottle carries the batch transport + the provider-correct Groq model.
        Assert.Equal("batch", root.GetProperty("transport").GetString());
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleBaseUrl, root.GetProperty("baseUrl").GetString());
        Assert.Equal(TranscriptionEndpointResolver.DevThrottleModel, root.GetProperty("model").GetString());
        Assert.Equal("whisper-large-v3", root.GetProperty("model").GetString());
        Assert.Equal("dt_live_xyz", root.GetProperty("key").GetString());
    }

    [Fact]
    public async Task Routing_KeyNotSet_Returns404_WithMarkerHeader()
    {
        // No key seeded.
        var resp = await _http.GetAsync("/transcription/routing");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        // The marker header is present so a Director can tell this from an older Gateway's framework 404.
        Assert.True(resp.Headers.Contains("X-Transcription-Routing"));
    }

    [Fact]
    public async Task Routing_DevThrottleMode_KeyNotSet_Returns404_WithMarkerHeader()
    {
        // Issue #887: DevThrottle is the default hosted mode and is key-gated like BYO. With no
        // account key set it returns 404 with the marker header (never a baked-in URL), so the Director
        // reports it unavailable and prompts to add credits.
        // No key seeded.
        var resp = await _http.GetAsync("/transcription/routing");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.True(resp.Headers.Contains("X-Transcription-Routing"));
    }

    [Fact]
    public async Task Routing_NoModeConfigured_NoKeys_DefaultsToDevThrottle_Returns404()
    {
        // Issue #887 acceptance: a Gateway with NO transcription mode set defaults to DevThrottle
        // (hosted). With no account key it returns 404 with the marker header - it needs the signed-in
        // account's key rather than an out-of-the-box local model.
        // (The temp CC_DIRECTOR_ROOT this test owns has no transcription_mode written.)
        var resp = await _http.GetAsync("/transcription/routing");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.True(resp.Headers.Contains("X-Transcription-Routing"));
    }

    private static int AllocateFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
