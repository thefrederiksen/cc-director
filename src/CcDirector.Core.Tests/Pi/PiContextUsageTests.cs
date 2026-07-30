using CcDirector.Core.Drivers;
using CcDirector.Core.Pi;
using Xunit;

namespace CcDirector.Core.Tests.Pi;

// =====================================================================================
// PiContextUsage: the gauge reads the LAST assistant message's usage.input from a pi
// session file. pi does not record the window anywhere in that file, and since issue #1100
// the driver no longer invents one from the model id - it reports the used tokens and no
// denominator until pi is actually asked.
// =====================================================================================
public sealed class PiContextUsageTests
{
    private const string Assistant1 =
        "{\"type\":\"message\",\"timestamp\":\"2026-06-27T03:00:00.000Z\",\"message\":{\"role\":\"assistant\",\"provider\":\"openai-codex\",\"model\":\"gpt-5.5\",\"usage\":{\"input\":2000,\"output\":33,\"totalTokens\":2033}}}";

    private const string Assistant2 =
        "{\"type\":\"message\",\"timestamp\":\"2026-06-27T03:05:00.000Z\",\"message\":{\"role\":\"assistant\",\"provider\":\"openai-codex\",\"model\":\"gpt-5.5\",\"usage\":{\"input\":3876,\"output\":40,\"totalTokens\":3916}}}";

    [Fact]
    public void Compute_TakesLastAssistantUsage()
    {
        var ctx = PiContextUsage.Compute(new[] { Assistant1, Assistant2 });

        Assert.NotNull(ctx);
        Assert.Equal(3876, ctx.UsedTokens);                 // the LATEST assistant message wins
        Assert.Equal(new DateTime(2026, 6, 27, 3, 5, 0, DateTimeKind.Utc), ctx.AsOfUtc);

        // The window used to be asserted as 272,000 here. See Compute_NeverDerivesAWindowFromTheModelId
        // below for why that number is gone (issue #1100); what this test owns is which MESSAGE wins and
        // that the measured fields are read correctly, not where a denominator came from.
        Assert.Null(ctx.WindowTokens);
        Assert.Null(ctx.PercentUsed);
    }

    [Fact]
    public void Compute_UnmappedModel_RawNumberFallback_NoPercent()
    {
        var line =
            "{\"type\":\"message\",\"message\":{\"role\":\"assistant\",\"model\":\"some-local-model\",\"usage\":{\"input\":500}}}";
        var ctx = PiContextUsage.Compute(new[] { line });

        Assert.NotNull(ctx);
        Assert.Equal(500, ctx.UsedTokens);
        Assert.Null(ctx.WindowTokens);
        Assert.Null(ctx.PercentUsed);
    }

    [Fact]
    public void Compute_IgnoresUserMessagesAndThinking_ReturnsNullWhenNoAssistantUsage()
    {
        var lines = new[]
        {
            "{\"type\":\"session\",\"cwd\":\"C:\\\\repo\"}",
            "{\"type\":\"message\",\"message\":{\"role\":\"user\",\"content\":[]}}",
            "{\"type\":\"thinking\"}",
        };
        Assert.Null(PiContextUsage.Compute(lines));
    }

    /// <summary>
    /// Issue #1100: pi reports no window either, and the table it used is gone.
    ///
    /// pi inherited the Claude bug wholesale - it delegated Claude model ids straight into the Claude
    /// table - and carried a second copy of the same pattern of its own: a hardcoded 272,000 for gpt-5.5,
    /// with a comment acknowledging it disagreed with the 258,400 the Codex backend reports for the same
    /// model. Two numbers for one window, and nothing on screen to say which was being shown.
    ///
    /// pi does have an honest route (an extension call that answers directly); wiring it is tracked
    /// separately. Until then the used tokens are reported and the denominator is not.
    /// </summary>
    [Theory]
    [InlineData("gpt-5.5")]
    [InlineData("claude-sonnet-4-5")]
    [InlineData("claude-opus-4-8[1m]")]
    [InlineData("mystery-model")]
    public void Compute_NeverDerivesAWindowFromTheModelId(string model)
    {
        var lines = new[]
        {
            "{\"type\":\"message\",\"timestamp\":\"2026-06-27T08:00:00Z\",\"message\":{\"role\":\"assistant\","
            + $"\"model\":\"{model}\",\"usage\":{{\"input\":50000}}}}}}",
        };

        var ctx = PiContextUsage.Compute(lines);

        Assert.NotNull(ctx);
        Assert.Equal(50_000, ctx.UsedTokens);
        Assert.Null(ctx.WindowTokens);
        Assert.Null(ctx.PercentUsed);
        Assert.Equal(nameof(ContextWindowSource.Unknown), ctx.WindowSource);
    }
}
