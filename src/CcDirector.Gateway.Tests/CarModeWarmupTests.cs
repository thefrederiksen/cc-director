using System.Net;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.CarMode;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Car Mode keep-warm (performance round) fires one tiny request to the hosted model and one to the
/// text-to-speech provider so a cold-start is paid before the drive, not during it. These tests drive it
/// with a stub HTTP handler (no network) to prove it warms BOTH providers, skips a leg with no key without
/// erroring, and NEVER throws on an upstream failure (a warmup is best-effort - it must not disrupt a turn).
/// </summary>
public sealed class CarModeWarmupTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage>? Responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Paths) Paths.Add(request.RequestUri!.AbsolutePath);
            var resp = Responder?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK);
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public async Task WarmAsync_WarmsBothModelAndTextToSpeech()
    {
        var handler = new RecordingHandler();
        var warmup = new CarModeWarmup(
            _ => ("https://api.test/v1", "test-model", "dt_live_key"),
            _ => ("https://api.test/v1", "af_bella", "tts-model", "dt_live_key"),
            new HttpClient(handler), _ => { });

        await warmup.WarmAsync(TenantId.Local, CancellationToken.None);

        Assert.Contains("/v1/chat/completions", handler.Paths);
        Assert.Contains("/v1/audio/speech", handler.Paths);
    }

    [Fact]
    public async Task WarmAsync_NoModelKey_SkipsModelLeg_ButStillWarmsTextToSpeech()
    {
        var handler = new RecordingHandler();
        var warmup = new CarModeWarmup(
            _ => ("https://api.test/v1", "test-model", ""),          // no model key
            _ => ("https://api.test/v1", "af_bella", "tts-model", "dt_live_key"),
            new HttpClient(handler), _ => { });

        await warmup.WarmAsync(TenantId.Local, CancellationToken.None);

        Assert.DoesNotContain("/v1/chat/completions", handler.Paths);
        Assert.Contains("/v1/audio/speech", handler.Paths);
    }

    [Fact]
    public async Task WarmAsync_NeverThrows_WhenUpstreamFails()
    {
        var handler = new RecordingHandler { Responder = _ => throw new HttpRequestException("upstream down") };
        var warmup = new CarModeWarmup(
            _ => ("https://api.test/v1", "m", "k"),
            _ => ("https://api.test/v1", "v", "tm", "k"),
            new HttpClient(handler), _ => { });

        // Best-effort: a warmup failure must be swallowed, never thrown into the caller.
        await warmup.WarmAsync(TenantId.Local, CancellationToken.None);
    }
}
