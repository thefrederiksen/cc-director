using CcDirector.Core.HostedAi;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.HostedAi;

/// <summary>
/// The single place that turns a shared <see cref="HostedAiState"/> into its HTTP form at the Gateway
/// boundary (issue #939, epic #937): the wire DTO stamped onto <c>SessionDto.VoiceUnavailable</c> and
/// the HTTP 402 response the paid endpoints return when a call runs dry. Both draw the copy from the
/// one source (<see cref="HostedAiMessages"/>), so every surface shows the identical message by
/// construction. The <c>error</c> field mirrors the shared text so a client that still only reads
/// <c>error</c> shows the correct copy even before it is taught the richer shape.
/// </summary>
public static class HostedAiHttp
{
    /// <summary>HTTP 402 Payment Required - the status the hosted proxy uses for out-of-credits / cap.</summary>
    public const int PaymentRequired = 402;

    /// <summary>Build the wire DTO for a state (used to stamp <c>SessionDto.VoiceUnavailable</c>).</summary>
    public static HostedAiMessageDto Dto(HostedAiState state)
    {
        var m = HostedAiMessages.For(state);
        return new HostedAiMessageDto
        {
            State = state.ToString(),
            Text = m.Text,
            CtaLabel = m.CtaLabel,
            CtaAction = m.CtaAction.ToString(),
            CtaUrl = m.CtaUrl,
        };
    }

    /// <summary>The HTTP 402 response for a state, carrying the shared message + call-to-action. The
    /// wire shape is the single-source <see cref="HostedAiPayload"/> (issue #941), so the Gateway and the
    /// Director Control API return the identical body.</summary>
    public static IResult PaymentRequiredResult(HostedAiState state)
        => Results.Json(HostedAiPayload.For(state), statusCode: PaymentRequired);
}
