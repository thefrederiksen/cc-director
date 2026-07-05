using System.Text.Json;

namespace CcDirector.Core.HostedAi;

/// <summary>
/// Maps a hosted-service HTTP 402 into the shared <see cref="HostedAiState"/> (issue #938, epic #937).
/// This is the runtime half of the gate: when a feature runs dry mid-use, the 402 the proxy returns is
/// normalized here so the user sees the SAME message as the pre-flight check
/// (<see cref="HostedAiReadiness"/>), never "transcription failed: ...402..." or a silent nothing.
///
/// Branch on the machine-readable <c>code</c>, NOT the <c>type</c>: the website proxy returns the same
/// <c>type</c> (<c>insufficient_quota</c>) for both conditions, so only the <c>code</c> distinguishes an
/// empty balance from a hit monthly cap (server contract in epic #937):
/// <list type="bullet">
/// <item><c>insufficient_credits</c> -> <see cref="HostedAiState.NeedsCredits"/></item>
/// <item><c>monthly_limit_reached</c> -> <see cref="HostedAiState.CapReached"/></item>
/// </list>
/// </summary>
public static class HostedAiErrorMapper
{
    /// <summary>The proxy code for an empty account balance.</summary>
    public const string InsufficientCreditsCode = "insufficient_credits";

    /// <summary>The proxy code for a reached monthly spending limit.</summary>
    public const string MonthlyLimitReachedCode = "monthly_limit_reached";

    /// <summary>
    /// Map a 402 error <paramref name="code"/> to a state. <c>monthly_limit_reached</c> is the cap;
    /// every other value (including <c>insufficient_credits</c>, an unknown code, or null) maps to
    /// <see cref="HostedAiState.NeedsCredits"/>, because this is only ever consulted for a 402 and an
    /// empty balance is the common case (matching the existing transcription behavior).
    /// </summary>
    public static HostedAiState MapCode(string? code) => (code?.Trim().ToLowerInvariant()) switch
    {
        MonthlyLimitReachedCode => HostedAiState.CapReached,
        _ => HostedAiState.NeedsCredits,
    };

    /// <summary>
    /// Best-effort read of the machine-readable error code from an OpenAI-compatible 402 body
    /// (<c>{ "error": { "code": "insufficient_credits" } }</c> or a flat <c>{ "code": ... }</c>).
    /// Defaults to <see cref="InsufficientCreditsCode"/> when the body carries no code, since the caller
    /// only reaches here on a 402. This is the single shared parse the transcription pipeline also uses.
    /// </summary>
    public static string ParseErrorCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return InsufficientCreditsCode;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("code", out var nested) && nested.ValueKind == JsonValueKind.String)
                return nested.GetString() ?? InsufficientCreditsCode;
            if (root.TryGetProperty("code", out var flat) && flat.ValueKind == JsonValueKind.String)
                return flat.GetString() ?? InsufficientCreditsCode;
        }
        catch (JsonException)
        {
            // A non-JSON 402 body still means out of credits; fall through to the default code.
        }
        return InsufficientCreditsCode;
    }

    /// <summary>Parse the code out of a 402 body and map it to a state in one call.</summary>
    public static HostedAiState Map402(string? body) => MapCode(ParseErrorCode(body));
}
