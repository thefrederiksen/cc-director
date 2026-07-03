using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// The wingman as a STATELESS, hosted chat-completions call (the account-first hosted-AI direction).
/// Unlike the warm <c>claude.exe</c> brain, this makes one OpenAI-compatible
/// <c>POST {base}/chat/completions</c> per ask - to the DevThrottle inference proxy
/// (<c>https://devthrottle.com/api/v1</c>, model <c>glm-5.2</c>) or to OpenAI directly
/// (model <c>gpt-5.5</c>), depending on the selected AI provider. The base URL, credential name, and
/// model all come from the one routing spot
/// (<see cref="Core.Configuration.TranscriptionEndpointResolver.ResolveWingman"/>).
///
/// It implements <see cref="IAgentBrain"/> so <see cref="WingmanTranslator"/> - which only ever calls
/// <see cref="AskAsync"/> then <see cref="ClearAsync"/> - drives it unchanged. There is no process and
/// no conversation state, so clear/cancel/restart/kill are no-ops and each ask stands alone (which is
/// exactly what the translator wants: it clears context between every translation anyway).
///
/// The credential is Bearer-presented and NEVER logged (security rule DT-05): this class logs only the
/// outcome (status code, byte counts), never the key or the prompt/reply text.
/// </summary>
public sealed class HostedInferenceBrain : IAgentBrain
{
    /// <summary>Shared client with a generous timeout - a wingman summary is one model round trip and
    /// the caller (voice turn) already bounds the overall wait.</summary>
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(3) };

    private readonly HttpClient _http;
    private readonly string _chatUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly Action<string> _log;

    /// <param name="baseUrl">The provider's OpenAI-compatible <c>/v1</c> base URL.</param>
    /// <param name="apiKey">The credential to present as the Bearer token. Must be non-empty.</param>
    /// <param name="model">The chat model id (e.g. <c>glm-5.2</c> or <c>gpt-5.5</c>).</param>
    /// <param name="http">HTTP client (tests inject a stub over a fake handler); a shared 3-minute client when null.</param>
    /// <param name="log">Log sink; <see cref="FileLog.Write"/> when null.</param>
    public HostedInferenceBrain(string baseUrl, string apiKey, string model, HttpClient? http = null, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("baseUrl is required", nameof(baseUrl));
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("model is required", nameof(model));
        _http = http ?? SharedHttp;
        _chatUrl = baseUrl.TrimEnd('/') + "/chat/completions";
        _apiKey = apiKey ?? "";
        _model = model.Trim();
        _log = log ?? FileLog.Write;
    }

    /// <summary>Stateless - there is no agent-internal session.</summary>
    public string? SessionId => null;

    /// <summary>
    /// One chat-completions round trip: POST the prompt as a single user message and return the
    /// assistant's text. A missing credential or a non-success response throws with the fix named
    /// (no-fallback rule) - the caller surfaces it rather than speaking a wrong or empty summary.
    /// </summary>
    public async Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException(
                "[HostedInferenceBrain] No API key for the selected AI provider. Sign in to DevThrottle " +
                "(or add your OpenAI key) so the wingman can reach the model.");

        var payload = JsonSerializer.Serialize(new
        {
            model = _model,
            messages = new[] { new { role = "user", content = prompt } },
            stream = false,
        });

        var sw = Stopwatch.StartNew();
        using var req = new HttpRequestMessage(HttpMethod.Post, _chatUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
        {
            _log($"[HostedInferenceBrain] chat/completions model={_model} -> {(int)resp.StatusCode} ({text.Length} bytes)");
            throw new InvalidOperationException(
                $"The wingman model call failed: {(int)resp.StatusCode} {resp.StatusCode}. " +
                (resp.StatusCode == System.Net.HttpStatusCode.PaymentRequired
                    ? "Your DevThrottle account is out of credits - add credits to keep hosted AI working."
                    : "Check the AI provider settings and that the account/key is valid."));
        }

        var content = ExtractContent(text);
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException(
                "[HostedInferenceBrain] The model returned an empty message for a non-empty prompt.");

        _log($"[HostedInferenceBrain] chat/completions model={_model} OK: {content.Length} chars in {sw.Elapsed.TotalSeconds:F1}s");
        return new AskResult { Text = content, ReplySeconds = sw.Elapsed.TotalSeconds };
    }

    /// <summary>
    /// Pull the assistant message text out of an OpenAI-compatible chat-completions response
    /// (<c>choices[0].message.content</c>). Internal so a test can assert the parse. Returns "" when
    /// the shape is unexpected (the caller treats an empty result as a failure).
    /// </summary>
    internal static string ExtractContent(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var message)
                    && message.TryGetProperty("content", out var contentEl)
                    && contentEl.ValueKind == JsonValueKind.String)
                {
                    return contentEl.GetString() ?? "";
                }
            }
        }
        catch (JsonException)
        {
            // An unparseable body is treated as an empty result by the caller (no-fallback: it throws).
        }
        return "";
    }

    // --- Stateless: no process, no conversation to reset or recover. ---

    /// <summary>No-op: there is no running turn to abort.</summary>
    public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>No-op: each ask is independent, so there is no context to clear.</summary>
    public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());

    /// <summary>No-op: there is no process to restart.</summary>
    public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>No-op: there is no process to kill.</summary>
    public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Alive when a credential is configured; nothing to spawn.</summary>
    public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default)
    {
        var hasKey = !string.IsNullOrWhiteSpace(_apiKey);
        return Task.FromResult(new BrainHealth
        {
            IsAlive = hasKey,
            Status = hasKey ? "Running" : "NotStarted",
            ActivityState = hasKey ? "Quiet" : "NotStarted",
        });
    }

    /// <summary>Nothing owned to dispose (the HTTP client is shared).</summary>
    public void Dispose() { }
}
