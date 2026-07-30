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
    /// The language for a code, or NULL when it names no language this product speaks.
    ///
    /// THIS USED TO ANSWER ENGLISH FOR ANYTHING IT DID NOT RECOGNIZE, and that single line was the most dangerous
    /// in the mission (re-audit). The reasoning was that degrading to speech beats silence - a code written by a
    /// newer Gateway should not take somebody's voice away. What it actually did was launder every unknown code
    /// into a confident English answer: a Gateway replying <c>{"language":"de"}</c> handed the desktop a
    /// perfectly valid ENGLISH utterance, and it spoke. The caller could not tell that from a real answer,
    /// because there was nothing to tell it with.
    ///
    /// So an unknown code is now an ABSENCE, and every caller has to say what it does about it. They refuse.
    /// Refusing is not the silence the old comment feared: the product says why, in a log and on a screen, which
    /// is recoverable. Speaking the wrong language is not, because nobody finds out.
    /// </summary>
    public static SpokenLanguage? TryResolve(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var trimmed = code.Trim();
        foreach (var language in All)
        {
            if (string.Equals(language.Code, trimmed, StringComparison.OrdinalIgnoreCase))
                return language;
        }
        return null;
    }

    /// <summary>
    /// The language for a code, or a THROW naming the code when it is not one we speak.
    ///
    /// For the callers that already know the code came from a validated write - the settings store, whose write
    /// path refuses an unknown code - so an unknown one here means data corruption or a rollback past a language
    /// we used to offer. That is a real failure and it fails loudly, with the code in the message. It does not
    /// pretend the account speaks English.
    /// </summary>
    /// <exception cref="ArgumentException">The code names no language this product speaks.</exception>
    public static SpokenLanguage Require(string? code)
        => TryResolve(code) ?? throw new ArgumentException(
            $"'{code}' is not a language DevThrottle speaks. Known: "
            + string.Join(", ", All.Select(l => l.Code)) + ". This is not defaulted to English on purpose: an "
            + "account silently spoken to in the wrong language is the failure this mission exists to remove.",
            nameof(code));

    /// <summary>Whether <paramref name="code"/> names a language this product speaks. Used by the
    ///  settings WRITE path, which must REFUSE an unknown code rather than quietly storing one - a
    ///  write is a person making a choice, and a choice we cannot honour has to fail where they can
    ///  see it. Reads degrade (see <see cref="Resolve"/>); writes do not.</summary>
    public static bool IsSupported(string? code)
        => !string.IsNullOrWhiteSpace(code)
           && All.Any(l => string.Equals(l.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
}
