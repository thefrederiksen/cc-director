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
/// Profiles let the same dictionary serve multiple contexts. The only knob is
/// whether dictionary correction runs at all (see
/// <see cref="DictationProfile.CleanupEnabled"/>); the correction itself never
/// rewrites, summarizes, or restyles the speaker's words.
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
/// A named dictation profile. <see cref="CleanupEnabled"/> is the only knob:
/// when true, the dictionary-correction LLM pass runs (dictionary terms only,
/// never rewording); when false, the raw transcript is returned verbatim with
/// no correction at all.
/// </summary>
public sealed record DictationProfile(
    string Name,
    bool CleanupEnabled);
