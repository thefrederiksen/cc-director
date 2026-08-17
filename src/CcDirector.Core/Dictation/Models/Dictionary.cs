namespace CcDirector.Core.Dictation.Models;

/// <summary>
/// User-editable dictation dictionary loaded from a YAML file.
///
/// NOTHING IN HERE IS EVER SENT TO THE SPEECH-TO-TEXT PROVIDER. The transcriber
/// is given audio only, with no vocabulary and no other steering hint, so that it
/// writes down what was actually said. Everything below is applied afterwards, by
/// a separate pass over the finished transcript. See the note where
/// <c>BuildSttPrompt</c> was deleted in <c>DictionaryLoader</c> for why (issue 2481).
///
/// Two layers of knowledge:
/// 1. <see cref="Vocabulary"/> - canonical terms the speaker uses. Read by the
///    correction pass, which restores a term to its correct spelling on the
///    finished transcript.
/// 2. <see cref="CommonMistranscriptions"/> - known mistranscription patterns
///    the user has observed in practice. Passed to the dictionary-correction
///    LLM, which replaces these exact wrong forms with the canonical term and
///    changes nothing else.
///
/// Profiles let the same dictionary serve multiple contexts. They control whether
/// dictionary correction runs at all (<see cref="DictationProfile.CleanupEnabled"/>) and
/// whether the unlisted fuzzy matcher may run on top of it
/// (<see cref="DictationProfile.FuzzyCorrectionEnabled"/>, default off); the correction
/// itself never rewrites, summarizes, or restyles the speaker's words.
/// </summary>
public sealed record DictationDictionary(
    IReadOnlyList<string> Vocabulary,
    IReadOnlyDictionary<string, IReadOnlyList<string>> CommonMistranscriptions,
    IReadOnlyDictionary<string, DictationProfile> Profiles)
{
    public static DictationDictionary Empty { get; } = new(
        Array.Empty<string>(),
        new Dictionary<string, IReadOnlyList<string>>(),
        new Dictionary<string, DictationProfile>());
}

/// <summary>
/// A named dictation profile. Two knobs, and the split between them matters:
///
/// <see cref="CleanupEnabled"/> - when false, the raw transcript is returned verbatim
/// with no correction at all. When true, the wrong forms the user LISTED in
/// <see cref="DictationDictionary.CommonMistranscriptions"/> are corrected. Those are safe
/// because the user chose them.
///
/// <see cref="FuzzyCorrectionEnabled"/> - whether the UNLISTED fuzzy matcher may also run.
/// It guesses from spelling similarity alone, with no sentence context and no signal that
/// the spoken word is already an ordinary word, so it rewrites ordinary English into
/// dictionary terms: "make sure" -> "make Soren", "explain the concept" -> "explain the
/// ConPty", "the screen is open" -> "the Soren is Soren". Measured against a real 28-term
/// glossary, 293 ordinary words out of a 22k-word corpus were silently rewritten. It is
/// therefore DEFAULT OFF and stays off until a judge that can read the sentence decides
/// each candidate (devthrottle_internal #1554).
///
/// Turning it on is a deliberate opt-in written into the glossary
/// (<c>fuzzy_correction_enabled: true</c> in YAML, <c>fuzzyCorrectionEnabled</c> over the
/// Gateway's dictionary JSON). Absent means OFF everywhere it is read, so every glossary
/// already in the field - all of which predate the key - gets the safe answer. Absent means
/// something different when a glossary is WRITTEN: the Gateway preserves whatever is on disk
/// rather than erasing it, so a save from a client that knows nothing about this setting
/// cannot switch it back off.
/// </summary>
public sealed record DictationProfile(
    string Name,
    bool CleanupEnabled,
    bool FuzzyCorrectionEnabled = false);
