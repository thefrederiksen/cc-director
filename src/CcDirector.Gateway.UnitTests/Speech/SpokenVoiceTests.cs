using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Speech;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Speech;

/// <summary>
/// THE VOICE IS THE ONLY THING A LANGUAGE CHANGES (issue #1010).
///
/// The Language tab is the setting the reverted build got wrong, and it was reverted for two reasons that
/// both live here rather than in the screen (devthrottle_internal#547). The first is that choosing a
/// language switched the speech MODEL; the pinned guard in <c>SpokenLanguageContractTests</c> owns that one.
/// The second is subtler and has no guard of its own: an account can be set to French and still be READ OUT
/// BY AN ENGLISH VOICE, and that failure is invisible to every test that only checks the setting was saved.
/// The audio plays. The words are French. The voice is American. Nobody's build goes red.
///
/// So these tests are about the RESOLUTION, not the screen: what voice an account is actually spoken with,
/// for every combination of language, remembered choice, corrupt value, and never-touched account.
/// </summary>
public sealed class SpokenVoiceTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly DateTime Now = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);
    private const TranscriptionMode Mode = TranscriptionMode.DevThrottle;

    private static TenantSettingsResolver NewResolver(GatewayDbTestHarness h)
        => new(new TenantSettingsStore(h.Open()));

    // ----------------------------------------------------------------------------------------------
    // The inventory: what we have decided to offer, and the asymmetry that is real.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The measured counts, asserted exactly: twenty-eight English, three Spanish, ONE French.
    ///
    /// Pinned as numbers on purpose. The asymmetry is what the screen has to cope with, and it is also the
    /// thing most likely to be "fixed" by somebody who reads a one-item dropdown as a bug and pads the list
    /// with voices that do not speak French. Kokoro ships one French voice; a second entry here would be
    /// invented.
    /// </summary>
    [Fact]
    public void The_voice_counts_are_the_measured_ones_and_French_has_exactly_one()
    {
        Assert.Equal(28, SpokenVoices.For(SpokenLanguages.English).Count);
        Assert.Equal(3, SpokenVoices.For(SpokenLanguages.Spanish).Count);
        Assert.Single(SpokenVoices.For(SpokenLanguages.French));
        Assert.Equal("ff_siwis", SpokenVoices.For(SpokenLanguages.French)[0].Id);
    }

    /// <summary>Every language the product SPEAKS can be spoken. A language in the offer with no voice
    ///  would render an empty dropdown - the "it looks broken" state - and could not be synthesized at
    ///  all. Written against SpokenLanguages.All rather than the three names, so adding a fourth language
    ///  goes red here until its voices are measured and registered.</summary>
    [Fact]
    public void Every_language_we_sell_has_at_least_one_voice_and_a_default_from_its_own_list()
    {
        foreach (var language in SpokenLanguages.All)
        {
            var voices = SpokenVoices.For(language);
            Assert.NotEmpty(voices);
            var fallback = SpokenVoices.Default(language);
            Assert.Contains(fallback, voices);
        }
    }

    /// <summary>
    /// NO VOICE BELONGS TO TWO LANGUAGES, and every id is unique.
    ///
    /// This is what makes "is this voice one of yours?" answerable at all. A duplicate id would make
    /// <see cref="SpokenVoices.LanguageOf"/> return whichever language happened to be first, and the
    /// mismatch refusal on the write path would start accepting an English voice for French.
    /// </summary>
    [Fact]
    public void A_voice_id_appears_in_exactly_one_language()
    {
        var all = SpokenLanguages.All.SelectMany(l => SpokenVoices.For(l).Select(v => v.Id)).ToList();

        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
        foreach (var language in SpokenLanguages.All)
        foreach (var voice in SpokenVoices.For(language))
        {
            Assert.Equal(language, SpokenVoices.LanguageOf(voice.Id));
            foreach (var other in SpokenLanguages.All.Where(l => l != language))
                Assert.False(SpokenVoices.Speaks(other, voice.Id),
                    $"'{voice.Id}' must not count as a {other.EnglishName} voice.");
        }
    }

    /// <summary>
    /// AN ACCOUNT THAT NEVER OPENS THIS SCREEN IS UNTOUCHED. English's default voice is the hosted
    /// registry's own default, so the new resolution path lands exactly where the old one did.
    ///
    /// Compared against the shipping constant rather than the literal, so if the product default ever moves
    /// this goes red instead of quietly speaking with a different voice than the rest of the product.
    /// </summary>
    [Fact]
    public void English_defaults_to_the_products_own_default_voice()
        => Assert.Equal(TranscriptionEndpointResolver.DefaultTtsVoice(Mode),
            SpokenVoices.Default(SpokenLanguages.English).Id);

    /// <summary>The dropdown line is assembled once, on the Gateway, and reads as a sentence about the
    ///  voice: name, language, then what distinguishes it. Every label is distinct, or two voices would be
    ///  indistinguishable in the list they are chosen from.</summary>
    [Fact]
    public void Every_voice_label_is_folded_by_the_gateway_and_unique_within_its_language()
    {
        Assert.Equal("Siwis - French, female",
            SpokenVoices.Label(SpokenLanguages.French, SpokenVoices.Default(SpokenLanguages.French)));
        Assert.Equal("Bella - English, American female",
            SpokenVoices.Label(SpokenLanguages.English, SpokenVoices.Default(SpokenLanguages.English)));

        foreach (var language in SpokenLanguages.All)
        {
            var labels = SpokenVoices.For(language).Select(v => SpokenVoices.Label(language, v)).ToList();
            Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
        }
    }

    /// <summary>The labels a person reads are ASCII. Voice names and descriptions are user-interface text,
    ///  not spoken content, so the repository's output rule binds them in full - the accents ruling covers
    ///  the payload and nothing else (docs/MISSION-multilingual-RULINGS.md).</summary>
    [Fact]
    public void Voice_labels_are_plain_ascii()
    {
        foreach (var language in SpokenLanguages.All)
        foreach (var voice in SpokenVoices.For(language))
        {
            var label = SpokenVoices.Label(language, voice);
            Assert.All(label, c => Assert.InRange(c, (char)0x20, (char)0x7E));
        }
    }

    // ----------------------------------------------------------------------------------------------
    // Resolution: the voice an account is ACTUALLY spoken with.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// An account that has never touched any of this resolves EXACTLY as it did before the Language tab
    /// existed: no language chosen, no voice chosen, so the operator global default.
    ///
    /// This is the no-regression row. Every existing account is in this state.
    /// </summary>
    [Fact]
    public void An_untouched_account_still_gets_the_operator_default_voice()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        Assert.Equal(TtsVoiceConfig.Resolve(Mode), r.TtsVoice(TenantA, Mode));
    }

    /// <summary>An English account's pre-existing <c>tts_voice</c> override still wins. It was set on the
    ///  old AI tab, it is what that account has been hearing, and a new screen must not silently retune it -
    ///  including when the id is not one of ours, which a self-hosted operator may legitimately have set
    ///  against their own engine.</summary>
    [Theory]
    [InlineData("bm_george")]
    [InlineData("some-operators-own-voice")]
    public void An_english_accounts_existing_voice_override_is_untouched(string existing)
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        r.SetTtsVoice(TenantA, existing, Now);

        Assert.Equal(existing, r.TtsVoice(TenantA, Mode));
    }

    /// <summary>
    /// THE ROW THAT MATTERS: choosing French changes the VOICE, with no voice ever having been chosen.
    ///
    /// And it must be a FRENCH voice - asserted through the inventory rather than against the literal
    /// <c>ff_siwis</c>, so the property under test is "a voice that speaks this language" and not "the
    /// string we happen to have written down".
    /// </summary>
    [Theory]
    [InlineData("fr")]
    [InlineData("es")]
    public void Choosing_a_language_changes_the_voice_to_one_that_speaks_it(string code)
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);
        var language = SpokenLanguages.Require(code);

        r.SetSpokenLanguage(TenantA, code, Now);

        var voice = r.TtsVoice(TenantA, Mode);
        Assert.True(SpokenVoices.Speaks(language, voice),
            $"An account set to {language.EnglishName} resolved to '{voice}', which does not speak it. "
            + "French words read by an English voice is a silent failure - the audio plays and the "
            + "pronunciation is wrong.");
    }

    /// <summary>
    /// A NON-ENGLISH ACCOUNT NEVER FALLS THROUGH TO THE ENGLISH OVERRIDE. An account that once chose an
    /// English voice and then switched language must not keep it: an English voice cannot read French, so
    /// inheriting it would be worse than any default.
    ///
    /// Revert-proof: delete the language branch from the resolver's TtsVoice and this goes red, while the
    /// no-regression test above stays green - which is the pair that pins the behaviour from both sides.
    /// </summary>
    [Fact]
    public void A_french_account_does_not_inherit_the_english_voice_override()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        r.SetTtsVoice(TenantA, "bm_george", Now);
        r.SetSpokenLanguage(TenantA, "fr", Now);

        Assert.NotEqual("bm_george", r.TtsVoice(TenantA, Mode));
        Assert.True(SpokenVoices.Speaks(SpokenLanguages.French, r.TtsVoice(TenantA, Mode)));
    }

    /// <summary>
    /// THE ACCEPTANCE ROW ON ISSUE #1010: English -> French -> English restores the original English voice.
    ///
    /// There is no restore step in the code, and that is the point. Each language's choice is stored under
    /// its own key, so a change to one cannot overwrite another and nothing has to be put back. The
    /// reverted build had an automatic switch AND a restore, and the restore was one of the moving parts
    /// that made it impossible to reason about.
    /// </summary>
    [Fact]
    public void Each_language_remembers_its_own_voice_so_a_round_trip_restores_nothing()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        r.SetSpokenVoice(TenantA, SpokenLanguages.English, "bm_george", Now);
        Assert.Equal("bm_george", r.TtsVoice(TenantA, Mode));

        r.SetSpokenLanguage(TenantA, "es", Now);
        r.SetSpokenVoice(TenantA, SpokenLanguages.Spanish, "em_alex", Now);
        Assert.Equal("em_alex", r.TtsVoice(TenantA, Mode));

        r.SetSpokenLanguage(TenantA, "fr", Now);
        Assert.Equal("ff_siwis", r.TtsVoice(TenantA, Mode));

        // Back to English, having chosen a voice in two other languages in between.
        r.SetSpokenLanguage(TenantA, "en", Now);
        Assert.Equal("bm_george", r.TtsVoice(TenantA, Mode));

        // And Spanish still holds its own choice, not the English one and not its default.
        r.SetSpokenLanguage(TenantA, "es", Now);
        Assert.Equal("em_alex", r.TtsVoice(TenantA, Mode));
    }

    /// <summary>
    /// ABSENT AND BLANK ARE DIFFERENT ROWS (audit 4, finding F1).
    ///
    /// No choice made is NO ROW, and that legitimately reads as English - it is what every account had before the
    /// setting existed. A row that is PRESENT and blank is something else entirely: the write path stores a
    /// canonical code and the store rejects null, so three spaces in that row can only be malformed or
    /// rolled-back data. The read used to test IsNullOrWhiteSpace and laundered it into English, which is the
    /// same silent default the mission removed everywhere else - a probe pushed real English speech through it.
    /// </summary>
    [Fact]
    public void No_stored_language_row_reads_as_English_but_a_blank_one_is_refused()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());
        var r = new TenantSettingsResolver(store);

        // Absent: the documented default, and the only absence that is legitimate.
        Assert.Equal(SpokenLanguages.English, r.SpokenLanguage(TenantA));

        // Present and blank: malformed, and it fails loudly rather than being spoken as English.
        store.Set(TenantA, TenantSettingKeys.SpokenLanguage, "   ", Now);
        var ex = Assert.Throws<ArgumentException>(() => r.SpokenLanguage(TenantA));
        Assert.Contains("not a language DevThrottle speaks", ex.Message);
    }

    /// <summary>A voice that does not speak the language is REFUSED on the write, where the person can see
    ///  it, and the message names the language it does belong to. A read degrades; a write does not - the
    ///  same split the language setting itself uses, for the same reason.</summary>
    [Fact]
    public void Setting_a_voice_for_the_wrong_language_is_refused_and_says_which_language_owns_it()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        var ex = Assert.Throws<ArgumentException>(
            () => r.SetSpokenVoice(TenantA, SpokenLanguages.French, "bm_george", Now));

        Assert.Contains("not a French voice", ex.Message);
        Assert.Contains("English voice", ex.Message);
        Assert.Contains("ff_siwis", ex.Message);
        // And nothing was written: the account is still on its own resolution.
        Assert.Empty(r.SpokenVoicesByLanguage(TenantA));
    }

    /// <summary>An unknown voice is refused too, and the message lists what this language offers rather than
    ///  simply saying no.</summary>
    [Fact]
    public void Setting_an_unknown_voice_is_refused()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        var ex = Assert.Throws<ArgumentException>(
            () => r.SetSpokenVoice(TenantA, SpokenLanguages.Spanish, "no_such_voice", Now));

        Assert.Contains("ef_dora", ex.Message);
    }

    /// <summary>
    /// A STORED VOICE THAT NO LONGER SPEAKS ITS LANGUAGE DEGRADES to that language's default rather than
    /// being sent to the engine. Written straight to the store, because that is how it would arrive: a
    /// newer Gateway wrote a voice this one does not know, or a voice was retired upstream.
    ///
    /// The direction matters. An unknown voice id returns 422 from the engine, which reaches the listener as
    /// SILENCE - and silence in a car is the worst answer available. A voice that certainly works is the
    /// right degradation.
    /// </summary>
    [Fact]
    public void A_stored_voice_that_does_not_speak_the_language_degrades_to_that_languages_default()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());
        var r = new TenantSettingsResolver(store);

        r.SetSpokenLanguage(TenantA, "fr", Now);
        store.Set(TenantA, TenantSettingKeys.SpokenVoiceByLanguage, """{"fr":"bm_george"}""", Now);

        Assert.Equal(SpokenVoices.Default(SpokenLanguages.French).Id, r.TtsVoice(TenantA, Mode));
        Assert.Null(r.SpokenVoice(TenantA, SpokenLanguages.French));
    }

    /// <summary>A corrupt map reads as "no choices made" - each language's own default - never a crash on a
    ///  spoken turn and never another tenant's value.</summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public void A_corrupt_voice_map_degrades_to_the_language_default(string corrupt)
    {
        using var h = new GatewayDbTestHarness();
        var store = new TenantSettingsStore(h.Open());
        var r = new TenantSettingsResolver(store);

        r.SetSpokenLanguage(TenantA, "es", Now);
        store.Set(TenantA, TenantSettingKeys.SpokenVoiceByLanguage, corrupt, Now);

        Assert.Empty(r.SpokenVoicesByLanguage(TenantA));
        Assert.Equal(SpokenVoices.Default(SpokenLanguages.Spanish).Id, r.TtsVoice(TenantA, Mode));
    }

    /// <summary>
    /// ONE ACCOUNT'S LANGUAGE AND VOICE NEVER REACH ANOTHER. Both are per-account settings on a shared
    /// hosted Gateway, and the failure would be a stranger's fleet suddenly speaking Spanish.
    /// </summary>
    [Fact]
    public void A_language_and_voice_choice_does_not_leak_between_accounts()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        r.SetSpokenLanguage(TenantA, "es", Now);
        r.SetSpokenVoice(TenantA, SpokenLanguages.Spanish, "em_santa", Now);

        Assert.Equal("em_santa", r.TtsVoice(TenantA, Mode));
        Assert.Equal(SpokenLanguages.English, r.SpokenLanguage(TenantB));
        Assert.Equal(TtsVoiceConfig.Resolve(Mode), r.TtsVoice(TenantB, Mode));
        Assert.Empty(r.SpokenVoicesByLanguage(TenantB));
    }
}
