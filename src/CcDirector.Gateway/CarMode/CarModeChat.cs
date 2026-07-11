using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.CarMode;

/// <summary>One function the model asked to call this step: its id (echoed back on the tool result),
/// the tool name, and the raw JSON-string arguments the model produced.</summary>
public sealed record CarModeToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>The assistant's turn from one chat-completions round: its spoken text (null/empty while it is
/// still calling tools) and the tools it wants run this step (empty on the final answer).</summary>
public sealed record CarModeAssistantTurn(string? Content, IReadOnlyList<CarModeToolCall> ToolCalls);

/// <summary>Raised when the hosted model call is refused for a money reason (HTTP 402): out of credits,
/// monthly cap reached, or no key. Carries the shared hosted-AI state so the endpoint returns the one
/// consistent add-credit / add-key message (issue #939), never a hand-written string.</summary>
public sealed class CarModeUnavailableException : Exception
{
    public HostedAiState State { get; }
    public CarModeUnavailableException(HostedAiState state, string message) : base(message) => State = state;
}

/// <summary>
/// The transport seam for the Car Mode tool-calling loop. Given the serialized messages and tool catalog,
/// it does ONE chat-completions round trip and returns the assistant's turn (content or tool calls). It is
/// an interface so the brain's loop is unit-tested with scripted turns and no network.
/// </summary>
public interface ICarModeChat
{
    Task<CarModeAssistantTurn> CompleteAsync(string messagesJson, string toolsJson, CancellationToken ct);
}

/// <summary>
/// The production <see cref="ICarModeChat"/>: a standard provider-compatible
/// <c>POST {base}/chat/completions</c> with a <c>tools</c> array and <c>tool_choice: auto</c>, against the
/// DevThrottle inference proxy. The base URL, credential, and model are resolved at CALL time (a settings
/// change is honoured on the next turn without a Gateway restart), mirroring the wingman brain. The
/// credential is Bearer-presented and NEVER logged (security rule DT-05): only outcomes are logged.
/// </summary>
public sealed class HostedCarModeChat : ICarModeChat
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(2) };

    private readonly Func<(string BaseUrl, string Model, string Key)> _resolve;
    private readonly HttpClient _http;
    private readonly Action<string> _log;

    /// <param name="resolve">Resolves the base URL, model, and credential for the current settings, read
    ///  fresh each call.</param>
    /// <param name="http">HTTP client (tests inject a stub handler); a shared 2-minute client when null.</param>
    /// <param name="log">Log sink; <see cref="FileLog.Write"/> when null.</param>
    public HostedCarModeChat(Func<(string BaseUrl, string Model, string Key)> resolve, HttpClient? http = null, Action<string>? log = null)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _http = http ?? SharedHttp;
        _log = log ?? FileLog.Write;
    }

    public async Task<CarModeAssistantTurn> CompleteAsync(string messagesJson, string toolsJson, CancellationToken ct)
    {
        var (baseUrl, model, key) = _resolve();
        if (string.IsNullOrWhiteSpace(key))
            throw new CarModeUnavailableException(HostedAiState.NeedsKey,
                "No DevThrottle account key is configured. Sign in to DevThrottle so Car Mode can reach the model.");

        // Assemble the request with the messages + tools verbatim (the brain owns their shape) so this
        // layer stays a thin transport. tool_choice is "required" (validated 2026-07-11): forcing a tool
        // call every turn is what makes a fast model choose tools RELIABLY instead of hallucinating an
        // action it never took. Conversational turns still work because the tool catalog carries an
        // explicit speak_answer tool the model calls to say anything - so "required" never traps it.
        var body = $"{{\"model\":{JsonSerializer.Serialize(model)},\"messages\":{messagesJson},\"tools\":{toolsJson},\"tool_choice\":\"required\",\"stream\":false}}";

        var url = baseUrl.TrimEnd('/') + "/chat/completions";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            _log($"[CarModeChat] chat/completions model={model} -> {(int)resp.StatusCode} ({text.Length} bytes)");
            if (resp.StatusCode == System.Net.HttpStatusCode.PaymentRequired)
            {
                var state = HostedAiErrorMapper.Map402(text);
                throw new CarModeUnavailableException(state, HostedAiMessages.For(state).Text);
            }
            throw new InvalidOperationException(
                $"The Car Mode model call failed: {(int)resp.StatusCode} {resp.StatusCode}.");
        }

        return ParseAssistantTurn(text);
    }

    /// <summary>Parse <c>choices[0].message</c> into an assistant turn: its content and any tool_calls.
    ///  Internal + static so a test asserts the parse without a network. Throws on an unusable body
    ///  (no-fallback: the caller must not proceed on a shape it could not read).</summary>
    internal static CarModeAssistantTurn ParseAssistantTurn(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            throw new InvalidOperationException("The model response had no choices.");

        var message = choices[0].GetProperty("message");
        string? content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

        var toolCalls = new List<CarModeToolCall>();
        if (message.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in tcs.EnumerateArray())
            {
                var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (!tc.TryGetProperty("function", out var fn)) continue;
                var name = fn.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var args = fn.TryGetProperty("arguments", out var argEl) ? argEl.GetString() ?? "{}" : "{}";
                if (name.Length > 0)
                    toolCalls.Add(new CarModeToolCall(id, name, string.IsNullOrWhiteSpace(args) ? "{}" : args));
            }
        }

        return new CarModeAssistantTurn(content, toolCalls);
    }

    /// <summary>
    /// Build the model resolver Car Mode uses: the DevThrottle base + vault key + the effective Car Mode
    /// model from <see cref="CarModeModelConfig"/> (the <c>CC_CARMODE_MODEL</c> env override, then the user's
    /// saved AI-Settings choice, then the Qwen2.5-72B default). Read fresh each call so a settings change is
    /// honoured on the next turn. Paired with <c>tool_choice=required</c> + <c>speak_answer</c> (see
    /// <see cref="CompleteAsync"/> and the brain's tool catalog), which is what makes a fast model choose
    /// tools reliably.
    /// </summary>
    public static Func<(string BaseUrl, string Model, string Key)> DefaultResolver(Func<string, string?> vaultGet)
    {
        return () =>
        {
            var mode = TranscriptionModeConfig.Get();
            // Same base URL + vault key as the wingman endpoint; only the model differs for Car Mode.
            var ep = TranscriptionEndpointResolver.ResolveWingman(mode);
            var key = vaultGet(ep.KeyName) ?? "";
            return (ep.BaseUrl, CarModeModelConfig.Resolve(), key);
        };
    }
}
