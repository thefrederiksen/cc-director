using System.Net;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Voice;
using Xunit;

namespace CcDirector.Core.Tests.Voice;

/// <summary>
/// Proves the consolidated AI provider drives <see cref="TtsService"/> when a key resolver is supplied
/// (the desktop / Director path): the base URL, credential, voice, and model all follow the selected
/// provider. Runs against an isolated CC_DIRECTOR_ROOT (config.json + a standalone key vault), with a
/// capturing handler so the outgoing request is asserted without network. In the CcStorageRoot
/// collection so it never races other root-mutating tests.
/// </summary>
[Collection("CcStorageRoot")]
public sealed class TtsServiceProviderTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public TtsServiceProviderTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-ttsprov-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Records the one request it is sent and returns tiny valid audio.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
        }
    }

    [Fact]
    public async Task GenerateAsync_DevThrottleProvider_PostsToProxyWithAccountKeyAndConfiguredVoice()
    {
        // DevThrottle is the default mode; pick a non-default voice to prove it is read from config.
        TtsVoiceConfig.Set("onyx");
        var vault = new KeyVault(Path.Combine(_root, "vault.dat"));
        vault.Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_testkey");

        var resolver = new TranscriptionKeyResolver(() => new GatewayConfig(), http: null, localVault: vault);
        var handler = new CapturingHandler();
        var svc = new TtsService(new AgentOptions(), handler, resolver);

        var result = await svc.GenerateAsync("Hello there.", null, null);

        Assert.True(result.Success);
        Assert.NotNull(handler.Request);
        Assert.Equal("https://devthrottle.com/api/v1/audio/speech", handler.Request!.RequestUri!.ToString());
        Assert.Equal("dt_live_testkey", handler.Request.Headers.Authorization!.Parameter);
        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("onyx", doc.RootElement.GetProperty("voice").GetString());
        Assert.Equal("hexgrad/Kokoro-82M", doc.RootElement.GetProperty("model").GetString());   // DevThrottle default speech model
    }

    [Fact]
    public async Task GenerateAsync_DevThrottleProvider_NoKey_ReturnsNoKey()
    {
        // Signed-out / no account key: a clear no_key result.
        var vault = new KeyVault(Path.Combine(_root, "vault.dat"));
        var resolver = new TranscriptionKeyResolver(() => new GatewayConfig(), http: null, localVault: vault);
        var svc = new TtsService(new AgentOptions(), new CapturingHandler(), resolver);

        var result = await svc.GenerateAsync("Hello.", null, null);

        Assert.False(result.Success);
        Assert.Equal("no_key", result.Status);
    }
}
