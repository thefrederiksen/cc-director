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
/// End-to-end proof for the consolidated AI-provider surface (the one switch that drives transcription
/// + wingman model + text-to-speech voice) and the TTS-voice endpoint. Boots a real GatewayHost on an
/// ephemeral port with CC_DIRECTOR_ROOT redirected to a temp dir, so writes round-trip an isolated
/// config.json rather than the user's real one.
/// </summary>
[Collection("DirectorRoot")]
public sealed class AiProviderEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-aiprov-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public AiProviderEndpointTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-aiprov-test-" + Guid.NewGuid().ToString("N"));
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
    public async Task Get_ai_provider_defaults_to_devthrottle_with_glm()
    {
        var obj = await _http.GetFromJsonAsync<JsonObject>("gateway/ai-provider");
        Assert.NotNull(obj);
        Assert.Equal("devthrottle", (string?)obj!["provider"]);
        Assert.Equal("zai-org/GLM-5.2", (string?)obj["wingmanModel"]);
        Assert.Equal("Qwen/Qwen2.5-72B-Instruct", (string?)obj["wingmanFastModel"]);
        Assert.Equal("whisper-large-v3", (string?)obj["transcriptionModel"]);
        Assert.Equal("af_bella", (string?)obj["ttsVoice"]);   // DevThrottle (Kokoro) default voice
        var voices = obj["voices"] as JsonArray;
        Assert.NotNull(voices);
        Assert.Contains(voices!, v => (string?)v == "nova");   // OpenAI voice fallback set
    }

    [Fact]
    public async Task Put_ai_provider_openai_sets_mode_byo_and_gpt55_and_round_trips()
    {
        var resp = await _http.PutAsJsonAsync("gateway/ai-provider", new { provider = "openai" });
        resp.EnsureSuccessStatusCode();

        var echoed = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("openai", (string?)echoed!["provider"]);
        Assert.Equal("gpt-5.5", (string?)echoed["wingmanModel"]);
        Assert.Equal("gpt-5.5-mini", (string?)echoed["wingmanFastModel"]);

        // Durable on disk: transcription_mode flips to byo AND the wingman model default is persisted.
        var onDisk = CcDirectorConfigService.ReadRaw();
        Assert.Equal("byo", (string?)onDisk["transcription_mode"]);
        Assert.Equal("gpt-5.5", (string?)onDisk["brain_model"]);
        Assert.Equal("gpt-5.5-mini", (string?)onDisk["brain_model_fast"]);

        // The GET reflects the saved choice after a reload.
        var obj = await _http.GetFromJsonAsync<JsonObject>("gateway/ai-provider");
        Assert.Equal("openai", (string?)obj!["provider"]);
        Assert.Equal("gpt-5.5", (string?)obj["wingmanModel"]);
        Assert.Equal("gpt-5.5-mini", (string?)obj["wingmanFastModel"]);
    }

    [Fact]
    public async Task Put_ai_provider_back_to_devthrottle_restores_glm()
    {
        await _http.PutAsJsonAsync("gateway/ai-provider", new { provider = "openai" });
        var resp = await _http.PutAsJsonAsync("gateway/ai-provider", new { provider = "devthrottle" });
        resp.EnsureSuccessStatusCode();

        var onDisk = CcDirectorConfigService.ReadRaw();
        Assert.Equal("devthrottle", (string?)onDisk["transcription_mode"]);
        Assert.Equal("zai-org/GLM-5.2", (string?)onDisk["brain_model"]);
        Assert.Equal("Qwen/Qwen2.5-72B-Instruct", (string?)onDisk["brain_model_fast"]);
    }

    [Fact]
    public async Task Put_ai_provider_rejects_unknown_provider()
    {
        var resp = await _http.PutAsJsonAsync("gateway/ai-provider", new { provider = "anthropic" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        // Nothing written.
        Assert.Null(CcDirectorConfigService.ReadRaw()["transcription_mode"]);
    }

    [Fact]
    public async Task Put_ai_provider_preserves_unrelated_config_sections()
    {
        CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["gateway"] = new JsonObject { ["url"] = "http://gw.example:7878" },
        });

        var resp = await _http.PutAsJsonAsync("gateway/ai-provider", new { provider = "openai" });
        resp.EnsureSuccessStatusCode();

        var onDisk = CcDirectorConfigService.ReadRaw();
        Assert.Equal("http://gw.example:7878", (string?)onDisk["gateway"]!["url"]);
        Assert.Equal("byo", (string?)onDisk["transcription_mode"]);
    }

    [Fact]
    public async Task Get_tts_voice_defaults_to_provider_default_with_fallback_set()
    {
        var obj = await _http.GetFromJsonAsync<JsonObject>("gateway/tts-voice");
        Assert.Equal("af_bella", (string?)obj!["voice"]);   // DevThrottle (Kokoro) default
        var voices = obj["voices"] as JsonArray;
        Assert.NotNull(voices);
        Assert.Contains(voices!, v => (string?)v == "shimmer");   // OpenAI fallback set
    }

    [Fact]
    public async Task Put_tts_voice_persists_and_round_trips()
    {
        var resp = await _http.PutAsJsonAsync("gateway/tts-voice", new { voice = "onyx" });
        resp.EnsureSuccessStatusCode();
        Assert.Equal("onyx", (string?)(await resp.Content.ReadFromJsonAsync<JsonObject>())!["voice"]);

        Assert.Equal("onyx", (string?)CcDirectorConfigService.ReadRaw()["tts_voice"]);
        var obj = await _http.GetFromJsonAsync<JsonObject>("gateway/tts-voice");
        Assert.Equal("onyx", (string?)obj!["voice"]);
    }

    [Fact]
    public async Task Put_tts_voice_accepts_any_voice_id()
    {
        // Voices are dynamic + provider-specific (Kokoro's af_bella is not an OpenAI voice), so any
        // non-empty id is accepted - there is no fixed allow-list.
        var resp = await _http.PutAsJsonAsync("gateway/tts-voice", new { voice = "af_bella" });
        resp.EnsureSuccessStatusCode();
        Assert.Equal("af_bella", (string?)(await resp.Content.ReadFromJsonAsync<JsonObject>())!["voice"]);
        Assert.Equal("af_bella", (string?)CcDirectorConfigService.ReadRaw()["tts_voice"]);
    }

    [Fact]
    public async Task Put_tts_voice_rejects_empty()
    {
        var resp = await _http.PutAsJsonAsync("gateway/tts-voice", new { voice = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Put_tts_voice_rejects_non_object_body()
    {
        var content = new StringContent("\"nova\"", Encoding.UTF8, "application/json");
        var resp = await _http.PutAsync("gateway/tts-voice", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
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
