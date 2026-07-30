namespace CcDirector.Gateway.Speech;

/// <summary>
/// THE VOICES EACH LANGUAGE IS SPOKEN WITH, and the one place a language becomes a list of voices
/// (issue #1010).
///
/// WHY THIS LIST IS HELD HERE AND NOT FETCHED. The truth about which voices the engine will accept lives
/// upstream, in the hosted model registry (<c>website/api/_lib/speech-providers.js</c> in the
/// devthrottle_internal repository), and it is advertised on <c>GET /api/v1/models</c> with each voice's
/// language beside it. The Gateway cannot read that at settings time: the model CATALOG route is refused
/// on the hosted Gateway, because it spends the shared deployment credential with no per-caller scoping
/// (see <c>AiModelsEndpoint</c>). The reverted build decided its language list in the BROWSER from that
/// same catalog, got an empty list on hosted, and the guard that was supposed to fire never fired - the
/// second of the four defects in devthrottle_internal#547. So the offer is held server-side, next to the
/// languages it belongs to, and the client is handed a finished list it renders verbatim.
///
/// Which means this file is a DECIDED OFFER, exactly like <see cref="SpokenLanguages"/>, not a capability
/// readout. The ids and their order are copied from the registry deliberately, so the two can be compared
/// by eye; the names and descriptions are ours, because the registry carries none. Adding a voice is an
/// edit here, made after it has been measured - never a list that grows because an engine learned a new
/// trick, which is the first defect in that same post-mortem.
///
/// THE COUNTS ARE LOPSIDED AND THAT IS NOT A BUG. English has twenty-eight voices, Spanish three, French
/// exactly ONE. That is what Kokoro ships. Every measurement is from 2026-07-29: each of the four
/// non-English voices round-tripped through whisper-large-v3 at a word error rate of 0.000 and was
/// detected as its own language. A language whose whole list is one entry still gets a visible control -
/// see the settings screen on issue #1010: a control that disappears between languages reads as a glitch.
///
/// NOTHING HERE SELECTS A MODEL. A language picks a voice inside the one engine that already serves
/// English, and <c>SpokenLanguageContractTests</c> fails the build if any single method both reads a
/// language and touches the text-to-speech model.
/// </summary>
public static class SpokenVoices
{
    // The id's first letter is the accent ('a' American, 'b' British, 'e' Spanish, 'f' French) and the
    // second is the gender (f/m) - the registry's own convention, which is why the descriptions below can
    // be checked against the ids without leaving this file.

    /// <summary>English: eleven American female, nine American male, four British female, four British
    ///  male. Registry order.</summary>
    private static readonly SpokenVoice[] EnglishVoices =
    {
        new("af_heart", "Heart", "American female"),
        new("af_bella", "Bella", "American female"),
        new("af_nicole", "Nicole", "American female"),
        new("af_sarah", "Sarah", "American female"),
        new("af_sky", "Sky", "American female"),
        new("af_aoede", "Aoede", "American female"),
        new("af_kore", "Kore", "American female"),
        new("af_nova", "Nova", "American female"),
        new("af_alloy", "Alloy", "American female"),
        new("af_jessica", "Jessica", "American female"),
        new("af_river", "River", "American female"),
        new("am_adam", "Adam", "American male"),
        new("am_michael", "Michael", "American male"),
        new("am_echo", "Echo", "American male"),
        new("am_eric", "Eric", "American male"),
        new("am_liam", "Liam", "American male"),
        new("am_onyx", "Onyx", "American male"),
        new("am_puck", "Puck", "American male"),
        new("am_fenrir", "Fenrir", "American male"),
        new("am_santa", "Santa", "American male"),
        new("bf_emma", "Emma", "British female"),
        new("bf_isabella", "Isabella", "British female"),
        new("bf_alice", "Alice", "British female"),
        new("bf_lily", "Lily", "British female"),
        new("bm_george", "George", "British male"),
        new("bm_lewis", "Lewis", "British male"),
        new("bm_daniel", "Daniel", "British male"),
        new("bm_fable", "Fable", "British male"),
    };

    /// <summary>French: one voice, and it is a France voice - the phonemizer has no Canadian French.
    ///  This is the asymmetry the settings screen has to cope with rather than hide.</summary>
    private static readonly SpokenVoice[] FrenchVoices =
    {
        new("ff_siwis", "Siwis", "female"),
    };

    /// <summary>Spanish: one female, two male.</summary>
    private static readonly SpokenVoice[] SpanishVoices =
    {
        new("ef_dora", "Dora", "female"),
        new("em_alex", "Alex", "male"),
        new("em_santa", "Santa", "male"),
    };

    /// <summary>
    /// The voices this language speaks with, in the order a person is offered them.
    ///
    /// THROWS for a language with no voice set instead of returning an empty list. An empty dropdown is
    /// the "it looks broken" state the issue rules out, and a language we cannot speak has no business
    /// being in <see cref="SpokenLanguages.All"/> in the first place - so this fails at the seam where it
    /// can be fixed, loudly, rather than rendering a control with nothing in it.
    /// </summary>
    /// <exception cref="ArgumentException">The language has no voices registered here.</exception>
    public static IReadOnlyList<SpokenVoice> For(SpokenLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (language == SpokenLanguages.English) return EnglishVoices;
        if (language == SpokenLanguages.French) return FrenchVoices;
        if (language == SpokenLanguages.Spanish) return SpanishVoices;
        throw new ArgumentException(
            $"No voices are registered for '{language.Code}'. A language in SpokenLanguages.All must have "
            + "at least one measured voice here, or the settings screen would offer an empty list.",
            nameof(language));
    }

    /// <summary>
    /// The voice a language speaks with when this account has never chosen one for it.
    ///
    /// English resolves to <c>af_bella</c> - the hosted registry's own default voice, so an account that
    /// never opens the Language tab keeps exactly the voice it has always had. French and Spanish resolve
    /// to their first entry. Held as an explicit id per language rather than "whatever happens to be
    /// first in the list", so reordering the list for readability cannot silently change what an account
    /// hears.
    /// </summary>
    public static SpokenVoice Default(SpokenLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var id = language == SpokenLanguages.English ? "af_bella"
            : language == SpokenLanguages.French ? "ff_siwis"
            : language == SpokenLanguages.Spanish ? "ef_dora"
            : throw new ArgumentException(
                $"No default voice is registered for '{language.Code}'.", nameof(language));
        return For(language).Single(v => string.Equals(v.Id, id, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether <paramref name="voiceId"/> is a voice that speaks <paramref name="language"/>.
    ///
    /// This is the check that stops a French account being read out in an English voice. A remembered
    /// voice is validated through here on every read, so a value written by a newer Gateway, or a voice
    /// retired upstream, degrades to the language's default instead of being sent to an engine that
    /// answers 422 - which reaches the listener as silence.
    /// </summary>
    public static bool Speaks(SpokenLanguage language, string? voiceId)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (string.IsNullOrWhiteSpace(voiceId)) return false;
        var trimmed = voiceId.Trim();
        return For(language).Any(v => string.Equals(v.Id, trimmed, StringComparison.Ordinal));
    }

    /// <summary>The language this voice speaks, or null when no language here claims it. Used by the
    ///  settings WRITE path so a voice sent for the wrong language is refused with a message that says
    ///  which language it actually belongs to.</summary>
    public static SpokenLanguage? LanguageOf(string? voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId)) return null;
        return SpokenLanguages.All.FirstOrDefault(l => Speaks(l, voiceId));
    }

    /// <summary>
    /// The one line a person reads in the dropdown: <c>Siwis - French, female</c>,
    /// <c>George - English, British male</c>.
    ///
    /// FOLDED HERE, ON THE GATEWAY, and sent finished. The client renders the string and never assembles
    /// one: a label built in a view is a label that differs between the Cockpit and the phone the first
    /// time either is edited, and the standing rule is that the Gateway owns every display verdict and
    /// the client only draws it (CLAUDE.md rule 7).
    /// </summary>
    public static string Label(SpokenLanguage language, SpokenVoice voice)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(voice);
        return $"{voice.Name} - {language.EnglishName}, {voice.Description}";
    }
}
