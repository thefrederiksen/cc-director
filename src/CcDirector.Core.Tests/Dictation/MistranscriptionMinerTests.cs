using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// Unit tests for the mistranscription MINER (devthrottle issue #2075): given a tenant's raw transcripts,
/// its current dictionary and its dismissed terms, it must surface the distinctive terms the speech model
/// keeps getting wrong - with the right canonical spelling, the right wrong spellings as evidence, the right
/// counts, and the right exclusions - and it must stay quiet when the evidence is thin. The miner is pure,
/// so every case here is exercised end to end with no I/O.
/// </summary>
public sealed class MistranscriptionMinerTests
{
    private static DictationDictionary Dict(
        IEnumerable<string>? vocab = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? mistranscriptions = null)
        => new(
            (vocab ?? Array.Empty<string>()).ToList(),
            mistranscriptions ?? new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, DictationProfile> { ["default"] = new("default", true) });

    // Build a corpus by repeating a spelling n times, one "utterance" each, so counts are explicit.
    private static IEnumerable<string> Say(params (string spelling, int times)[] spellings)
    {
        foreach (var (spelling, times) in spellings)
            for (var i = 0; i < times; i++)
                yield return $"we shipped the {spelling} change today";
    }

    private static IReadOnlyList<MistranscriptionSuggestion> Mine(
        IEnumerable<string> transcripts,
        DictationDictionary? dict = null,
        IEnumerable<string>? dismissed = null,
        MistranscriptionMiner.Options? opts = null)
        => MistranscriptionMiner.Mine(
            transcripts, dict ?? Dict(), (dismissed ?? Array.Empty<string>()).ToList(), opts);

    [Fact]
    public void Mine_TermHeardSeveralWrongWays_IsSuggestedWithCanonicalSpelling()
    {
        // "mindzie" is said 44 times right and heard wrong 53 times across four near-spellings.
        var corpus = Say(
            ("mindzie", 44), ("Mindsee", 20), ("Mindsy", 15), ("Mindzee", 12), ("Mindsea", 6)).ToList();

        var suggestions = Mine(corpus);

        var s = Assert.Single(suggestions);
        Assert.Equal("mindzie", s.Term);
        Assert.Equal(53, s.WrongCount);
        Assert.Equal(97, s.TotalCount);
        Assert.Equal(
            new[] { "Mindsee", "Mindsy", "Mindzee", "Mindsea" },
            s.Variants.Select(v => v.Heard).ToArray());
        Assert.Equal(20, s.Variants[0].Count);
    }

    [Fact]
    public void Mine_HyphenatedMishearing_ClustersAsOneTerm()
    {
        // "ConPty" heard as "Con-TY"/"ConTY" - the hyphen stays inside the token, so it is one spelling.
        var corpus = Say(("ConPty", 64), ("Con-TY", 50), ("ConTY", 30)).ToList();

        var s = Assert.Single(Mine(corpus));

        Assert.Equal("ConPty", s.Term);
        Assert.Equal(80, s.WrongCount);
        Assert.Equal(144, s.TotalCount);
        Assert.Contains(s.Variants, v => v.Heard == "Con-TY");
        Assert.Contains(s.Variants, v => v.Heard == "ConTY");
    }

    [Fact]
    public void Mine_TermAlreadyInVocabulary_IsNotSuggested()
    {
        var corpus = Say(("mindzie", 44), ("Mindsee", 20), ("Mindsy", 15), ("Mindzee", 12)).ToList();

        var suggestions = Mine(corpus, Dict(vocab: new[] { "mindzie" }));

        Assert.Empty(suggestions);
    }

    [Fact]
    public void Mine_TermAlreadyAKnownMistranscription_IsNotSuggested()
    {
        // The correct term is in the dictionary as a mistranscription key, so it is already handled.
        var corpus = Say(("mindzie", 44), ("Mindsee", 20), ("Mindsy", 15), ("Mindzee", 12)).ToList();
        var dict = Dict(mistranscriptions: new Dictionary<string, IReadOnlyList<string>>
        {
            ["mindzie"] = new[] { "Mindsee" },
        });

        Assert.Empty(Mine(corpus, dict));
    }

    [Fact]
    public void Mine_DismissedTerm_IsNotSuggested()
    {
        var corpus = Say(("mindzie", 44), ("Mindsee", 20), ("Mindsy", 15), ("Mindzee", 12)).ToList();

        Assert.Empty(Mine(corpus, dismissed: new[] { "mindzie" }));
    }

    [Fact]
    public void Mine_DismissedThenRestored_ReappearsWhenNotInDismissedSet()
    {
        var corpus = Say(("mindzie", 44), ("Mindsee", 20), ("Mindsy", 15), ("Mindzee", 12)).ToList();

        Assert.Empty(Mine(corpus, dismissed: new[] { "mindzie" }));
        Assert.Single(Mine(corpus, dismissed: Array.Empty<string>()));
    }

    [Fact]
    public void Mine_BelowWrongCountFloor_IsNotSuggested()
    {
        // Only two wrong hearings - under the default floor of three.
        var corpus = Say(("mindzie", 40), ("Mindsee", 1), ("Mindsy", 1)).ToList();

        Assert.Empty(Mine(corpus));
    }

    [Fact]
    public void Mine_BelowWrongRatio_IsNotSuggested()
    {
        // Heard wrong 3 of 100 = 3%, well under the 25% floor, even though the wrong count clears its floor.
        var corpus = Say(("mindzie", 97), ("Mindsee", 1), ("Mindsy", 1), ("Mindzee", 1)).ToList();

        Assert.Empty(Mine(corpus));
    }

    [Fact]
    public void Mine_ConsistentlySpelledWord_IsNeverSuggested()
    {
        // An ordinary word the model always spells the same way has no wrong cluster - the whole premise.
        var corpus = Enumerable.Repeat("we reviewed the dashboard carefully today", 200);

        Assert.Empty(Mine(corpus));
    }

    [Fact]
    public void Mine_ShortTerm_IsBelowLengthFloorAndSkipped()
    {
        // "cat"/"bat"/"hat" cluster but "cat" normalizes to three chars, under the MinTermChars floor.
        var corpus = Say(("cat", 40), ("bat", 20), ("hat", 20)).ToList();

        Assert.Empty(Mine(corpus));
    }

    [Fact]
    public void Mine_RanksHigherWrongCountFirst()
    {
        var corpus = new List<string>();
        // Term A: wrong 30 times.
        corpus.AddRange(Say(("Frederiksen", 60), ("Fredriksson", 18), ("Fredrickson", 12)));
        // Term B: wrong 8 times (distinct first letter so it clusters separately).
        corpus.AddRange(Say(("Kubernetes", 20), ("Kubernetis", 5), ("Kubernettes", 3)));

        var suggestions = Mine(corpus);

        Assert.Equal(2, suggestions.Count);
        Assert.Equal("Frederiksen", suggestions[0].Term);
        Assert.Equal("Kubernetes", suggestions[1].Term);
        Assert.True(suggestions[0].WrongCount > suggestions[1].WrongCount);
    }

    [Fact]
    public void Mine_VariantThatIsItselfAVocabularyWord_IsNotCountedWrong()
    {
        // "form" is real vocabulary; even if it clusters near "from", it must not be scored as a mishearing.
        var corpus = Say(("Frederiksen", 60), ("Fredriksson", 18), ("Fredrickson", 12), ("form", 30)).ToList();
        var dict = Dict(vocab: new[] { "form" });

        var s = Assert.Single(Mine(corpus, dict));
        Assert.Equal("Frederiksen", s.Term);
        Assert.DoesNotContain(s.Variants, v => v.Heard == "form");
    }

    [Fact]
    public void Mine_EmptyCorpus_ReturnsEmpty()
    {
        Assert.Empty(Mine(Array.Empty<string>()));
        Assert.Empty(Mine(new[] { "", "   ", null! }));
    }

    [Fact]
    public void Mine_RespectsMaxSuggestionsCap()
    {
        var corpus = new List<string>();
        // Ten distinct terms (distinct first letters) each clearly wrong enough to suggest.
        var seeds = new[]
        {
            ("Avalonia", "Avalonya"), ("Bitbucket", "Bitbuckit"), ("Cassandra", "Cassaandra"),
            ("Datadog", "Dataadog"), ("Elasticsearch", "Elasticsearcs"), ("Fastlane", "Fastlaine"),
            ("Grafana", "Grafanna"), ("Hashicorp", "Hashicorb"), ("Immutable", "Immutible"),
            ("Jenkins", "Jenkens"),
        };
        foreach (var (right, wrong) in seeds)
            corpus.AddRange(Say((right, 20), (wrong, 10)));

        var suggestions = Mine(corpus, opts: MistranscriptionMiner.Options.Default with { MaxSuggestions = 4 });

        Assert.Equal(4, suggestions.Count);
    }
}
