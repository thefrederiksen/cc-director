namespace CcDirector.Gateway.Speech;

/// <summary>
/// The languages this product speaks, and the ONE place a stored code becomes a
/// <see cref="SpokenLanguage"/> (issue #1008).
///
/// The list is what we have DECIDED TO SELL, not what an engine is capable of. That distinction is
/// the first defect in the post-mortem (devthrottle_internal#547): the reverted build populated its
/// dropdown from the speech engine's capability list and offered twelve languages nobody had checked,
/// verified or priced. Three are offered here because three have measured voices serving them -
/// English (28 voices), French (exactly one, <c>ff_siwis</c>), and Spanish (three). Adding a fourth
/// is an edit HERE, made deliberately, after its voices are measured - never a list that grows on its
/// own because an engine learned a new trick.
/// </summary>
public static class SpokenLanguages
{
    /// <summary>English - the default, and what every account that has never opened the Language tab
    ///  speaks.</summary>
    public static readonly SpokenLanguage English = new("en", "English", "English");

    /// <summary>French. One voice (<c>ff_siwis</c>) - real asymmetry the settings screen must cope
    ///  with, not a gap to hide.</summary>
    public static readonly SpokenLanguage French = new("fr", "French", "Francais");

    /// <summary>Spanish. Three voices (<c>ef_dora</c>, <c>em_alex</c>, <c>em_santa</c>).</summary>
    public static readonly SpokenLanguage Spanish = new("es", "Spanish", "Espanol");

    /// <summary>Every language the product speaks, English first because it is the default.</summary>
    public static readonly IReadOnlyList<SpokenLanguage> All = new[] { English, French, Spanish };

    /// <summary>The language an account speaks when it has expressed no choice: English. This is the
    ///  documented default, not a fallback - an account that never opens the Language tab gets exactly
    ///  what every account got before the tab existed.</summary>
    public static SpokenLanguage Default => English;

    /// <summary>
    /// The language for a stored code. An unknown, blank, or missing code reads as
    /// <see cref="Default"/>, and that direction is deliberate: the two ways to be wrong are not
    /// symmetric. Speaking English to someone who chose French is visible to them and one setting away
    /// from being fixed; throwing on an unrecognized code would take the voice away entirely and leave
    /// them with silence and no way back. It also makes a Gateway rollback safe - a code written by a
    /// newer Gateway that this one does not know yet degrades to speech, not to a broken turn.
    /// </summary>
    public static SpokenLanguage Resolve(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return Default;
        var trimmed = code.Trim();
        foreach (var language in All)
        {
            if (string.Equals(language.Code, trimmed, StringComparison.OrdinalIgnoreCase))
                return language;
        }
        return Default;
    }

    /// <summary>Whether <paramref name="code"/> names a language this product speaks. Used by the
    ///  settings WRITE path, which must REFUSE an unknown code rather than quietly storing one - a
    ///  write is a person making a choice, and a choice we cannot honour has to fail where they can
    ///  see it. Reads degrade (see <see cref="Resolve"/>); writes do not.</summary>
    public static bool IsSupported(string? code)
        => !string.IsNullOrWhiteSpace(code)
           && All.Any(l => string.Equals(l.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
}
