using System.Globalization;
using System.Text;

namespace CcDirector.Gateway.CarMode;

/// <summary>
/// The spoken-confirmation words for a destructive Car Mode action (Car Mode mission, decision 3:
/// deleting or killing a session always requires a spoken confirmation). Pure and exhaustively tested,
/// because it gates an irreversible action: a delete only proceeds on a clear affirmative, and anything
/// ambiguous is treated as NOT confirmed (negatives win), so an accidental word never destroys a session.
///
/// IT IS DELIBERATELY LANGUAGE-BLIND (issue #1009), and that is a stronger design than making it
/// language-aware. Until this change the word lists were English only, so an account being spoken to in
/// French was asked - in French - to confirm, said "oui", and was not understood. Nothing was deleted,
/// which is the safe direction, but the owner was left saying yes to a machine that kept not hearing it.
///
/// The fix is not to pass the language in. Every word from every language we speak is matched at once,
/// for two reasons:
///
///  1. THERE IS NOTHING TO FORGET. A gate that took a language would be a fifth place a language has to
///     reach, and this whole mission exists because the fourth place did not get one. A gate that needs
///     no language cannot be given the wrong one.
///  2. IT IS RIGHT AT THE EDGES. People code-switch, especially under one-word pressure: a French
///     speaker says "ok" and an English speaker who set the product to Spanish still says "yes". Both
///     are unambiguous, and refusing them because a setting says otherwise would be pedantry with a
///     confirmation prompt attached. Negatives still win over affirmatives, so widening the lists can
///     only ever make a destructive action LESS likely to run, never more.
///
/// Accents are stripped for MATCHING only, on both sides. That is not the accents ruling being ignored -
/// it is the opposite of the case that ruling covers. Here the text is INPUT from a transcriber, which
/// may hand back "si" or "s&#237;" for the same spoken word; there is nothing to pronounce and nothing
/// to speak, so folding the difference away makes the match more robust. Accents matter where the
/// product SPEAKS - see SpokenPhrases.
/// </summary>
public static class CarModeConfirm
{
    /// <summary>
    /// Clear affirmatives, in every language the product speaks. Written WITHOUT accents because
    /// <see cref="Normalize"/> strips them from the incoming text before comparing - so "si" here
    /// matches a transcriber's "s&#237;", and "confirme" matches "confirm&#233;".
    /// </summary>
    private static readonly string[] Affirmatives =
    {
        // English
        "yes", "yeah", "yep", "yup", "confirm", "confirmed", "do it", "go ahead", "affirmative",
        "correct", "sure", "please do", "proceed", "okay do it", "ok do it",
        // French
        "oui", "ouais", "confirme", "confirmer", "vas y", "allez y", "d accord", "daccord",
        "exact", "c est ca", "fais le", "faites le", "je confirme",
        // Spanish
        "si", "claro", "confirmo", "confirmar", "adelante", "hazlo", "de acuerdo", "correcto",
        "por supuesto", "vale",
    };

    /// <summary>
    /// Negatives, in every language the product speaks. Negatives WIN over affirmatives, so a word here
    /// can only ever stop a destructive action - which is why this list is the safe one to be generous
    /// with.
    /// </summary>
    private static readonly string[] Negatives =
    {
        // English
        "no", "nope", "cancel", "stop", "wait", "don't", "do not", "dont", "never mind", "nevermind",
        "negative", "forget it", "abort", "leave it",
        // French
        "non", "annule", "annuler", "arrete", "arretez", "attends", "attendez", "surtout pas",
        "laisse", "laissez", "oublie", "oubliez", "pas du tout",
        // Spanish
        "cancela", "cancelar", "para", "espera", "espere", "olvidalo", "dejalo", "para nada",
        "de ninguna manera",
    };

    /// <summary>
    /// Lower-case, strip diacritics, and reduce everything that is not a letter or a digit to a single
    /// space, so the word lists above can be plain ASCII and still match accented speech.
    ///
    /// The old version reduced anything outside <c>[a-z0-9]</c> to a space, which deleted accented
    /// letters outright: "confirm&#233;" became "confirm " and "s&#237;" became "s ". Stripping the
    /// diacritic FIRST keeps the letter.
    /// </summary>
    private static string Normalize(string raw)
    {
        var lowered = (raw ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(lowered.Length);
        var lastWasSpace = false;
        foreach (var ch in lowered)
        {
            // FormD split each accented letter into its base letter plus a combining mark; drop the mark
            // and keep the letter.
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); lastWasSpace = false; continue; }
            if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
        }
        return sb.ToString().Trim();
    }

    /// <summary>True when the text is a clear affirmative AND carries no negative (negatives win, so a
    ///  destructive action never proceeds on a mixed or ambiguous answer).</summary>
    public static bool IsAffirmative(string text)
    {
        var t = " " + Normalize(text) + " ";
        if (t.Trim().Length == 0) return false;
        if (ContainsAny(t, Negatives)) return false;
        return ContainsAny(t, Affirmatives);
    }

    /// <summary>True when the text carries a negative / cancel word.</summary>
    public static bool IsNegative(string text)
    {
        var t = " " + Normalize(text) + " ";
        return ContainsAny(t, Negatives);
    }

    // Whole-word/phrase match: the padded text contains " phrase " so "no" does not match inside "north".
    // The phrases are normalized the same way as the text, so an entry written with punctuation (like
    // "don't") is compared in the form the normalizer produces ("don t").
    private static bool ContainsAny(string paddedText, string[] phrases)
    {
        foreach (var phrase in phrases)
        {
            if (paddedText.Contains(" " + Normalize(phrase) + " ", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
