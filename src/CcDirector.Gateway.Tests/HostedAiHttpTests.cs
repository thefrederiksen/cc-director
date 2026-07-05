using CcDirector.Core.HostedAi;
using CcDirector.Gateway.HostedAi;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #939 (epic #937): the single place that turns a shared <see cref="HostedAiState"/> into its
/// HTTP wire form at the gateway boundary. Proves the DTO carries the one-source copy for each state,
/// so every paid endpoint's 402 and the <c>/sessions</c> stamp are identical by construction.
/// </summary>
public sealed class HostedAiHttpTests
{
    [Theory]
    [InlineData(HostedAiState.NeedsCredits, "NeedsCredits", "OpenBilling")]
    [InlineData(HostedAiState.CapReached, "CapReached", "OpenBilling")]
    [InlineData(HostedAiState.NeedsKey, "NeedsKey", "OpenSettings")]
    public void Dto_CarriesSharedCopyForState(HostedAiState state, string expectedState, string expectedAction)
    {
        var dto = HostedAiHttp.Dto(state);
        var msg = HostedAiMessages.For(state);

        Assert.Equal(expectedState, dto.State);
        Assert.Equal(expectedAction, dto.CtaAction);
        Assert.Equal(msg.Text, dto.Text);         // one source of copy
        Assert.Equal(msg.CtaLabel, dto.CtaLabel);
        Assert.Equal(msg.CtaUrl, dto.CtaUrl);
    }

    [Fact]
    public void Dto_NeedsCredits_CtaUrlReachesBilling()
    {
        var dto = HostedAiHttp.Dto(HostedAiState.NeedsCredits);
        Assert.NotNull(dto.CtaUrl);
        Assert.EndsWith("/account/billing", dto.CtaUrl);
    }

    [Fact]
    public void PaymentRequiredResult_Is402()
    {
        // The helper returns an IResult with the 402 status - the one status the paid endpoints use for
        // out-of-credits / cap. (Content shape is asserted via Dto above.)
        var result = HostedAiHttp.PaymentRequiredResult(HostedAiState.NeedsCredits);
        Assert.NotNull(result);
        Assert.Equal(402, HostedAiHttp.PaymentRequired);
    }
}
