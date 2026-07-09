using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// Unit tests for the deterministic candidate generator that replaced the cleanup language model.
/// It only PROPOSES edits; the assertions here check which spans it proposes and, just as importantly,
/// which it leaves alone. Final safety is the engine's job (<see cref="TranscriptEditEngine"/>).
/// </summary>
public sealed class FuzzyDictionaryMatcherTests
{
    private static DictationDictionary Dict(params string[] vocab)
        => new(vocab, new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, DictationProfile> { ["default"] = new("default", true) });

    private static (string Find, string Replace)[] Propose(string text, DictationDictionary dict)
        => FuzzyDictionaryMatcher.Propose(text, dict).Select(e => (e.Find, e.Replace)).ToArray();

    [Fact]
    public void Propose_PhoneticMishearing_MapsToCanonicalTerm()
    {
        var edits = Propose("my buddy Mindsey called", Dict("mindzie"));
        Assert.Contains(("Mindsey", "mindzie"), edits);
    }

    [Fact]
    public void Propose_OneToken_NearMiss_MapsToTerm()
    {
        var edits = Propose("we deployed Terascale last night", Dict("Tailscale"));
        Assert.Contains(("Terascale", "Tailscale"), edits);
    }

    [Fact]
    public void Propose_TwoWordSpokenForm_CollapsesToSingleToken()
    {
        var edits = Propose("open the Acme Flow dashboard", Dict("acmeflow"));
        Assert.Contains(("Acme Flow", "acmeflow"), edits);
    }

    [Fact]
    public void Propose_CasingOnlyVariant_IsProposed()
    {
        var edits = Propose("the CONPTY renderer", Dict("ConPTY"));
        Assert.Contains(("CONPTY", "ConPTY"), edits);
    }

    [Fact]
    public void Propose_ExactlyCorrectTerm_IsNotProposed()
    {
        var edits = Propose("the acmeflow dashboard is fine", Dict("acmeflow"));
        Assert.Empty(edits);
    }

    [Fact]
    public void Propose_OrdinaryProse_ProposesNothing()
    {
        var edits = Propose("can you show me the plan please and wrap up", Dict("acmeflow", "cc-director", "ConPTY"));
        Assert.Empty(edits);
    }

    [Fact]
    public void Propose_CommonWord_IsNotRewrittenIntoJargon()
    {
        // "session" must never become a jargon term even if a term is vaguely similar.
        var edits = Propose("start the session now", Dict("CenCon"));
        Assert.Empty(edits);
    }

    [Fact]
    public void Propose_MultiWordWindow_DoesNotSwallowStopWord()
    {
        // Must not glue "Akmeflow and" -> acmeflow (dropping "and"); the single-token match is correct.
        var edits = Propose("run Akmeflow and stop", Dict("acmeflow"));
        Assert.Contains(("Akmeflow", "acmeflow"), edits);
        Assert.DoesNotContain(edits, e => e.Find.Contains(' '));
    }

    [Fact]
    public void Propose_EmptyDictionary_ProposesNothing()
    {
        Assert.Empty(Propose("Mindsey and Akmeflow", Dict()));
    }

    // ===== language-agnostic: no bundled word list, works on any language ====

    [Fact]
    public void Propose_NonEnglishProse_WithNoNearbyTerm_ProposesNothing()
    {
        // A German sentence with no term anywhere near the vocabulary must be left untouched - there is
        // no English (or any) word list involved, only similarity to the user's own terms.
        var edits = Propose("der schnelle braune Fuchs springt ueber den Hund", Dict("acmeflow", "ConPTY"));
        Assert.Empty(edits);
    }

    [Fact]
    public void Propose_MishearingInNonEnglishSentence_StillCorrected()
    {
        // The surrounding language does not matter; a clear mishearing of a term is still caught.
        var edits = Propose("ich benutze Akmeflow jeden Tag", Dict("acmeflow"));
        Assert.Contains(("Akmeflow", "acmeflow"), edits);
    }

    [Fact]
    public void Propose_RealWordSharingPrefixWithTerm_IsNotProposed()
    {
        // "avalanche" shares the "aval" prefix with "Avalonia" but is a different word. Without a
        // Winkler prefix bonus and with a conservative threshold, it must not be proposed.
        Assert.Empty(Propose("the avalanche warning came overnight", Dict("Avalonia")));
    }

    [Theory]
    [InlineData("mindzie", "mindzie", 1.0)]
    [InlineData("", "", 1.0)]
    [InlineData("abc", "", 0.0)]
    public void Jaro_EdgeCases(string a, string b, double expected)
        => Assert.Equal(expected, FuzzyDictionaryMatcher.Jaro(a, b), 3);

    [Fact]
    public void Jaro_MoreCharacterOverlap_ScoresHigher()
    {
        var closer = FuzzyDictionaryMatcher.Jaro("mindsey", "mindzie");
        var farther = FuzzyDictionaryMatcher.Jaro("xyzzey", "mindzie");
        Assert.True(closer > farther);
    }
}
