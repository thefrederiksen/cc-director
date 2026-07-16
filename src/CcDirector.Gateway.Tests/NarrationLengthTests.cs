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

    /// <summary>The flat deadline this replaced. The derived one must never be tighter than it, at any
    /// length - deriving a number is only an improvement if it cannot be worse than the constant.</summary>
    private static readonly TimeSpan OldFlatDeadline = TimeSpan.FromSeconds(15);

    [Theory]
    // input, measured synthesis seconds direct to the provider (2026-07-15, production key).
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

    [Theory]
    // THE REGRESSION GUARD, and the one test here that was written from a real outage.
    //
    // The deadline shipped as 5s + 4ms/char. It looked right - it cleared every measured synthesis
    // time with >2x headroom - and it was TIGHTER than the flat 15s it replaced for anything under
    // ~2,500 chars, which is almost every real narration. Minutes after deploy, on the owner's
    // Gateway:
    //   [TtsSynthesis] attempt 1/2 timed out after 5s (20 chars); retrying
    //   [TtsSynthesis] attempt 2/2 timed out after 5s (20 chars); giving up
    //
    // The theory above could not catch it, because it only ever asked "is there room for the
    // SYNTHESIS?" - and synthesis was never the problem. The same 47-char call measured 0.7s, 1.3s
    // and 13.3s on one day with 72ms of GPU: the fixed overhead is large and VARIABLE, and it hits a
    // 20-char call as hard as a 4,000-char one. So this asks the other question, the one that
    // actually failed: is it ever WORSE than what we replaced?
    [InlineData(0)]
    [InlineData(20)]        // the exact length that died in production
    [InlineData(47)]        // the call measured at 0.7s / 1.3s / 13.3s
    [InlineData(479)]
    [InlineData(550)]
    [InlineData(1_292)]     // today's median narration
    [InlineData(2_500)]
    [InlineData(NarrationText.MaxChars)]
    public void DeadlineFor_IsNeverTighterThanTheFlatDeadlineItReplaced(int chars)
    {
        var deadline = TtsSynthesis.DeadlineFor(chars);
        Assert.True(deadline >= OldFlatDeadline,
            $"{chars} chars: deadline {deadline.TotalSeconds:F1}s is TIGHTER than the flat "
            + $"{OldFlatDeadline.TotalSeconds:F0}s it replaced - that is a regression, not a derivation");
    }

    [Fact]
    public void DeadlineFor_LeavesRoomForTheOverheadOutlier_NotJustSynthesis()
    {
        // The 47-char call that took 13.3s of wall time for 72ms of GPU. A deadline that only budgets
        // for synthesis kills this call every time. Whatever the formula becomes, a short narration
        // must survive the worst overhead we have actually observed.
        //
        // 13.3 was the worst we had SEEN, not the worst there is, and this test passed at 15.2s while
        // the fleet went silent - the constant had rotted. Re-measured 2026-07-15 against a genuinely
        // cold provider: 16.9s, HTTP 200, real audio. So the old base of 15 failed this test's own
        // premise; it just did not know it yet. Raised to what was actually observed.
        const double worstObservedOverheadSeconds = 39.9;
        Assert.True(TtsSynthesis.DeadlineFor(47).TotalSeconds > worstObservedOverheadSeconds,
            "a short call must outlive the worst fixed overhead we have measured, not just its own synthesis");
    }

    [Fact]
    public void DeadlineFor_ClearsAColdStart_AtEveryLength()
    {
        // THE DEFECT, 2026-07-15. The provider scales the speech model down when idle, so the first
        // call after a quiet spell pays the model load. Measured direct, 720 chars, cold then warm:
        //   COLD: 16.9s 12.4s 11.3s    WARM: 1.8s 1.9s 3.8s   (all HTTP 200, all real audio)
        //
        // This is not a slow-provider inconvenience, it is a trap: a timeout arms the FLEET-WIDE speech
        // cooldown for 120s, and 120s of nobody calling is precisely how the provider goes cold again.
        // The cooldown manufactures the cold start that causes the next timeout. The fleet sat at 0/8
        // sessions with audio, all reporting ServiceDown, while the service answered every hand-made
        // call perfectly - three warm-up calls took it to 6/8 with no code change.
        //
        // A cold start must therefore cost a SLOW narration, never a failed one - at any length, since
        // the cold start does not scale with the text. The old base of 15 gave a 47-char narration a
        // 15.2s deadline against a 16.9s cold start: it could not succeed at all.
        // 16.9 was the worst seen when this test was written. HOURS later the same provider took
        // 39.9s for a SIXTEEN character call, and the live log showed a 168-char narration timing out
        // at 31s. Twice this constant has been set just above the worst-so-far and twice the provider
        // has gone slower. Pinned to the real worst; the deadline must clear it at EVERY length,
        // because a cold start does not scale with the text.
        const double observedColdStartSeconds = 39.9;
        foreach (var chars in new[] { 20, 47, 469, 720, 1292, 4000, NarrationText.MaxChars })
        {
            Assert.True(TtsSynthesis.DeadlineFor(chars).TotalSeconds > observedColdStartSeconds,
                $"a {chars}-char narration must survive a cold start ({observedColdStartSeconds}s observed), " +
                $"but its deadline is only {TtsSynthesis.DeadlineFor(chars).TotalSeconds:F1}s - a cold provider " +
                "would time out, arm the fleet-wide cooldown, and guarantee the next call is cold too");
        }
    }

    [Fact]
    public void DeadlineFor_ScalesWithTheText_SoItCannotRotWhenTheCapMoves()
    {
        // The point of the change: the deadline is DERIVED, not picked. A longer text gets more time,
        // automatically. The old flat 15s was only ever right for the 4000-char world it was born in.
        var shortDeadline = TtsSynthesis.DeadlineFor(550);
        var longDeadline = TtsSynthesis.DeadlineFor(NarrationText.MaxChars);

        Assert.True(longDeadline > shortDeadline);
    }

    [Fact]
    public void DeadlineFor_AtTheRunawayCeiling_StaysBounded()
    {
        // The cap is what keeps the derived deadline finite: cap and deadline are ONE decision. If
        // someone raises MaxChars without thinking about what it does to the worst-case wait, they
        // will see it here. The ceiling only binds for a runaway that should never occur - a real
        // narration is ~550 chars - but it must stay a bounded wait rather than an open-ended one.
        //
        // Upper bound raised 90 -> 130 when the base went 30 -> 60 to clear a 39.9s cold start. This is
        // a deliberate re-justification, not a widen-until-green: what this bound protects changed. A
        // long deadline used to hold the fleet-wide gate's fate - one slow call could silence everyone -
        // so 90 was rightly stingy. A timeout now costs ONLY its own session (WingmanVoiceService.TtsAsync
        // no longer arms the shared gate on a timeout), so the worst case here is one runaway narration
        // occupying one of two slots for 108s. That is a bounded wait, which is all this test is for.
        var atCap = TtsSynthesis.DeadlineFor(NarrationText.MaxChars);
        Assert.InRange(atCap.TotalSeconds, 30, 130);
    }

    [Fact]
    public void DeadlineFor_IsNeverNegative_ForDegenerateInput()
    {
        Assert.True(TtsSynthesis.DeadlineFor(0) > TimeSpan.Zero);
        Assert.True(TtsSynthesis.DeadlineFor(-1) > TimeSpan.Zero);
    }
}
