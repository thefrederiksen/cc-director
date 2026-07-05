using System.Text.Json.Serialization;

namespace CcDirector.Core.HostedAi;

/// <summary>
/// The single wire shape for a hosted-AI unavailable response, shared by every server boundary that
/// reports out-of-credits / no-key to a client (issue #941, epic #937): the Gateway 402
/// (<c>HostedAiHttp</c>), the Director Control API endpoints, and the <c>/dictate</c> WebSocket error
/// frame. Built from the one copy source (<see cref="HostedAiMessages"/>), so every web and native
/// client shows the identical message by construction.
///
/// The <see cref="Error"/> field deliberately mirrors <see cref="Text"/>: the existing web surfaces
/// display the server's <c>error</c> string raw, so putting the shared copy there makes the correct
/// message appear with no per-client change, while a client taught the richer shape can read
/// <see cref="State"/> and <see cref="CtaUrl"/> to render the call-to-action. Pure (no ASP.NET
/// dependency) so it lives in Core and both the Gateway and the Control API serialize the same shape.
/// The JSON names are pinned so the wire contract does not depend on a serializer's casing policy.
/// </summary>
public sealed record HostedAiPayload(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("ctaLabel")] string CtaLabel,
    [property: JsonPropertyName("ctaAction")] string CtaAction,
    [property: JsonPropertyName("ctaUrl")] string? CtaUrl)
{
    /// <summary>HTTP 402 Payment Required - the status the hosted proxy uses for out-of-credits / cap.</summary>
    public const int PaymentRequired = 402;

    /// <summary>Build the shared payload for a state from the single-source copy.</summary>
    public static HostedAiPayload For(HostedAiState state)
    {
        var m = HostedAiMessages.For(state);
        return new HostedAiPayload(
            Error: m.Text,
            State: state.ToString(),
            Text: m.Text,
            CtaLabel: m.CtaLabel,
            CtaAction: m.CtaAction.ToString(),
            CtaUrl: m.CtaUrl);
    }

    /// <summary>Build the shared payload directly from a proxy 402 body (branches on the machine code).</summary>
    public static HostedAiPayload FromBody(string? body) => For(HostedAiErrorMapper.Map402(body));
}
