using System.Reflection;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Speech;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Speech;

/// <summary>
/// ONE PLACE WE SPEAK FROM (issue #1031).
///
/// The product speaks from several places, and the defect was never that there were several SPEAKERS - it is
/// that there were several DECIDERS. Each one worked out its own language and voice, so it was always possible
/// to hand a sink some text and no language at all. It happened twice: the desktop resolved a voice from a
/// machine-global file and never saw the account, and the browser set no language on its utterance and read
/// correct French in an English voice. Both are silent failures - the audio plays.
///
/// So the language stopped being something a caller passes and became something a TYPE carries. What these
/// tests pin is the shape of that type, and the claim that rests on it: adding a fourth language is a
/// one-place change.
/// </summary>
public sealed class SpokenUtteranceTests
{
    private static readonly TenantId Tenant = new("tenant-utterance");
    private static readonly DateTime Now = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);
    private const TranscriptionMode Mode = TranscriptionMode.DevThrottle;

    private static TenantSettingsResolver NewResolver(GatewayDbTestHarness h)
        => new(new TenantSettingsStore(h.Open()));

    // ----------------------------------------------------------------------------------------------
    // The type: it cannot exist without a language.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// THERE IS NO WAY TO BUILD AN UTTERANCE WITHOUT NAMING A LANGUAGE.
    ///
    /// Asserted through reflection rather than by trying to write the bad call, because the bad call does not
    /// compile - which is the property. This checks the SHAPE that makes it not compile: no public constructor
    /// at all, and every factory takes a language. If someone adds a convenience overload without one, this
    /// goes red before the convenience can be used.
    /// </summary>
    [Fact]
    public void An_utterance_cannot_be_constructed_without_a_language()
    {
        Assert.Empty(typeof(SpokenUtterance).GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        var factories = typeof(SpokenUtterance)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(SpokenUtterance))
            .ToList();

        Assert.NotEmpty(factories);
        foreach (var factory in factories)
        {
            Assert.Contains(factory.GetParameters(), p => p.ParameterType == typeof(SpokenLanguage));
            // And it cannot be omitted at the call site either - an optional language is an absent language.
            Assert.All(factory.GetParameters().Where(p => p.ParameterType == typeof(SpokenLanguage)),
                p => Assert.False(p.IsOptional, "The language must not be optional."));
        }
    }

    /// <summary>A null language FAILS LOUD rather than defaulting to English. A quiet English default is the
    ///  reported bug itself: the setting appears to do nothing.</summary>
    [Fact]
    public void A_null_language_throws_instead_of_defaulting_to_English()
        => Assert.Throws<ArgumentNullException>(() => SpokenUtterance.For(null!, "af_bella", "hello"));

    /// <summary>Blank words and a blank voice both fail here, where the caller can see them. Synthesizing an
    ///  empty string is billed and returns silence, and a blank voice reaches the engine as a 422 - both arrive
    ///  at the listener as a voice that failed, which is the hardest kind of failure to attribute.</summary>
    [Theory]
    [InlineData("", "hello")]
    [InlineData("   ", "hello")]
    [InlineData("af_bella", "")]
    [InlineData("af_bella", "   ")]
    public void A_blank_voice_or_blank_words_are_refused(string voice, string text)
        => Assert.Throws<ArgumentException>(() => SpokenUtterance.For(SpokenLanguages.English, voice, text));

    /// <summary>
    /// AN UTTERANCE CARRIES NO MODEL AND NO ENGINE.
    ///
    /// This is the rule the whole mission turns on. Choosing a language switched the speech MODEL in the build
    /// that was reverted, and that engine could not say the lengths this product writes. If a model ever appears
    /// on this type, every sink in the product would be one field away from selecting an engine from a language.
    /// </summary>
    [Fact]
    public void An_utterance_carries_no_model_or_engine()
    {
        var members = typeof(SpokenUtterance).GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Concat(typeof(SpokenUtterance).GetMembers(BindingFlags.Public | BindingFlags.Static))
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain(members, m => m.Contains("Model", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, m => m.Contains("Engine", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, m => m.Contains("Endpoint", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The language cannot be changed after the decision: no setter, and the only way to alter an
    ///  utterance keeps its language. A settable language is a language that can be set to the wrong one, one
    ///  hop away from the decision that got it right.</summary>
    [Fact]
    public void The_language_and_voice_cannot_be_reassigned()
    {
        foreach (var name in new[] { nameof(SpokenUtterance.Language), nameof(SpokenUtterance.Voice), nameof(SpokenUtterance.Text) })
            Assert.Null(typeof(SpokenUtterance).GetProperty(name)!.SetMethod);

        var original = SpokenUtterance.For(SpokenLanguages.French, "ff_siwis", "Bonjour.");
        var reworded = original.WithText("Bonjour a nouveau.");
        Assert.Equal(SpokenLanguages.French, reworded.Language);
        Assert.Equal("ff_siwis", reworded.Voice);
    }

    /// <summary>The log-safe fact is a LENGTH, and it is on the type so a log line has something correct to
    ///  reach for. Spoken content carries accents and must never reach an output channel
    ///  (docs/MISSION-multilingual-RULINGS.md, guard 1); a length answers the question a log line is usually
    ///  asking anyway.</summary>
    [Fact]
    public void An_utterance_offers_its_length_so_a_log_never_needs_its_words()
        => Assert.Equal(8, SpokenUtterance.For(SpokenLanguages.French, "ff_siwis", "Bonjour.").Length);

    /// <summary>
    /// NONBLANK IS NOT VALID (re-audit, the one root cause).
    ///
    /// The first version of this checked that a language was non-empty, and every downstream check asked the same
    /// weak question. So `new SpokenLanguage("zz", "Unknown", "Unknown")` was ordinary compiling code that
    /// satisfied the factory, and an utterance built on it was speakable. The gap between "nonblank" and "known"
    /// is this entire mission.
    /// </summary>
    [Theory]
    [InlineData("zz")]
    [InlineData("de")]
    [InlineData("en-GB")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_language_that_is_not_one_we_speak_cannot_be_constructed(string code)
        => Assert.Throws<ArgumentException>(() => new SpokenLanguage(code, "Unknown", "Unknown"));

    /// <summary>The known codes and the offered languages are ONE list in two shapes, and they cannot drift: the
    ///  codes validate the instances, so a code with no language - or a language with no code - fails here.</summary>
    [Fact]
    public void The_known_codes_and_the_offered_languages_are_the_same_set()
    {
        var offered = SpokenLanguages.All.Select(l => l.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var language in SpokenLanguages.All)
            Assert.NotNull(SpokenLanguages.TryResolve(language.Code));
        Assert.Equal(offered.Count, SpokenLanguages.All.Count);
    }

    /// <summary>
    /// AN UNKNOWN CODE NEVER SILENTLY BECOMES ENGLISH. That line was the most dangerous in the mission: it turned
    /// every unrecognized code into a confident English answer that no caller could distinguish from a real one.
    /// TryResolve says "I do not know", and Require says so loudly.
    /// </summary>
    [Theory]
    [InlineData("de")]
    [InlineData("zz")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_code_resolves_to_nothing_rather_than_English(string? code)
    {
        Assert.Null(SpokenLanguages.TryResolve(code));
        var ex = Assert.Throws<ArgumentException>(() => SpokenLanguages.Require(code));
        Assert.Contains("not a language DevThrottle speaks", ex.Message);
    }

    // ----------------------------------------------------------------------------------------------
    // The one decider.
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The resolver decides both halves together, so a voice always speaks the language it was chosen for.
    ///
    /// Checked for French and Spanish rather than only English, because English is the default and an
    /// English-only check would pass on a resolver that always said English.
    /// </summary>
    [Theory]
    [InlineData("fr")]
    [InlineData("es")]
    public void The_resolver_decides_the_language_and_a_voice_that_speaks_it(string code)
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);
        r.SetSpokenLanguage(Tenant, code, Now);

        var utterance = r.Utterance(Tenant, Mode, "des mots");

        Assert.Equal(SpokenLanguages.Require(code), utterance.Language);
        Assert.True(SpokenVoices.Speaks(utterance.Language, utterance.Voice),
            $"An account set to {code} got voice '{utterance.Voice}', which does not speak it.");
    }

    /// <summary>
    /// A caller may audition a DIFFERENT VOICE - the Language tab offers one before it is chosen - and it may
    /// NOT ask to be spoken to in a different language.
    ///
    /// Both halves matter. Without the first, the Play sample button cannot exist. With a language override, the
    /// one decision stops being one decision, and a page could show French while the account is English - which
    /// is precisely the third defect in devthrottle_internal#547, where the sample auditioned what the PAGE
    /// believed rather than what the ACCOUNT was set to.
    /// </summary>
    [Fact]
    public void A_caller_can_audition_a_voice_but_cannot_choose_a_language()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);
        r.SetSpokenLanguage(Tenant, "es", Now);

        var audition = r.Utterance(Tenant, Mode, "unas palabras", voiceOverride: "em_santa");

        Assert.Equal("em_santa", audition.Voice);
        Assert.Equal(SpokenLanguages.Spanish, audition.Language);

        // There is no language parameter to pass. Asserted on the signature, because the absence is the point.
        var parameters = typeof(TenantSettingsResolver)
            .GetMethod(nameof(TenantSettingsResolver.Utterance))!
            .GetParameters();
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(SpokenLanguage));
    }

    /// <summary>A blank override is the ordinary path - "the account's own voice" - not an error.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_voice_override_means_the_accounts_own_voice(string? blank)
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);
        r.SetSpokenLanguage(Tenant, "fr", Now);

        Assert.Equal(r.TtsVoice(Tenant, Mode), r.Utterance(Tenant, Mode, "des mots", blank).Voice);
    }

    /// <summary>
    /// TIGHTENING THE LANGUAGE DID NOT TIGHTEN THE OPERATOR'S OWN VOICE.
    ///
    /// Two rules that pull in opposite directions, and both were decided deliberately:
    ///   - a voice belonging to ANOTHER KNOWN language is refused, because we can be certain it is wrong;
    ///   - a voice belonging to NO known language is ALLOWED, because a self-hosted operator may have configured
    ///     their own voice against their own engine, and refusing it would take speech away from an account that
    ///     works today.
    ///
    /// The second is the one at risk from a change that makes the first stricter, and it has no other test: the
    /// language rules got tighter twice in one night, and "we deliberately allowed this" is worth exactly nothing
    /// unless something fails when it stops being true.
    /// </summary>
    [Fact]
    public void A_custom_voice_belonging_to_no_known_language_is_still_allowed()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);
        r.SetSpokenLanguage(Tenant, "en", Now);
        r.SetTtsVoice(Tenant, "an-operators-own-voice", Now);

        // Through the resolver, which is how a self-hosted operator's voice actually reaches synthesis.
        var utterance = r.Utterance(Tenant, Mode, "words to say");
        Assert.Equal("an-operators-own-voice", utterance.Voice);
        Assert.Equal(SpokenLanguages.English, utterance.Language);

        // And directly at the factory, for every language - an unknown-owner voice is not "wrong", it is unknown.
        foreach (var language in SpokenLanguages.All)
            Assert.Equal("an-operators-own-voice",
                SpokenUtterance.For(language, "an-operators-own-voice", "words").Voice);
    }

    /// <summary>
    /// A voice from ANOTHER KNOWN language is still refused, in both directions, so the allowance above cannot be
    /// read as "any string goes". This is the pair that makes each half meaningful.
    /// </summary>
    [Fact]
    public void A_voice_from_another_known_language_is_still_refused()
    {
        Assert.Throws<ArgumentException>(
            () => SpokenUtterance.For(SpokenLanguages.French, "af_bella", "des mots"));
        Assert.Throws<ArgumentException>(
            () => SpokenUtterance.For(SpokenLanguages.English, "ff_siwis", "some words"));
    }

    /// <summary>
    /// CASING AND WHITESPACE DO NOT SMUGGLE A LANGUAGE PAST THE RULE, in either direction: a known code with odd
    /// spacing or capitals is still known, and an unknown one is still unknown however it is dressed. Worth its
    /// own test because "KNOWN" is now enforced by string comparison in several places, and a comparison is where
    /// a rule quietly stops applying.
    /// </summary>
    [Theory]
    [InlineData("EN")]
    [InlineData(" fr ")]
    [InlineData("Es")]
    public void A_known_code_survives_casing_and_whitespace(string code)
        => Assert.NotNull(SpokenLanguages.TryResolve(code));

    [Theory]
    [InlineData(" DE ")]
    [InlineData("ZZ")]
    [InlineData("en_GB")]
    public void An_unknown_code_is_still_unknown_however_it_is_dressed(string code)
        => Assert.Null(SpokenLanguages.TryResolve(code));

    /// <summary>
    /// The static initializers survive being reached from EITHER side.
    ///
    /// The known-code set lives on <see cref="SpokenLanguage"/> and is read while
    /// <see cref="SpokenLanguages"/> is constructing its instances, so the two types initialize each other's
    /// statics in an order this test forces rather than assumes: touch the language type first here, and the
    /// collection first everywhere else. A broken order is a run-time null on first use, which is exactly the
    /// class of failure a test suite usually misses because something else always touched it first.
    /// </summary>
    [Fact]
    public void The_language_type_can_be_reached_before_the_language_list()
    {
        var direct = new SpokenLanguage("fr", "French", "Francais");
        Assert.Equal("fr", direct.Code);
        Assert.Equal(SpokenLanguages.French, direct);
    }

    /// <summary>
    /// THE ONE-PLACE CLAIM, DEMONSTRATED RATHER THAN ASSERTED (issue #1031's acceptance row).
    ///
    /// Adding a fourth language means one edit: a row in <see cref="SpokenLanguages.All"/> with its voices. This
    /// test walks EVERY language the product offers and proves each one already resolves end to end - language,
    /// a voice that speaks it, and an utterance - through the single decider, with no per-language code
    /// anywhere. So a fourth row is served by the same path the other three are, on the day it is added.
    ///
    /// It is written against SpokenLanguages.All rather than against three names on purpose: the day somebody
    /// adds German, this test covers German without being edited. That is what makes it a demonstration of the
    /// property instead of a restatement of today's list.
    /// </summary>
    [Fact]
    public void Every_language_the_product_offers_resolves_through_the_one_decider()
    {
        using var h = new GatewayDbTestHarness();
        var r = NewResolver(h);

        foreach (var language in SpokenLanguages.All)
        {
            r.SetSpokenLanguage(Tenant, language.Code, Now);
            var utterance = r.Utterance(Tenant, Mode, "words to say");

            Assert.Equal(language, utterance.Language);
            Assert.True(SpokenVoices.Speaks(language, utterance.Voice));
            // And the fixed spoken sentences exist in it, which is the other half of a language being usable:
            // a language with no translations does not build (SpokenPhraseTests), and one with no voice cannot
            // be spoken. Both are checked from the same single list.
            Assert.False(string.IsNullOrWhiteSpace(SpokenPhrases.SettingsVoiceSample.In(language)));
        }
    }
}
