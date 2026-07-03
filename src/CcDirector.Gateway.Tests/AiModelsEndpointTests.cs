using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end proof for the AI model catalog + test surface (the Settings AI tab's model/voice pickers).
/// Boots a real GatewayHost on an ephemeral port with an isolated CC_DIRECTOR_ROOT. The list + test-chat
/// routes need a live provider, so here we prove: the model setters persist and round-trip through the
/// /gateway/ai-provider snapshot, and the list route reports "not signed in" (503) with no key - never a
/// crash or a silent empty list.
/// </summary>
[Collection("DirectorRoot")]
public sealed class AiModelsEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-aimodels-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public AiModelsEndpointTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-aimodels-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: AllocateFreePort(), token: "test-token-12345", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token-12345");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Put_wingman_model_persists_and_snapshot_reflects_it()
    {
        var resp = await _http.PutAsJsonAsync("gateway/ai/wingman-model", new { model = "kimi-k2" });
        resp.EnsureSuccessStatusCode();
        Assert.Equal("kimi-k2", (string?)(await resp.Content.ReadFromJsonAsync<JsonObject>())!["model"]);

        Assert.Equal("kimi-k2", (string?)CcDirectorConfigService.ReadRaw()["brain_model"]);
        var snap = await _http.GetFromJsonAsync<JsonObject>("gateway/ai-provider");
        Assert.Equal("kimi-k2", (string?)snap!["wingmanModel"]);
    }

    [Fact]
    public async Task Put_tts_model_persists_and_snapshot_reflects_it()
    {
        var resp = await _http.PutAsJsonAsync("gateway/ai/tts-model", new { model = "kokoro" });
        resp.EnsureSuccessStatusCode();
        Assert.Equal("kokoro", (string?)(await resp.Content.ReadFromJsonAsync<JsonObject>())!["model"]);

        Assert.Equal("kokoro", (string?)CcDirectorConfigService.ReadRaw()["tts_model"]);
        var snap = await _http.GetFromJsonAsync<JsonObject>("gateway/ai-provider");
        Assert.Equal("kokoro", (string?)snap!["ttsModel"]);
    }

    [Fact]
    public async Task Snapshot_defaults_wingman_and_tts_model()
    {
        var snap = await _http.GetFromJsonAsync<JsonObject>("gateway/ai-provider");
        Assert.Equal("zai-org/GLM-5.2", (string?)snap!["wingmanModel"]);   // provider default when unset
        Assert.Equal("hexgrad/Kokoro-82M", (string?)snap["ttsModel"]);
    }

    [Fact]
    public async Task Get_models_without_key_reports_not_signed_in()
    {
        // No DevThrottle key stored -> the catalog cannot be fetched; a clear 503, never a crash/empty.
        var resp = await _http.GetAsync("gateway/ai/models?kind=chat");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task Put_wingman_model_rejects_blank_and_non_object()
    {
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.PutAsJsonAsync("gateway/ai/wingman-model", new { model = "   " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.PutAsync("gateway/ai/tts-model", new StringContent("[1,2]", Encoding.UTF8, "application/json"))).StatusCode);
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
