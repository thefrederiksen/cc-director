namespace CcDirector.Gateway.CarMode;

/// <summary>
/// The spoken-confirmation words for a destructive Car Mode action (Car Mode mission, decision 3:
/// deleting or killing a session always requires a spoken confirmation). Pure and exhaustively tested,
/// because it gates an irreversible action: a delete only proceeds on a clear affirmative, and anything
/// ambiguous is treated as NOT confirmed (negatives win), so an accidental word never destroys a session.
/// </summary>
public static class CarModeConfirm
{
    private static readonly string[] Affirmatives =
    {
        "yes", "yeah", "yep", "yup", "confirm", "confirmed", "do it", "go ahead", "affirmative",
        "correct", "sure", "please do", "proceed", "okay do it", "ok do it",
    };

    private static readonly string[] Negatives =
    {
        "no", "nope", "cancel", "stop", "wait", "don't", "do not", "dont", "never mind", "nevermind",
        "negative", "forget it", "abort", "leave it",
    };

    private static string Normalize(string raw) =>
        System.Text.RegularExpressions.Regex.Replace(raw.ToLowerInvariant(), "[^a-z0-9\\s]", " ")
            .Replace("  ", " ").Trim();

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
    private static bool ContainsAny(string paddedText, string[] phrases)
    {
        foreach (var phrase in phrases)
        {
            if (paddedText.Contains(" " + phrase + " ", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
