using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// Regression cover for the over-correction defect (devthrottle_internal #1553).
///
/// The unlisted fuzzy matcher decides on spelling similarity alone. Against a REAL glossary -
/// short, ordinary-looking terms like "Soren", "Codex", "Opus", "ConPty" - that rewrote ordinary
/// English into dictionary terms in the middle of the owner's dictation. A sweep of 22,398 distinct
/// words from the repo docs found 293 ordinary words silently rewritten.
///
/// The existing fuzzy tests missed all of it because they use one to three exotic terms against
/// prose containing no near neighbour, so the negative controls could not fail. These tests use the
/// real glossary shape, and every sentence below is one the feature actually corrupted in
/// production - including the owner's own bug report, which the feature mangled while he wrote it.
///
/// The assertions are exact string equality, not a score. A corrector that leaves correct words
/// alone is the whole requirement.
/// </summary>
public sealed class CleanupOverCorrectionTests
{
    /// <summary>The owner's real glossary shape: 28 terms, several of them short and
    /// collision-prone, plus the hand-listed wrong forms.</summary>
    private static DictationDictionary RealWorldDictionary(bool fuzzy = false)
        => new(
            new[]
            {
                "mindzie", "mindzieStudio", "Frederiksen", "Soren", "Center Consulting",
                "DevThrottle", "ConPty", "cc-director", "Avalonia", "SignalR", "Kestrel",
                "Codex", "Anthropic", "Claude", "Opus", "Sonnet", "Haiku", "Postgres",
                "Tailscale", "Cockpit", "Wingman", "Gateway", "Supabase", "Blazor", "Azure",
                "OAuth", "Whisper", "Kubernetes",
            },
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["mindzie"] = new[] { "Mindsee", "Mindsy", "Mindzee", "Mindsey" },
                ["ConPty"] = new[] { "Con-TY", "ConTY", "Con TY", "Conpity" },
                ["DevThrottle"] = new[] { "Dev Throttle", "Dev-Throttle" },
                ["Supabase"] = new[] { "Superbase" },
            },
            new Dictionary<string, DictationProfile>
            {
                ["default"] = new("default", CleanupEnabled: true, FuzzyCorrectionEnabled: fuzzy),
            });

    private static string Clean(string raw, DictationDictionary dict, ICandidateJudge? judge = null)
        => new CleanupOrchestrator(judge: judge, mode: UnlistedCorrectionMode.Enforce)
            .CleanAsync(raw, dict, "default").GetAwaiter().GetResult().Text;

    /// <summary>
    /// Every one of these was corrupted by the shipped corrector. They must come back byte for byte.
    /// The first two are sentences from the owner's own bug report about this defect.
    /// </summary>
    [Theory]
    [InlineData("make sure to create GitHub issues")]
    [InlineData("it is making too many mistakes")]
    [InlineData("explain the concept to me")]
    [InlineData("that is a good concept")]
    [InlineData("the context of the code")]
    [InlineData("the code is broken")]
    [InlineData("the screen is open")]
    [InlineData("I am not sure")]
    [InlineData("some of them")]
    [InlineData("the score was high")]
    [InlineData("go to the store and buy milk")]
    [InlineData("my throat is sore today")]
    [InlineData("the sort order is wrong")]
    [InlineData("a sorted list of names")]
    [InlineData("he wore a coat")]
    [InlineData("we need to connect the parts")]
    [InlineData("the cloud provider")]
    [InlineData("the getaway car")]
    [InlineData("the cocktail party")]
    [InlineData("please focus on the bonus")]
    [InlineData("the final signal was single")]
    [InlineData("nature has a mature feature")]
    [InlineData("while the white one is wider")]
    [InlineData("the path is auto")]
    public void OrdinaryWords_AreNeverRewrittenIntoDictionaryTerms(string sentence)
        => Assert.Equal(sentence, Clean(sentence, RealWorldDictionary()));

    /// <summary>
    /// The corrections the user LISTED by hand still apply. Turning the guessing off must not cost
    /// them the corrections they chose for themselves - that is the whole point of the split.
    /// </summary>
    [Theory]
    [InlineData("the Con-TY renderer", "the ConPty renderer")]
    [InlineData("I work at Mindsey every day", "I work at mindzie every day")]
    [InlineData("we use Superbase for auth", "we use Supabase for auth")]
    [InlineData("Dev Throttle runs the fleet", "DevThrottle runs the fleet")]
    public void ListedWrongForms_AreStillCorrected(string raw, string expected)
        => Assert.Equal(expected, Clean(raw, RealWorldDictionary()));

    /// <summary>
    /// A term the user listed by hand must survive intact. Letting the fuzzy stage run on the
    /// alias-corrected text was tried and reverted because of exactly this: the matcher skips a
    /// multi-word canonical window but does not RESERVE it, so it then judges each token inside
    /// separately and rewrote half of the phrase stage 1 had just inserted - "alfa beta" became
    /// "Alpha Beta" and then "Alpha Beto".
    ///
    /// Stage 1 short-circuits again, so this cannot happen. The order-dependence that restores is a
    /// known defect tracked with the offset-based apply in #1554, and it is strictly less harmful
    /// than corrupting a term the user chose for themselves.
    /// </summary>
    [Fact]
    public void AnAliasResult_IsNeverPartlyRewrittenByTheFuzzyStage()
    {
        var dict = new DictationDictionary(
            new[] { "Alpha Beta", "Beto" },
            new Dictionary<string, IReadOnlyList<string>> { ["Alpha Beta"] = new[] { "alfa beta" } },
            new Dictionary<string, DictationProfile>
            {
                ["default"] = new("default", CleanupEnabled: true, FuzzyCorrectionEnabled: true),
            });

        Assert.Equal("ship Alpha Beta today", Clean("ship alfa beta today", dict));
    }

    /// <summary>
    /// The negative control for the whole file, and it now has two halves.
    ///
    /// With the guessing enabled AND a judge that accepts everything, the known corruption comes back -
    /// proving the matcher still nominates "sure" as the speaker's name, so the safe results above are
    /// the judge refusing rather than a corrector that quietly stopped working.
    ///
    /// With the guessing enabled and NO judge, the same sentence is untouched. That is the invariant:
    /// the matcher's opinion on its own is never enough.
    /// </summary>
    [Fact]
    public void TheMatcherStillNominatesTheCorruption_AndOnlyTheJudgeStopsIt()
    {
        const string sentence = "make sure to create GitHub issues";

        Assert.Equal("make Soren to create GitHub issues",
            Clean(sentence, RealWorldDictionary(fuzzy: true), Judges.AcceptAll));

        Assert.Equal(sentence, Clean(sentence, RealWorldDictionary(fuzzy: true)));
    }

    /// <summary>Absent from the YAML means off. Every glossary in the field predates this key, so
    /// the default is what actually protects people - not the value we write in new files.</summary>
    [Fact]
    public void FuzzyCorrection_IsOffWhenTheGlossaryDoesNotMentionIt()
    {
        var dict = DictionaryLoader.Parse("""
            vocabulary:
              - Soren
            profiles:
              default:
                cleanup_enabled: true
            """);

        Assert.False(dict.Profiles["default"].FuzzyCorrectionEnabled);
        Assert.Equal("I am not sure", Clean("I am not sure", dict));
    }

    /// <summary>A glossary with no profiles at all gets the same protection.</summary>
    [Fact]
    public void FuzzyCorrection_IsOffWhenTheGlossaryHasNoProfilesAtAll()
    {
        var dict = DictionaryLoader.Parse("""
            vocabulary:
              - Soren
              - Codex
            """);

        Assert.False(dict.Profiles["default"].FuzzyCorrectionEnabled);
        Assert.Equal("the code is broken", Clean("the code is broken", dict));
    }

    /// <summary>The opt-in round-trips through the YAML, so a deliberate enable survives a save.</summary>
    [Fact]
    public void FuzzyCorrection_OptInRoundTripsThroughYaml()
    {
        var reparsed = DictionaryLoader.Parse(DictionaryLoader.Serialize(RealWorldDictionary(fuzzy: true)));

        Assert.True(reparsed.Profiles["default"].FuzzyCorrectionEnabled);
    }

    /// <summary>
    /// The opt-in also survives the Gateway's dictionary JSON. What this parser returns is what
    /// ResolveAsync then writes to the local cache, so dropping the field here would quietly rewrite
    /// a deliberate enable as false on every resolve. Asserted with a TRUE value, because a false one
    /// would pass whether the field was read or ignored.
    /// </summary>
    [Fact]
    public void FuzzyCorrection_OptInSurvivesTheGatewayJson()
    {
        var dict = DictionaryResolver.ParseDictionaryJson("""
            {"vocabulary":["Soren"],"commonMistranscriptions":{},
             "profiles":{"default":{"cleanupEnabled":true,"fuzzyCorrectionEnabled":true}}}
            """);

        Assert.True(dict.Profiles["default"].FuzzyCorrectionEnabled);
    }

    /// <summary>And a Gateway response that says nothing about it still means off.</summary>
    [Fact]
    public void FuzzyCorrection_IsOffWhenTheGatewayJsonOmitsIt()
    {
        var dict = DictionaryResolver.ParseDictionaryJson("""
            {"vocabulary":["Soren"],"profiles":{"default":{"cleanupEnabled":true}}}
            """);

        Assert.False(dict.Profiles["default"].FuzzyCorrectionEnabled);
    }

    /// <summary>Cleanup disabled entirely still means verbatim, aliases included.</summary>
    [Fact]
    public void CleanupDisabled_LeavesEvenListedWrongFormsAlone()
    {
        var dict = RealWorldDictionary() with
        {
            Profiles = new Dictionary<string, DictationProfile>
            {
                ["default"] = new("default", CleanupEnabled: false),
            },
        };

        Assert.Equal("the Con-TY renderer", Clean("the Con-TY renderer", dict));
    }
}
