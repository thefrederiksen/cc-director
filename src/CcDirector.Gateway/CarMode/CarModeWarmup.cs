using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.CarMode;

/// <summary>
/// Keeps the Car Mode hosted model and text-to-speech provider WARM (Car Mode performance round). The
/// measured cold-start swing was the dominant felt latency (~9s cold vs ~1.5s warm model; ~4.7s vs ~1.3s
/// text-to-speech), so the browser fires a warmup the instant the owner taps Start and a small keep-warm
/// ping every few minutes WHILE Car Mode is open; both land here. One tiny request to each provider spins
/// its worker up so the REAL first turn a moment later reuses the hot worker.
///
/// Best-effort by design: a warmup is a courtesy, so any failure is logged and swallowed - it must NEVER
/// disrupt a turn or throw into the endpoint. Overlapping warmups (Start plus a keep-warm tick) are
/// collapsed with an in-flight guard so we never stampede the upstream. Credits are spent only while Car
/// Mode is actively open (the endpoint gates on the keep-warm config), never 24/7.
/// </summary>
public sealed class CarModeWarmup
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly Func<TenantId, (string BaseUrl, Core.Configuration.IncludedModelId Model, string Key)> _model;
    private readonly Func<TenantId, (string BaseUrl, string Voice, string Model, string Key)> _tts;
    private readonly HttpClient _http;
    private readonly Action<string> _log;

    // 0 = idle, 1 = a warmup is in flight. Collapses an overlapping Start + keep-warm tick.
    private int _inFlight;

    // The chat model resolver carries the PROVEN included type (issue #1360): the warmup presents the
    // DevThrottle deployment credential, and the phase-2 inspection showed a raw tuple handed to this
    // constructor bypassed every resolver-internal check - the type makes that construction
    // inexpressible. The text-to-speech resolver keeps plain strings: speech is included in its
    // entirety, so no id there can bill credits.
    public CarModeWarmup(
        Func<TenantId, (string BaseUrl, Core.Configuration.IncludedModelId Model, string Key)> modelResolver,
        Func<TenantId, (string BaseUrl, string Voice, string Model, string Key)> ttsResolver,
        HttpClient? http = null,
        Action<string>? log = null)
    {
        _model = modelResolver ?? throw new ArgumentNullException(nameof(modelResolver));
        _tts = ttsResolver ?? throw new ArgumentNullException(nameof(ttsResolver));
        _http = http ?? SharedHttp;
        _log = log ?? FileLog.Write;
    }

    /// <summary>Warm the model and text-to-speech provider in parallel, once. A warmup already in flight is
    ///  skipped (not queued). Never throws - both legs swallow and log their own failures.</summary>
    public async Task WarmAsync(TenantId tenant, CancellationToken ct)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("Car Mode warmup requires an explicit tenant.", nameof(tenant));
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            _log("[CarModeWarmup] skip: a warmup is already in flight");
            return;
        }
        try
        {
            var sw = Stopwatch.StartNew();
            await Task.WhenAll(WarmModelAsync(tenant, ct), WarmTtsAsync(tenant, ct));
            _log($"[CarModeWarmup] warmed model + text-to-speech in {sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    // A one-token chat completion so the hosted model worker spins up. Best-effort: logged and swallowed.
    private async Task WarmModelAsync(TenantId tenant, CancellationToken ct)
    {
        try
        {
            var (baseUrl, model, key) = _model(tenant);
            if (string.IsNullOrWhiteSpace(key))
            {
                _log("[CarModeWarmup] model warm skipped: no key configured");
                return;
            }
            var body = $"{{\"model\":{JsonSerializer.Serialize(model.Value)},\"messages\":[{{\"role\":\"user\",\"content\":\"ping\"}}],\"max_tokens\":1,\"stream\":false}}";
            var url = baseUrl.TrimEnd('/') + "/chat/completions";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            _log($"[CarModeWarmup] model {model} -> {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            _log($"[CarModeWarmup] model warm failed (ignored): {ex.Message}");
        }
    }

    // A tiny synthesis so the text-to-speech worker spins up. Best-effort: logged and swallowed.
    private async Task WarmTtsAsync(TenantId tenant, CancellationToken ct)
    {
        try
        {
            var (baseUrl, voice, model, key) = _tts(tenant);
            if (string.IsNullOrWhiteSpace(key))
            {
                _log("[CarModeWarmup] text-to-speech warm skipped: no key configured");
                return;
            }
            var payload = new { model, voice, input = "ok", response_format = "mp3" };
            var url = baseUrl.TrimEnd('/') + "/audio/speech";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            _log($"[CarModeWarmup] text-to-speech {model}/{voice} -> {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            _log($"[CarModeWarmup] text-to-speech warm failed (ignored): {ex.Message}");
        }
    }
}
