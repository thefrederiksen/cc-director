namespace CcDirector.Core.Configuration;

/// <summary>
/// The language DevThrottle SPEAKS BACK in - the tenant's "spoken language".
///
/// This is deliberately not "the language setting". Dictation already understands every language
/// on its own: the transcription upload sends no language field at all, so the provider detects
/// it. The only direction that needs a choice is the one going out - what the wingman writes and
/// what the speech model says.
///
/// Codes are BCP-47 primary subtags, matching the <c>languages</c> array each speech model
/// publishes in the DevThrottle catalog, so a language can be checked against a model's coverage
/// without any mapping in between.
/// </summary>
public static class SpokenLanguage
{
    /// <summary>The config.json key, and the tenant-settings key, holding the chosen language.</summary>
    public const string ConfigKey = "spoken_language";

    /// <summary>
    /// English. The default for every account that has never touched the setting, so nothing
    /// changes for anyone who does not want this.
    /// </summary>
    public const string Default = "en";

    /// <summary>
    /// The languages we offer, IN THE ORDER THEY ARE SHOWN. Five, not more.
    ///
    /// This is the OFFER, and it is a commercial decision, not a capability list. The speech engine
    /// can say 23 languages; we sell five, because each one we list has to be translated, supported
    /// and kept current on a product that ships weekly. An earlier version of this file listed
    /// twelve - everything the engine could manage - which is exactly the capability-for-offer
    /// mistake the note below warns against. Adding a language here is a decision, not a courtesy.
    ///
    /// The order is deliberate: English first because it is the default and the language everything
    /// is authored in; then the three market languages alphabetically; then Danish last, because it
    /// is carried for testing the pipeline rather than as a market, and it should not sit above a
    /// language somebody might actually buy in.
    ///
    /// Whether a given speech model can SAY one of these is a separate question, answered by the
    /// model's own languages list. Keep the two apart: offering a language no model can speak
    /// produces confident gibberish.
    /// </summary>
    public static readonly IReadOnlyList<(string Code, string English, string Endonym)> Offered =
        new[]
        {
            ("en", "English", "English"),
            ("fr", "French", "francais"),
            ("de", "German", "Deutsch"),
            ("es", "Spanish", "espanol"),
            ("da", "Danish", "dansk"),
        };

    /// <summary>The offered languages by code, for lookup. <see cref="Offered"/> holds the order.</summary>
    public static readonly IReadOnlyDictionary<string, (string English, string Endonym)> Supported =
        Offered.ToDictionary(l => l.Code, l => (l.English, l.Endonym), StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="code"/> is a language we offer.</summary>
    public static bool IsSupported(string? code) =>
        !string.IsNullOrWhiteSpace(code) && Supported.ContainsKey(code.Trim());

    /// <summary>
    /// Normalize a stored value to a code we offer, falling back to <see cref="Default"/>.
    /// A stored language we no longer offer must not break speech - English always works.
    /// </summary>
    public static string Normalize(string? code) =>
        IsSupported(code) ? code!.Trim().ToLowerInvariant() : Default;

    /// <summary>True when this is the default (English) and no language instruction is needed.</summary>
    public static bool IsDefault(string? code) =>
        string.Equals(Normalize(code), Default, StringComparison.Ordinal);

    /// <summary>
    /// The display name for a code: "Danish (dansk)", or the raw code if we do not know it.
    /// </summary>
    public static string DisplayName(string? code)
    {
        var norm = Normalize(code);
        if (!Supported.TryGetValue(norm, out var names)) return norm;
        return names.English == names.Endonym
            ? names.English
            : $"{names.English} ({names.Endonym})";
    }

    /// <summary>
    /// The instruction that makes a model answer in this language, or an empty string for English.
    ///
    /// Lives here, in ONE place, because there is not one thing that speaks - there are several, and
    /// they are separate generators: turn narration, the direct "talk to the wingman" reply, the
    /// about-DevThrottle answer, and Car Mode's hands-free voice. Applying the language to only one
    /// of them is the defect this method exists to prevent: the owner set Danish, heard English, and
    /// was right to say the setting had not taken - narration had been translated and conversation
    /// had not.
    ///
    /// Naming what must NOT be translated matters as much as naming the language. A listener has to
    /// be able to type what they hear, so identifiers, paths, commands and error text stay verbatim.
    /// </summary>
    public static string PromptInstruction(string? code)
    {
        if (IsDefault(code)) return string.Empty;
        var name = EnglishName(code);
        return
            $"LANGUAGE. Answer in {name}. Whatever language the material you are given is in - usually " +
            $"English - your spoken answer is in {name}, written the way a native speaker would say it " +
            "out loud rather than as a word-for-word translation.\n" +
            "Leave these EXACTLY as they appear, never translated and never spelled differently: file " +
            "names and paths, code identifiers, command names, branch names, and error text. Everything " +
            $"else is spoken in {name}.\n\n";
    }

    /// <summary>The English name alone - what the wingman prompt names the target language.</summary>
    public static string EnglishName(string? code) =>
        Supported.TryGetValue(Normalize(code), out var names) ? names.English : Normalize(code);

    /// <summary>
    /// Whether a speech model advertising <paramref name="modelLanguages"/> can say
    /// <paramref name="code"/>. A model that publishes no languages is treated as English-only -
    /// the safe direction, because the alternative is letting a model be picked for a language it
    /// cannot pronounce.
    /// </summary>
    public static bool ModelCanSpeak(IEnumerable<string>? modelLanguages, string? code)
    {
        // NOT normalized. This asks a question about the MODEL, not about our offer: "can this engine
        // say xx?" is a fact independent of whether we sell xx. Normalizing first was a real bug -
        // it turned a question about an unoffered language into a question about English, and any
        // model that speaks English answered yes.
        var want = (code ?? "").Trim().ToLowerInvariant();
        if (want.Length == 0) return false;
        if (modelLanguages is null) return want == Default;
        var known = modelLanguages
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim().ToLowerInvariant())
            .ToList();
        if (known.Count == 0) return want == Default;
        return known.Contains(want);
    }
}
