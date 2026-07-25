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
    /// The languages we offer, code to (English name, endonym). The English name is what the
    /// wingman prompt says; the endonym is included because naming a language in its own words
    /// measurably helps a model commit to it, and because the settings UI should show people
    /// their own language the way they write it.
    ///
    /// This list is the OFFER, not a capability claim - whether a given speech model can actually
    /// say one of these is the model's <c>languages</c> array, checked separately. Keep the two
    /// apart: offering a language no model can speak produces confident gibberish.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string English, string Endonym)> Supported =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = ("English", "English"),
            ["da"] = ("Danish", "dansk"),
            ["de"] = ("German", "Deutsch"),
            ["fr"] = ("French", "francais"),
            ["es"] = ("Spanish", "espanol"),
            ["pt"] = ("Portuguese", "portugues"),
            ["it"] = ("Italian", "italiano"),
            ["nl"] = ("Dutch", "Nederlands"),
            ["sv"] = ("Swedish", "svenska"),
            ["no"] = ("Norwegian", "norsk"),
            ["ja"] = ("Japanese", "Nihongo"),
            ["tr"] = ("Turkish", "Turkce"),
        };

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
        var want = Normalize(code);
        if (modelLanguages is null) return string.Equals(want, Default, StringComparison.Ordinal);
        var known = modelLanguages
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim().ToLowerInvariant())
            .ToList();
        if (known.Count == 0) return string.Equals(want, Default, StringComparison.Ordinal);
        return known.Contains(want);
    }
}
