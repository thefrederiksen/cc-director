using System.Text;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// What checking a stated reason against the screen it was given produced. <see cref="Statement"/> is never
/// empty - that is the point of it. A firing must always be able to say what the grounding check found,
/// including that there was nothing to check, so that a run in which the check NEVER RAN cannot look
/// identical to a run in which it ran and found nothing wrong.
/// </summary>
public sealed record RuleGrounding(bool IsGrounded, string Statement, IReadOnlyList<string> NotOnTheScreen);

/// <summary>
/// AN ACT'S REASON MUST BE GROUNDED IN THE SCREEN IT WAS GIVEN (Architect ruling A12).
///
/// This exists because of one live run. A rule declined, and its recorded reason quoted a sentence that was
/// not on the screen the firing record stores - the words had been on that session's screen twelve minutes
/// earlier, in an unrelated run. The decline was safe, because declining is the direction that does
/// nothing. THE SAME UNFAITHFULNESS IN THE OTHER DIRECTION IS A RULE ACTING ON EVIDENCE THAT WAS NOT
/// THERE, and that is the sharpest thing this mission has learned about its own design.
///
/// So: an ACT whose stated reason quotes text the screen does not contain is REFUSED. A DECLINE that does
/// the same is recorded as it happened - declining is safe and the record should show what actually
/// occurred - but recorded with the mismatch NOTED, so the unfaithfulness is visible rather than smoothed
/// over.
///
/// WHAT IT CHECKS, AND WHAT IT DOES NOT. It checks QUOTED passages: text the reason puts in quotation marks
/// is a claim about what the screen says, and that claim is checkable. It does not check paraphrase, and it
/// cannot - a reason that says the screen "looks like a limit notice" is a judgement, and judging the
/// judgement is what the agent was asked for in the first place. So this is a floor, not a proof of
/// faithfulness, and the report says so.
/// </summary>
public static class RuleReasonGrounding
{
    /// <summary>Quoted passages shorter than this are ignored. A reason that says the word 'act' in quotes
    /// is not making a claim about the screen, and a one-word match against forty lines of terminal output
    /// would be noise in both directions.</summary>
    public const int ShortestCheckablePassage = 8;

    /// <summary>The statement a firing carries when its reason is this Gateway's own words rather than the
    /// agent's - a refusal, or an abandonment. There is nothing to check, and saying so is not the same as
    /// saying nothing.</summary>
    public const string NotTheAgentsReason =
        "grounding: not applicable - this reason is the Gateway's own, not the agent's.";

    /// <summary>Check a stated reason against the screen it was given.</summary>
    public static RuleGrounding Check(string? reason, string? screenText)
    {
        var quoted = QuotedPassages(reason ?? "");
        if (quoted.Count == 0)
            return new RuleGrounding(
                true,
                "grounding: the reason quoted nothing from the screen, so there was nothing to check.",
                Array.Empty<string>());

        var screen = Flatten(screenText);
        var missing = quoted.Where(p => !screen.Contains(Flatten(p), StringComparison.Ordinal)).ToList();

        if (missing.Count == 0)
            return new RuleGrounding(
                true,
                $"grounding: {quoted.Count} quoted passage(s) checked against this screen, all found on it.",
                Array.Empty<string>());

        return new RuleGrounding(
            false,
            "grounding: the reason quotes text this screen does not contain: " +
            string.Join("; ", missing.Select(m => "'" + m.Trim() + "'")) + ".",
            missing);
    }

    /// <summary>The quoted passages in a piece of text, long enough to be a claim about the screen.</summary>
    internal static IReadOnlyList<string> QuotedPassages(string text)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(text)) return found;

        var pairs = new (char Open, char Close)[]
        {
            ('\'', '\''),
            ('"', '"'),
            // The typographic pair, written as escapes so this file stays pure ASCII. A model writing
            // prose uses them without being asked, and a quotation we could not recognise would read as a
            // reason that quoted nothing at all.
            ('\u2018', '\u2019'),
            ('\u201C', '\u201D'),
        };

        foreach (var (open, close) in pairs)
        {
            var index = 0;
            while (index < text.Length)
            {
                var start = text.IndexOf(open, index);
                if (start < 0) break;
                var end = text.IndexOf(close, start + 1);
                if (end < 0) break;
                var passage = text[(start + 1)..end];
                if (passage.Trim().Length >= ShortestCheckablePassage) found.Add(passage);
                index = end + 1;
            }
        }
        return found;
    }

    /// <summary>Whitespace collapsed and case dropped, so a quotation that crossed a line wrap on the
    /// terminal is still recognised as the same words.</summary>
    internal static string Flatten(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }
            sb.Append(char.ToLowerInvariant(ch));
            lastWasSpace = false;
        }
        return sb.ToString().Trim();
    }
}
