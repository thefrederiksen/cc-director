namespace CcDirector.Gateway.Rules;

/// <summary>
/// ONE NORMALISER FOR A TRIGGER WORD, AND ONE GROUNDING CHECK (fix round D, ruling D2).
///
/// The word that was checked against the screen and the word that is stored have to be the same string,
/// and the only way to guarantee that is for there to be one function that produces it. Before this type
/// the draft reader checked the model's word as written and the store trimmed it on the way in, so a
/// padded word was checked narrow and stored wide - two functions that happened to agree on every word
/// that had no padding.
///
/// The grounding check lives here for the same reason: the draft route runs it before a rule is offered
/// and the write route runs it again before a rule is stored, and a check that exists twice is a check
/// that can be different twice.
/// </summary>
public static class RuleTriggerWords
{
    /// <summary>The form in which a trigger word is checked and stored.</summary>
    public static string Normalise(string? word) => (word ?? "").Trim();

    /// <summary>Every word in its checked-and-stored form, with the empty ones gone.</summary>
    public static IReadOnlyList<string> NormaliseAll(IEnumerable<string>? words) =>
        (words ?? Array.Empty<string>()).Select(Normalise).Where(w => w.Length > 0).ToList();

    /// <summary>The words that are NOT on the excerpt, in the order they were given.</summary>
    public static IReadOnlyList<string> NotOn(IEnumerable<string>? words, string excerpt)
    {
        var text = excerpt ?? "";
        return NormaliseAll(words).Where(w => !text.Contains(w, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Why the words are not grounded in this screen, or null when every one of them is on it. The check
    /// ignores case because the matching it guards ignores case; a guard stricter than the thing it guards
    /// refuses rules that would have worked.
    /// </summary>
    /// <param name="words">The trigger words, in any form; they are normalised here.</param>
    /// <param name="screen">The screen they have to be on.</param>
    /// <param name="whichScreen">How to name the screen in the refusal, for example "the screen you
    /// captured" or "that session's screen right now".</param>
    public static string? WhyNotGrounded(IEnumerable<string>? words, RuleScreenReading screen, string whichScreen)
    {
        if (screen is null) throw new ArgumentNullException(nameof(screen));
        var invented = NotOn(words, screen.Excerpt);
        if (invented.Count == 0) return null;
        return "the rule watches for words that are not on " + whichScreen + ": " +
               string.Join(", ", invented.Select(w => "\"" + w + "\"")) + ". A rule only ever looks at a " +
               "screen, so a word that is not there is a rule that never fires.";
    }
}
