using CcDirector.Core.HostedAi;
using Xunit;

namespace CcDirector.Core.Tests.HostedAi;

/// <summary>
/// Issue #938 (epic #937): the single-source copy. Each state maps to the one correct
/// message + call-to-action, the hosted-account copy passes the forbidden-language rules (no provider
/// names, no "free credits", no subscription/tier words), and the billing call-to-action carries the
/// real Billing URL so the CTA reaches <c>/account/billing</c> on every surface.
/// </summary>
public sealed class HostedAiMessagesTests
{
    [Fact]
    public void Ready_HasNoMessageOrCta()
    {
        var m = HostedAiMessages.For(HostedAiState.Ready);
        Assert.Equal("", m.Text);
        Assert.Equal("", m.CtaLabel);
        Assert.Equal(HostedAiCtaAction.None, m.CtaAction);
        Assert.Null(m.CtaUrl);
    }

    [Fact]
    public void NeedsCredits_AddCredits_OpensBilling()
    {
        var m = HostedAiMessages.For(HostedAiState.NeedsCredits);
        Assert.NotEmpty(m.Text);
        Assert.Equal("Add credits", m.CtaLabel);
        Assert.Equal(HostedAiCtaAction.OpenBilling, m.CtaAction);
        Assert.NotNull(m.CtaUrl);
        Assert.EndsWith("/account/billing", m.CtaUrl);
    }

    [Fact]
    public void CapReached_OpenBilling()
    {
        var m = HostedAiMessages.For(HostedAiState.CapReached);
        Assert.NotEmpty(m.Text);
        Assert.Equal("Open Billing", m.CtaLabel);
        Assert.Equal(HostedAiCtaAction.OpenBilling, m.CtaAction);
        Assert.NotNull(m.CtaUrl);
        Assert.EndsWith("/account/billing", m.CtaUrl);
    }

    [Theory]
    [InlineData(HostedAiState.NeedsCredits)]
    [InlineData(HostedAiState.CapReached)]
    public void HostedCopy_PassesForbiddenLanguageRules(HostedAiState state)
    {
        var m = HostedAiMessages.For(state);
        var violations = HostedAiCopyRules.FindViolations(m.Text);
        Assert.True(HostedAiCopyRules.IsClean(m.Text),
            $"hosted copy for {state} must name no provider / no 'free credits' / no tier words, found: {string.Join(", ", violations)}");
    }

    [Theory]
    [InlineData("Use your OpenAI key", "openai")]
    [InlineData("Get free credits now", "free credit")]
    [InlineData("Upgrade your subscription", "subscription")]
    [InlineData("Powered by Groq Whisper", "groq")]
    public void ForbiddenLanguageChecker_CatchesViolations(string text, string expectedHit)
    {
        var violations = HostedAiCopyRules.FindViolations(text);
        Assert.False(HostedAiCopyRules.IsClean(text));
        Assert.Contains(expectedHit, violations);
    }

    [Fact]
    public void ForbiddenLanguageChecker_CleanText_NoViolations()
    {
        Assert.Empty(HostedAiCopyRules.FindViolations("Voice needs credit. Add $5 to keep going."));
    }

    [Fact]
    public void For_UnknownState_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HostedAiMessages.For((HostedAiState)999));
    }
}
