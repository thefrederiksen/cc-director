using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// How a judge's answer is read, and what happens to every shape of answer that is not the one we
/// asked for.
///
/// This is where the July model cleanup went wrong and why it was removed. The danger was never a
/// model giving a wrong answer - it was accepting output shaped differently from the question:
/// prose, an apology, a refusal, an explanation wrapped around the JSON, leaked example text. Every
/// one of those must be indistinguishable from "no ruling", because no ruling means nothing is
/// applied and the user keeps their words.
/// </summary>
public sealed class CandidateJudgeProtocolTests
{
    private static readonly int[] Offered = { 0, 1, 2 };

    // ===== the answers we asked for ===========================================

    [Fact]
    public void AcceptsTheShapeWeAskedFor()
        => Assert.Equal(new[] { 0, 2 },
            CandidateJudgeProtocol.ParseAccepted("{\"acceptedCandidateIds\":[0,2]}", Offered));

    [Fact]
    public void AnEmptyArrayIsARuling_NotAFailure()
    {
        var accepted = CandidateJudgeProtocol.ParseAccepted("{\"acceptedCandidateIds\":[]}", Offered);

        Assert.NotNull(accepted);
        Assert.Empty(accepted);
    }

    [Fact]
    public void WhitespaceAroundTheJsonIsFine()
        => Assert.Equal(new[] { 1 },
            CandidateJudgeProtocol.ParseAccepted("  \n {\"acceptedCandidateIds\":[1]}\n ", Offered));

    /// <summary>A repeated id is a sloppy answer, not a dangerous one - the same span accepted twice
    /// still means accepted once.</summary>
    [Fact]
    public void RepeatedIdsAreCollapsed()
        => Assert.Equal(new[] { 1 },
            CandidateJudgeProtocol.ParseAccepted("{\"acceptedCandidateIds\":[1,1,1]}", Offered));

    /// <summary>An extra field cannot change the meaning of the field we read, so it is tolerated
    /// rather than treated as a failure. Pinned so a future tightening is a deliberate decision.</summary>
    [Fact]
    public void AnExtraFieldIsTolerated()
        => Assert.Equal(new[] { 0 },
            CandidateJudgeProtocol.ParseAccepted(
                "{\"acceptedCandidateIds\":[0],\"confidence\":0.9}", Offered));

    // ===== everything else fails closed =======================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I'm sorry, I can't help with that.")]
    [InlineData("Sure! Here are the ones I'd correct: 0 and 2.")]
    [InlineData("```json\n{\"acceptedCandidateIds\":[0]}\n```")]
    [InlineData("Here is the JSON: {\"acceptedCandidateIds\":[0]}")]
    [InlineData("{\"acceptedCandidateIds\":[0]} Hope that helps!")]
    [InlineData("[0,2]")]
    [InlineData("{}")]
    [InlineData("{\"accepted\":[0]}")]
    [InlineData("{\"acceptedCandidateIds\":\"0,2\"}")]
    [InlineData("{\"acceptedCandidateIds\":[\"0\"]}")]
    [InlineData("{\"acceptedCandidateIds\":[0.5]}")]
    [InlineData("{\"acceptedCandidateIds\":[null]}")]
    [InlineData("{\"acceptedCandidateIds\":null}")]
    [InlineData("{\"acceptedCandidateIds\":[[0]]}")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{\"acceptedCandidateIds\":[0],")]
    public void AnythingThatIsNotTheShapeWeAskedFor_IsNoRuling(string? reply)
        => Assert.Null(CandidateJudgeProtocol.ParseAccepted(reply, Offered));

    /// <summary>
    /// An id nobody offered voids the WHOLE answer rather than being skipped. A judge ruling on a
    /// candidate that does not exist did not understand the question, so its opinion on the ones that
    /// do exist is not worth acting on either.
    /// </summary>
    [Theory]
    [InlineData("{\"acceptedCandidateIds\":[7]}")]
    [InlineData("{\"acceptedCandidateIds\":[0,7]}")]
    [InlineData("{\"acceptedCandidateIds\":[-1]}")]
    public void AnIdThatWasNeverOffered_VoidsTheEntireRuling(string reply)
        => Assert.Null(CandidateJudgeProtocol.ParseAccepted(reply, Offered));

    [Fact]
    public void WhenNothingWasOffered_EvenAValidLookingIdIsRefused()
        => Assert.Null(CandidateJudgeProtocol.ParseAccepted(
            "{\"acceptedCandidateIds\":[0]}", Array.Empty<int>()));

    // ===== the question we send ===============================================

    [Fact]
    public void TheQuestionCarriesTheSentenceAndEveryCandidate()
    {
        var prompt = CandidateJudgeProtocol.BuildUserPrompt(
            "I am not sure about that",
            new[]
            {
                new JudgeCandidate(0, "sure", "Soren", 9),
                new JudgeCandidate(1, "about", "Avalonia", 14),
            });

        Assert.Contains("I am not sure about that", prompt);
        Assert.Contains("0: \"sure\" might be \"Soren\"", prompt);
        Assert.Contains("1: \"about\" might be \"Avalonia\"", prompt);
    }

    /// <summary>The tie-break is the whole safety posture, so it is pinned: unsure means reject.</summary>
    [Fact]
    public void TheInstructionTellsTheJudgeToRejectWhenUnsure()
    {
        Assert.Contains("REJECT", CandidateJudgeProtocol.SystemPrompt);
        Assert.Contains("acceptedCandidateIds", CandidateJudgeProtocol.SystemPrompt);
    }

    // ===== applying at an offset ==============================================

    [Fact]
    public void ApplyAt_RewritesOnlyTheSpanItWasGiven()
    {
        var (text, applied) = TranscriptEditEngine.ApplyAt(
            "sure and then sure again",
            new[] { new JudgeCandidate(0, "sure", "Soren", 0) });

        Assert.Equal("Soren and then sure again", text);
        Assert.Equal(1, applied);
    }

    /// <summary>Replacements change length, so a left-to-right pass would shift every later offset.
    /// Applying right to left is what keeps the second edit landing where it was judged.</summary>
    [Fact]
    public void ApplyAt_LaterEditsDoNotShiftEarlierOnes()
    {
        var (text, applied) = TranscriptEditEngine.ApplyAt(
            "sure and sure",
            new[]
            {
                new JudgeCandidate(0, "sure", "Soren", 0),
                new JudgeCandidate(1, "sure", "Soren", 9),
            });

        Assert.Equal("Soren and Soren", text);
        Assert.Equal(2, applied);
    }

    [Fact]
    public void ApplyAt_SkipsAnOffsetThatNoLongerMatchesTheText()
    {
        var (text, applied) = TranscriptEditEngine.ApplyAt(
            "sure and then sure again",
            new[] { new JudgeCandidate(0, "sure", "Soren", 5) });

        Assert.Equal("sure and then sure again", text);
        Assert.Equal(0, applied);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1000)]
    public void ApplyAt_SkipsAnOffsetOutsideTheText(int start)
    {
        var (text, applied) = TranscriptEditEngine.ApplyAt(
            "sure enough",
            new[] { new JudgeCandidate(0, "sure", "Soren", start) });

        Assert.Equal("sure enough", text);
        Assert.Equal(0, applied);
    }

    // ===== the candidates the matcher offers ==================================

    [Fact]
    public void ProposeCandidates_KeepsEveryOccurrenceSeparately_WithItsOwnOffset()
    {
        var dict = new DictationDictionary(
            new[] { "Tailscale" },
            new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, DictationProfile>());

        var candidates = FuzzyDictionaryMatcher.ProposeCandidates("Terascale then Terascale", dict);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(new[] { 0, 1 }, candidates.Select(c => c.Id));
        Assert.Equal(0, candidates[0].Start);
        Assert.Equal("Terascale then ".Length, candidates[1].Start);
    }

    /// <summary>The offset must actually point at the span, or ApplyAt silently skips it and judged
    /// corrections quietly stop happening.</summary>
    [Fact]
    public void ProposeCandidates_OffsetsPointAtTheSpanTheyDescribe()
    {
        var dict = new DictationDictionary(
            new[] { "Tailscale", "Soren" },
            new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, DictationProfile>());

        const string text = "make sure we ship Terascale";
        foreach (var c in FuzzyDictionaryMatcher.ProposeCandidates(text, dict))
            Assert.Equal(c.Find, text.Substring(c.Start, c.Find.Length));
    }

    /// <summary>The older whole-utterance path still collapses repeats, because its consumer rewrites
    /// every occurrence of a find anyway.</summary>
    [Fact]
    public void Propose_StillCollapsesRepeats()
    {
        var dict = new DictationDictionary(
            new[] { "Tailscale" },
            new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, DictationProfile>());

        Assert.Single(FuzzyDictionaryMatcher.Propose("Terascale then Terascale", dict));
    }
}
