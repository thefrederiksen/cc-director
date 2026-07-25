using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// <see cref="SpokenLanguage"/> is the language DevThrottle SPEAKS BACK in. Two properties matter more
/// than the rest and both are asserted here: English is always the answer when anything is wrong or
/// unset (speech must not break over a stale setting), and a speech model is only considered capable of
/// a language when it SAYS SO - an unknown model is English-only, never "speaks everything", because the
/// failure mode of guessing wrong is a model confidently producing gibberish.
/// </summary>
public sealed class SpokenLanguageTests
{
    [Fact]
    public void Default_is_english()
    {
        Assert.Equal("en", SpokenLanguage.Default);
        Assert.True(SpokenLanguage.IsDefault(null));
        Assert.True(SpokenLanguage.IsDefault(""));
        Assert.True(SpokenLanguage.IsDefault("en"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("klingon")]
    [InlineData("zz")]
    public void Normalize_falls_back_to_english_for_anything_we_do_not_offer(string? code)
        => Assert.Equal("en", SpokenLanguage.Normalize(code));

    [Theory]
    [InlineData("da", "da")]
    [InlineData("DA", "da")]
    [InlineData(" de ", "de")]
    public void Normalize_accepts_and_lowercases_offered_languages(string input, string expected)
        => Assert.Equal(expected, SpokenLanguage.Normalize(input));

    [Fact]
    public void The_three_market_languages_and_the_test_language_are_offered()
    {
        // German, French and Spanish are the market bets; Danish is the language the pipeline is
        // proved against. If one of these ever stops being offered it should break here loudly.
        Assert.True(SpokenLanguage.IsSupported("de"));
        Assert.True(SpokenLanguage.IsSupported("fr"));
        Assert.True(SpokenLanguage.IsSupported("es"));
        Assert.True(SpokenLanguage.IsSupported("da"));
    }

    [Fact]
    public void DisplayName_carries_the_endonym_because_people_recognise_their_own_language()
    {
        Assert.Equal("Danish (dansk)", SpokenLanguage.DisplayName("da"));
        Assert.Equal("German (Deutsch)", SpokenLanguage.DisplayName("de"));
        Assert.Equal("English", SpokenLanguage.DisplayName("en"));   // no parenthetical when identical
    }

    [Fact]
    public void EnglishName_is_what_the_prompt_says()
    {
        Assert.Equal("Danish", SpokenLanguage.EnglishName("da"));
        Assert.Equal("Spanish", SpokenLanguage.EnglishName("es"));
    }

    [Fact]
    public void A_model_can_speak_only_what_it_advertises()
    {
        var multilingual = new[] { "en", "da", "de", "fr", "es" };
        Assert.True(SpokenLanguage.ModelCanSpeak(multilingual, "da"));
        Assert.True(SpokenLanguage.ModelCanSpeak(multilingual, "DE"));
        Assert.False(SpokenLanguage.ModelCanSpeak(multilingual, "ja"));
    }

    [Fact]
    public void A_model_that_advertises_nothing_is_english_only_not_everything()
    {
        // The safe direction. Treating silence as "speaks everything" would let the settings offer
        // Danish on a model that answers in English phonetics.
        Assert.True(SpokenLanguage.ModelCanSpeak(null, "en"));
        Assert.False(SpokenLanguage.ModelCanSpeak(null, "da"));
        Assert.True(SpokenLanguage.ModelCanSpeak(new string[0], "en"));
        Assert.False(SpokenLanguage.ModelCanSpeak(new string[0], "da"));
    }

    [Fact]
    public void The_english_only_engine_cannot_be_offered_for_german_or_danish()
    {
        // Mirrors what the catalog publishes for the default speech engine. This is the check that
        // stops a tenant being left on an engine that physically cannot say their language.
        var englishOnly = new[] { "en" };
        Assert.False(SpokenLanguage.ModelCanSpeak(englishOnly, "de"));
        Assert.False(SpokenLanguage.ModelCanSpeak(englishOnly, "da"));
        Assert.True(SpokenLanguage.ModelCanSpeak(englishOnly, "en"));
    }
}
