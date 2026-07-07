using System.Text.Json;
using CcDirector.Core.HostedAi;
using Xunit;

namespace CcDirector.Core.Tests.HostedAi;

/// <summary>
/// Issue #941 (epic #937): the shared server-boundary wire shape. Every web/native client keys off
/// these exact field names, and the <c>error</c> field must mirror the shared text so a raw-display
/// client shows the right copy. Proves the payload carries the single-source copy and serializes to the
/// pinned camelCase names regardless of a serializer's casing policy.
/// </summary>
public sealed class HostedAiPayloadTests
{
    [Theory]
    [InlineData(HostedAiState.NeedsCredits, "NeedsCredits", "OpenBilling")]
    [InlineData(HostedAiState.CapReached, "CapReached", "OpenBilling")]
    public void For_CarriesSharedCopy_AndErrorMirrorsText(HostedAiState state, string expectedState, string expectedAction)
    {
        var p = HostedAiPayload.For(state);
        var m = HostedAiMessages.For(state);

        Assert.Equal(expectedState, p.State);
        Assert.Equal(expectedAction, p.CtaAction);
        Assert.Equal(m.Text, p.Text);
        Assert.Equal(m.Text, p.Error);      // error mirrors text so raw-display clients show the right copy
        Assert.Equal(m.CtaLabel, p.CtaLabel);
        Assert.Equal(m.CtaUrl, p.CtaUrl);
    }

    [Fact]
    public void Serializes_WithPinnedCamelCaseNames()
    {
        // Default options (no camelCase policy): the [JsonPropertyName] pins must still produce the
        // exact wire names the clients read.
        var json = JsonSerializer.Serialize(HostedAiPayload.For(HostedAiState.NeedsCredits));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("error", out _));
        Assert.True(root.TryGetProperty("state", out _));
        Assert.True(root.TryGetProperty("text", out _));
        Assert.True(root.TryGetProperty("ctaLabel", out _));
        Assert.True(root.TryGetProperty("ctaAction", out _));
        Assert.True(root.TryGetProperty("ctaUrl", out _));
    }

    [Fact]
    public void FromBody_BranchesOnCode()
    {
        Assert.Equal("CapReached", HostedAiPayload.FromBody("{\"error\":{\"code\":\"monthly_limit_reached\"}}").State);
        Assert.Equal("NeedsCredits", HostedAiPayload.FromBody("{\"error\":{\"code\":\"insufficient_credits\"}}").State);
        Assert.Equal("NeedsCredits", HostedAiPayload.FromBody("not json").State); // default
    }
}
