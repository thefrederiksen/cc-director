using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// Tests for the cleanup orchestrator. It applies the
/// exact/alias map, then the <see cref="FuzzyDictionaryMatcher"/>, and validates every proposed edit
/// through <see cref="TranscriptEditEngine"/>. These pin the two invariants that matter: real
/// dictionary mishearings get corrected, and text that is NOT a dictionary term is never touched.
///
/// This file exercises the fuzzy stage ON PURPOSE, so its dictionaries opt in explicitly
/// (<see cref="DictationProfile.FuzzyCorrectionEnabled"/>). That stage is OFF by default in the
/// field; the default and the over-correction it prevents are covered by
/// <see cref="CleanupOverCorrectionTests"/>.
///
/// It also supplies an ACCEPT-ALL judge in Enforce mode, because an unlisted correction now needs an
/// affirmative ruling to reach the text. That keeps these tests measuring what they were written to
/// measure - which spans the matcher finds - rather than silently passing because nothing is applied
/// any more. What happens with a refusing or absent judge is <see cref="DictationJudgeTests"/>.
/// </summary>
public sealed class CleanupOrchestratorTests
{
    private static DictationDictionary BuildDict(
        IReadOnlyList<string>? vocab = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? patterns = null,
        IReadOnlyDictionary<string, DictationProfile>? profiles = null)
        => new(
            vocab ?? Array.Empty<string>(),
            patterns ?? new Dictionary<string, IReadOnlyList<string>>(),
            profiles ?? new Dictionary<string, DictationProfile>
            {
                ["default"] = new DictationProfile(
                    "default", CleanupEnabled: true, FuzzyCorrectionEnabled: true),
            });

    private static DictationDictionary ProductionLikeDict() => BuildDict(
        vocab: new[] { "acmeflow", "cc-director", "ConPTY", "mindzie", "Tailscale" },
        patterns: new Dictionary<string, IReadOnlyList<string>>
        {
            ["cc-director"] = new[] { "CC Director", "See Director" },
            ["ConPTY"] = new[] { "Conty" },
        });

    private static async Task<CleanupOutcome> Clean(string raw, DictationDictionary dict, string profile = "default")
        => await new CleanupOrchestrator(
                judge: Judges.AcceptAll, mode: UnlistedCorrectionMode.Enforce)
            .CleanAsync(raw, dict, profile);

    [Fact]
    public async Task CleanAsync_EmptyInput_ReturnsEmpty()
    {
        var outcome = await Clean("", BuildDict(vocab: new[] { "acmeflow" }));
        Assert.False(outcome.Applied);
        Assert.Equal("", outcome.Text);
    }

    [Fact]
    public async Task CleanAsync_EmptyDictionary_ReturnsRawVerbatim()
    {
        var outcome = await Clean("hello there um world", BuildDict());
        Assert.False(outcome.Applied);
        Assert.Equal("hello there um world", outcome.Text);
        Assert.Contains("no dictionary terms", outcome.Reason);
    }

    [Fact]
    public async Task CleanAsync_CleanupDisabledProfile_ReturnsRawVerbatim()
    {
        var profiles = new Dictionary<string, DictationProfile>
        {
            ["code"] = new DictationProfile("code", CleanupEnabled: false),
            ["default"] = new DictationProfile("default", CleanupEnabled: true),
        };
        var dict = BuildDict(vocab: new[] { "acmeflow" }, profiles: profiles);
        var outcome = await Clean("hello world", dict, "code");
        Assert.False(outcome.Applied);
        Assert.Equal("hello world", outcome.Text);
        Assert.Contains("cleanup disabled", outcome.Reason);
    }

    // ===== stage 1: exact/alias map ==========================================

    [Fact]
    public async Task CleanAsync_KnownMistranscription_AppliesDeterministicallyFromMap()
    {
        var outcome = await Clean("please test the Conty terminal path", ProductionLikeDict());
        Assert.True(outcome.Applied);
        Assert.Equal("please test the ConPTY terminal path", outcome.Text);
        Assert.Contains("deterministic", outcome.Reason);
    }

    [Fact]
    public async Task CleanAsync_ListedTwoWordAlias_Applied()
    {
        var outcome = await Clean("push it to See Director tonight", ProductionLikeDict());
        Assert.True(outcome.Applied);
        Assert.Equal("push it to cc-director tonight", outcome.Text);
    }

    // ===== stage 2: fuzzy matcher (replaces the old LLM proposal step) ========

    [Fact]
    public async Task CleanAsync_UnlistedPhoneticMishearing_CorrectedByFuzzyMatcher()
    {
        // "Mindsey" / "Terascale" / "Akmeflow" are NOT in the alias map for these terms, so this is the
        // exact case that used to force the LLM call. The fuzzy matcher must catch them in-process.
        var outcome = await Clean("my buddy Mindsey uses Akmeflow and Terascale every day", ProductionLikeDict());
        Assert.True(outcome.Applied);
        Assert.Equal("my buddy mindzie uses acmeflow and Tailscale every day", outcome.Text);
    }

    [Fact]
    public async Task CleanAsync_TwoWordSpokenForm_CollapsesToSingleTerm()
    {
        var outcome = await Clean("open the Acme Flow dashboard", ProductionLikeDict());
        Assert.True(outcome.Applied);
        Assert.Equal("open the acmeflow dashboard", outcome.Text);
    }

    [Fact]
    public async Task CleanAsync_CasingOnlyMishearing_Normalized()
    {
        var outcome = await Clean("restart the CONPTY renderer", ProductionLikeDict());
        Assert.True(outcome.Applied);
        Assert.Equal("restart the ConPTY renderer", outcome.Text);
    }

    // ===== precision: ordinary text is never rewritten =======================

    [Fact]
    public async Task CleanAsync_NoJargon_ReturnsRawVerbatim()
    {
        const string raw = "can you show me the plan please and then wrap up for the day";
        var outcome = await Clean(raw, ProductionLikeDict());
        Assert.False(outcome.Applied);
        Assert.Equal(raw, outcome.Text);
    }

    [Fact]
    public async Task CleanAsync_MultiWordWindowNeverSwallowsNeighbourWord()
    {
        // Regression: a two-word window must not glue a term to a stop word or an already-correct term
        // (that would drop "and"/"the"). Every non-term word must survive verbatim.
        var outcome = await Clean("check the acmeflow and the ConPTY logs", ProductionLikeDict());
        Assert.Equal("check the acmeflow and the ConPTY logs", outcome.Text);
    }

    [Fact]
    public async Task CleanAsync_PlausibleButInnocentWord_NotCorrected()
    {
        // "Avalanche" is not "Avalonia" - the speaker may really have said it. No guessing.
        var dict = BuildDict(vocab: new[] { "Avalonia" });
        var outcome = await Clean("the avalanche warning came in overnight", dict);
        Assert.False(outcome.Applied);
        Assert.Equal("the avalanche warning came in overnight", outcome.Text);
    }
}
