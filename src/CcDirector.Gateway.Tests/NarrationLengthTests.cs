using CcDirector.Gateway.HostedAi;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1612. Narration was silently cut at 4000 characters to satisfy OpenAI's /audio/speech limit
/// - months after we stopped calling OpenAI. It happened 24 times in one day (of 873): the worst lost
/// 969 characters, about 55 seconds of speech, dropped mid-word with the user told nothing.
///
/// These pin the two mechanisms the fix rests on. The third and most important part - the wingman
/// actually summarising - is pinned in WingmanTranslatorTests, because that is a prompt contract.
/// </summary>
public class NarrationLengthTests
{
    // ---- The cut is the lie, not the limit ---------------------------------

    [Fact]
    public void LimitForSpeech_NormalNarration_PassesThroughUntouched()
    {
        // A real narration (~30 seconds spoken) must never be touched. If this ever trips, the guard
        // has become a product limit, which is the bug it replaced.
        var text = new string('a', 550);
        var result = NarrationText.LimitForSpeech(text, out var wasCut);
        Assert.False(wasCut);
        Assert.Equal(text, result);
    }

    [Fact]
    public void LimitForSpeech_TheWorstEverObserved_IsNoLongerCut()
    {
        // 4,969 chars: the worst real case on 2026-07-15, which lost ~55 seconds under the old 4000
        // cap. It is well inside the guard now, so it is spoken in full.
        var result = NarrationText.LimitForSpeech(new string('a', 4969), out var wasCut);
        Assert.False(wasCut);
        Assert.Equal(4969, result.Length);
    }

    [Fact]
    public void LimitForSpeech_RunawayText_IsCutButSaysSo()
    {
        // The guard fires only for a runaway. When it does, the listener MUST hear that they are
        // missing something - being cut off mid-word in silence is the defect.
        var text = string.Join(" ", Enumerable.Repeat("word", 5000));   // ~25,000 chars
        var result = NarrationText.LimitForSpeech(text, out var wasCut);

        Assert.True(wasCut);
        Assert.Contains("as much as I can read out", result);
        Assert.Contains("open the session to read the full reply", result);
        Assert.True(result.Length <= NarrationText.MaxChars,
            $"the result must stay within the ceiling the deadline is derived from, was {result.Length}");
    }

    [Fact]
    public void LimitForSpeech_NeverCutsMidWord()
    {
        // The old behaviour was a bare text[..4000], which cut mid-word and made truncation sound
        // like a crash. Whatever we cut, the last spoken word must be a whole word.
        var text = string.Join(" ", Enumerable.Repeat("indistinguishable", 2000));
        var result = NarrationText.LimitForSpeech(text, out var wasCut);

        Assert.True(wasCut);
        var spokenPart = result[..result.IndexOf(" That is as much as I can read out", StringComparison.Ordinal)];
        Assert.All(spokenPart.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            w => Assert.Equal("indistinguishable", w));
    }

    [Fact]
    public void LimitForSpeech_PrefersASentenceBoundary()
    {
        // Cut at the end of a thought where we can, so the listener hears something whole before
        // being told the rest is missing.
        var text = string.Join(" ", Enumerable.Repeat("The agent changed the timeout.", 1000));
        var result = NarrationText.LimitForSpeech(text, out var wasCut);

        Assert.True(wasCut);
        var spokenPart = result[..result.IndexOf(" That is as much as I can read out", StringComparison.Ordinal)];
        Assert.EndsWith(".", spokenPart);
    }

    [Fact]
    public void LimitForSpeech_NullOrEmpty_IsSafe()
    {
        Assert.Equal("", NarrationText.LimitForSpeech(null, out var cutNull));
        Assert.False(cutNull);
        Assert.Equal("", NarrationText.LimitForSpeech("", out var cutEmpty));
        Assert.False(cutEmpty);
    }

    // ---- The cap and the deadline are ONE decision -------------------------

    [Theory]
    // input, measured synthesis seconds direct to the provider (2026-07-15, production key).
    // The deadline must clear the MEASURED time at every length. A flat 15s - the old value - fails
    // this at 8,000 and 12,000, which is exactly why raising the cap alone would have turned silent
    // truncation into silent failure.
    [InlineData(4_000, 7.3)]
    [InlineData(5_000, 11.6)]
    [InlineData(8_000, 14.0)]
    [InlineData(12_000, 21.1)]
    public void DeadlineFor_ClearsTheMeasuredSynthesisTime_WithHeadroom(int chars, double measuredSeconds)
    {
        var deadline = TtsSynthesis.DeadlineFor(chars).TotalSeconds;
        Assert.True(deadline > measuredSeconds * 2,
            $"{chars} chars: deadline {deadline:F1}s must leave >2x headroom over the measured {measuredSeconds}s");
    }

    [Fact]
    public void DeadlineFor_ScalesWithTheText_SoItCannotRotWhenTheCapMoves()
    {
        // The point of the change: the deadline is DERIVED, not picked. A longer text gets more time,
        // automatically. The old flat 15s was only ever right for the 4000-char world it was born in.
        var shortDeadline = TtsSynthesis.DeadlineFor(550);
        var longDeadline = TtsSynthesis.DeadlineFor(NarrationText.MaxChars);

        Assert.True(longDeadline > shortDeadline);
        // A normal narration must not wait on a budget sized for a runaway.
        Assert.True(shortDeadline < TimeSpan.FromSeconds(10),
            $"a ~30-second narration should get a snug deadline, got {shortDeadline.TotalSeconds:F1}s");
    }

    [Fact]
    public void DeadlineFor_AtTheRunawayCeiling_StaysBounded()
    {
        // The cap is what keeps the derived deadline finite. If someone raises MaxChars without
        // thinking about this, they will see it here.
        var atCap = TtsSynthesis.DeadlineFor(NarrationText.MaxChars);
        Assert.InRange(atCap.TotalSeconds, 30, 60);
    }

    [Fact]
    public void DeadlineFor_IsNeverNegative_ForDegenerateInput()
    {
        Assert.True(TtsSynthesis.DeadlineFor(0) > TimeSpan.Zero);
        Assert.True(TtsSynthesis.DeadlineFor(-1) > TimeSpan.Zero);
    }
}
