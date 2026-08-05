using System.Text.Json;

namespace CcDirector.Core.HostedAi;

/// <summary>
/// Maps a hosted-service HTTP 402 into the shared <see cref="HostedAiState"/> (issue #938, epic #937).
/// This is the runtime half of the gate: when a feature runs dry mid-use, the 402 the proxy returns is
/// normalized here so the user sees the SAME message as the pre-flight check
/// (<see cref="HostedAiReadiness"/>), never "transcription failed: ...402..." or a silent nothing.
///
/// Branch on the machine-readable <c>code</c>, NOT the <c>type</c>: the website proxy returns the same
/// <c>type</c> (<c>insufficient_quota</c>) for more than one condition, so only the <c>code</c> tells
/// them apart (server contract in epic #937; the two Included AI codes added by issue #1360):
/// <list type="bullet">
/// <item><c>insufficient_credits</c> -> <see cref="HostedAiState.NeedsCredits"/></item>
/// <item><c>monthly_limit_reached</c> -> <see cref="HostedAiState.CapReached"/></item>
/// <item><c>subscription_required</c> -> <see cref="HostedAiState.SubscriptionRequired"/></item>
/// <item><c>fair_use_limit_reached</c> -> <see cref="HostedAiState.FairUseLimitReached"/></item>
/// <item>anything else -> <see cref="HostedAiState.Unavailable"/> - an UNKNOWN code must never claim
/// the account is out of credits (issue #1360; the old default was NeedsCredits, which would show an
/// "add credits" prompt to members the owner ruled must never see a cost)</item>
/// </list>
/// </summary>
public static class HostedAiErrorMapper
{
    /// <summary>The proxy code for an empty account balance (the direct-API credits path).</summary>
    public const string InsufficientCreditsCode = "insufficient_credits";

    /// <summary>The proxy code for a reached monthly spending limit (the credits spend cap).</summary>
    public const string MonthlyLimitReachedCode = "monthly_limit_reached";

    /// <summary>The proxy code for a member with no live entitlement on an included service (issue #1360).</summary>
    public const string SubscriptionRequiredCode = "subscription_required";

    /// <summary>The proxy code for a used-up monthly fair-use allowance on the included services (issue #1360).</summary>
    public const string FairUseLimitReachedCode = "fair_use_limit_reached";

    /// <summary>
    /// Map a 402 error <paramref name="code"/> to a state. The four known codes map to their states;
    /// everything else - an unknown code, or null when the body carried none at all - maps to the
    /// NEUTRAL <see cref="HostedAiState.Unavailable"/>. A null/absent code is genuinely unknown, and
    /// guessing "out of credits" on it would put a top-up prompt in front of members who must never
    /// see one; the direct-API credits path always names its code, so nothing is lost by not guessing.
    /// </summary>
    public static HostedAiState MapCode(string? code) => (code?.Trim().ToLowerInvariant()) switch
    {
        InsufficientCreditsCode => HostedAiState.NeedsCredits,
        MonthlyLimitReachedCode => HostedAiState.CapReached,
        SubscriptionRequiredCode => HostedAiState.SubscriptionRequired,
        FairUseLimitReachedCode => HostedAiState.FairUseLimitReached,
        _ => HostedAiState.Unavailable,
    };

    /// <summary>The sentinel for a 402 body that carried NO machine code (or was not JSON). Maps to the
    /// neutral <see cref="HostedAiState.Unavailable"/> - never assumed to mean "out of credits"
    /// (issue #1360: the old default was <see cref="InsufficientCreditsCode"/>, which put an add-credits
    /// prompt in front of members who must never see a cost).</summary>
    public const string UnknownCode = "unknown";

    /// <summary>
    /// Best-effort read of the machine-readable error code from a provider-compatible 402 body
    /// (<c>{ "error": { "code": "insufficient_credits" } }</c> or a flat <c>{ "code": ... }</c>).
    /// Returns <see cref="UnknownCode"/> when the body carries no code - a genuinely unknown condition,
    /// deliberately NOT collapsed into "out of credits" (issue #1360). This is the single shared parse
    /// the transcription pipeline also uses.
    /// </summary>
    public static string ParseErrorCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return UnknownCode;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("code", out var nested) && nested.ValueKind == JsonValueKind.String)
                return nested.GetString() ?? UnknownCode;
            if (root.TryGetProperty("code", out var flat) && flat.ValueKind == JsonValueKind.String)
                return flat.GetString() ?? UnknownCode;
        }
        catch (JsonException)
        {
            // A non-JSON 402 body is a money-shaped refusal with no readable reason: unknown, never
            // assumed to be out of credits.
        }
        return UnknownCode;
    }

    /// <summary>Parse the code out of a 402 body and map it to a state in one call.</summary>
    public static HostedAiState Map402(string? body) => MapCode(ParseErrorCode(body));
}
