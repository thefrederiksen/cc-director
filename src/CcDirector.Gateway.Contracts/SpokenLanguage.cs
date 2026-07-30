namespace CcDirector.Gateway.Speech;

/// <summary>
/// One language the product SPEAKS. Not a locale, not a translation of the user interface - the
/// language every spoken path talks in for one account (issue #1008).
///
/// A language is a VOICE, never an engine. The previous attempt at this (reverted by product pull
/// request 2181, post-mortem devthrottle_internal#547) made choosing a language switch the speech
/// MODEL to a multilingual engine, and that engine could not say the lengths this product writes:
/// French returned silence at 155 characters, Spanish blew a sixty-second deadline at 208, and the
/// wingman is tuned to write about 500. Nothing in this type, or anywhere downstream of it, selects
/// a speech model. French and Spanish are voices inside the same engine that already serves English,
/// measured on 2026-07-29 at 1.31 s and 1.24 s for a ~500-character narration against English's
/// 1.29 s. If a future change starts branching on <see cref="Code"/> to pick a model, that is the
/// reverted failure returning.
/// </summary>
/// <param name="Code">The short language code stored per account and carried on the wire:
///  <c>en</c>, <c>fr</c>, <c>es</c>. Lower case, and the only form persisted.</param>
/// <param name="EnglishName">The language's name in English - the word the SPOKEN OUTPUT CONTRACT
///  puts in front of the model ("SPEAK ENTIRELY IN FRENCH").</param>
/// <param name="NativeName">The language's name in its own language, for the settings screen. Held
///  to plain ASCII deliberately: it is a label in a user interface and a log line, not spoken
///  content, and the repository's output rule is ASCII everywhere. Spoken CONTENT is a different
///  thing and carries its own accents.</param>
public sealed record SpokenLanguage(string Code, string EnglishName, string NativeName)
{
    // A LANGUAGE IS VALID ONLY IF IT IS ONE WE KNOW (re-audit, the one root cause).
    //
    // The first version of this check tested that each part was NON-EMPTY, and non-empty is not valid. The gap
    // between those two words is the whole mission: `new SpokenLanguage("zz", "Unknown", "Unknown")` passed, and
    // every downstream check that asked "is there a language?" said yes. So the code is checked against the
    // codes this product actually speaks.
    //
    // THE CODE LIST LIVES HERE, not in SpokenLanguages, and that is deliberate rather than awkward: if the
    // instances validated themselves against the collection that CONSTRUCTS them, the static initializer would
    // recurse. So the codes are the authority, the instances are built from them, and a test asserts the two
    // cannot drift apart - a language in one and not the other fails the build.

    /// <summary>The codes this product speaks. The authority: nothing else may name a language.</summary>
    internal static readonly IReadOnlySet<string> KnownCodes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "en", "fr", "es" };

    /// <summary>The short language code stored per account and carried on the wire. Lower case, and the only
    ///  form persisted.</summary>
    public string Code { get; init; } = RequireKnownCode(Code);

    /// <summary>The language's name in English, as the spoken output contract states it.</summary>
    public string EnglishName { get; init; } = RequireText(EnglishName, nameof(EnglishName));

    /// <summary>The language's name in its own language, for the settings screen.</summary>
    public string NativeName { get; init; } = RequireText(NativeName, nameof(NativeName));

    private static string RequireKnownCode(string code)
    {
        var trimmed = (code ?? "").Trim();
        if (!KnownCodes.Contains(trimmed))
            throw new ArgumentException(
                $"'{code}' is not a language DevThrottle speaks. Known: {string.Join(", ", KnownCodes)}. A blank "
                + "or unrecognized code would satisfy every 'is there a language?' check downstream while naming "
                + "no language at all, which is how words get spoken in the wrong one.", nameof(code));
        return trimmed.ToLowerInvariant();
    }

    private static string RequireText(string value, string part)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"A spoken language needs a {part}.", part)
            : value.Trim();
}
