using System.Text;
using System.Text.Json;

namespace CcDirector.Gateway.Rules;

/// <summary>The two answers there are to the second question. Nothing else is read as an answer.</summary>
public static class RuleOwnState
{
    /// <summary>The cited line is this session's own agent reporting this session's own state.</summary>
    public const string Own = "own";

    /// <summary>The cited line is something the session is merely displaying about something else.</summary>
    public const string Elsewhere = "elsewhere";
}

/// <summary>
/// The second question's answer, after it has been checked. Exactly one of <see cref="Verdict"/> and
/// <see cref="Refusal"/> is set, and an answer nobody could read is a refusal rather than a verdict.
/// </summary>
/// <param name="Verdict"><see cref="RuleOwnState.Own"/> or <see cref="RuleOwnState.Elsewhere"/>.</param>
/// <param name="Reason">Why, in the model's words. Required when the verdict is
/// <see cref="RuleOwnState.Elsewhere"/>, because that reason is what the refusal is recorded as.</param>
/// <param name="Refusal">Why the answer could not be read, or null when it was read.</param>
public sealed record RuleOwnStateReading(string? Verdict, string Reason, string? Refusal)
{
    /// <summary>An answer that was read.</summary>
    public static RuleOwnStateReading Read(string verdict, string reason) => new(verdict, reason, null);

    /// <summary>An answer that could not be read, with the words that go on the record.</summary>
    public static RuleOwnStateReading Refused(string refusal) => new(null, "", refusal);

    /// <summary>Whether this answer lets the act go ahead. TRUE ONLY for a verdict of
    /// <see cref="RuleOwnState.Own"/> that was actually read - the pass condition is a PRESENCE, so a
    /// refusal, an unreadable answer, or a model that never answered all stop the act rather than
    /// letting it through on an absence.</summary>
    public bool CanCarryAnAct => Refusal is null && Verdict == RuleOwnState.Own;
}

/// <summary>
/// THE SECOND QUESTION, ASKED ONLY WHEN THE FIRST ANSWER IS ACT (phase 1, the targeted fix).
///
/// The phase 1 measurement of 2026-09-04 found the fast model correct on eleven of the twelve real limit
/// screens and wrong on five of the twenty negatives - and the five were one confusion, not general
/// unreliability. Every one of them was a screen that TALKS ABOUT a limit rather than a screen where this
/// session has stopped on one: a report about another session that hit a spend limit, a banner saying 93
/// percent of an allowance is used on a session that is still working, a context limit which is a
/// different situation, a test fixture holding a provider error. The copied-line citation cannot catch any
/// of these, because in every case the line really is on the screen; whose state the line reports is a
/// different question, and it was never asked.
///
/// So it is asked, on its own, and only on the expensive side: an act pays one short call, a decline pays
/// nothing. The question carries no instruction and no rule - it is deliberately NOT a second chance to
/// re-judge whether the situation applies, which would just be the first question again and would trade
/// one model's opinion for the same model's opinion. It asks the one thing the first question is measurably
/// worst at: is the line that was cited this session reporting itself, or something it is displaying.
///
/// A REFUSAL STOPS THE ACT. The pass condition is an explicit verdict of <see cref="RuleOwnState.Own"/>.
/// An unreadable answer, a missing field, a value outside the two, or a model that did not answer at all
/// leaves the act refused and recorded, because a check whose pass condition is an absence certifies
/// nothing.
/// </summary>
public static class RuleOwnStateContract
{
    /// <summary>The name of the answer's one field. Named here so the prompt, the reader and anything that
    /// needs to tell this question from the judgement question all use the same string.</summary>
    public const string Field = "whose_state";

    /// <summary>
    /// Build the second question: the line that was cited, the screen it came off, and whose state it
    /// reports.
    /// </summary>
    /// <param name="quote">The line the first answer cited, already checked as being on the screen.</param>
    /// <param name="screenExcerpt">THE SAME EXCERPT the judgement question carried, so both questions are
    /// about one text.</param>
    /// <param name="facts">The session, when known. Only the agent is taken from it, exactly as in the
    /// judgement question: a screen means something relative to the agent that printed it.</param>
    public static string BuildPrompt(string quote, string screenExcerpt, RuleSessionFacts? facts = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Below is the tail of a coding session's terminal screen, and ONE line that has been");
        sb.AppendLine("cited from it. Answer one question about that line and nothing else.");
        sb.AppendLine();

        if (facts is not null && !string.IsNullOrWhiteSpace(facts.Agent))
        {
            sb.AppendLine($"The session is running the agent {facts.Agent}. Read the screen as that agent's screen.");
            sb.AppendLine();
        }

        sb.AppendLine("--- the line that was cited ---");
        sb.AppendLine(quote ?? "");
        sb.AppendLine("--- end of the cited line ---");
        sb.AppendLine();
        sb.AppendLine("--- the session's screen ---");
        sb.AppendLine(screenExcerpt ?? "");
        sb.AppendLine("--- end of screen ---");
        sb.AppendLine();

        sb.AppendLine("Whose state does that line report?");
        sb.AppendLine();
        sb.AppendLine($"Answer \"{RuleOwnState.Own}\" only when the line is this session's OWN agent reporting");
        sb.AppendLine("THIS session's OWN state right now - this session is itself stopped, blocked or waiting");
        sb.AppendLine("because of what the line says.");
        sb.AppendLine();
        sb.AppendLine($"Answer \"{RuleOwnState.Elsewhere}\" when the line is anything the session is merely");
        sb.AppendLine("displaying: documentation it is reading, code or a diff or a test or a fixture it is");
        sb.AppendLine("writing, a log, the output of a command it ran, a report or a summary about ANOTHER");
        sb.AppendLine("session or an earlier run, or a status banner about an allowance that has not actually");
        sb.AppendLine("stopped this session.");
        sb.AppendLine();
        sb.AppendLine($"If you cannot tell which it is, answer \"{RuleOwnState.Elsewhere}\".");
        sb.AppendLine();
        sb.AppendLine("Answer with JSON and nothing else, in exactly this shape:");
        sb.AppendLine("{");
        sb.AppendLine($"  \"{Field}\": \"{RuleOwnState.Own}\" or \"{RuleOwnState.Elsewhere}\",");
        sb.AppendLine("  \"reason\": \"why, in one sentence - never empty\"");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Read the second answer. Anything that is not one of the two verdicts is a refusal, in words that go
    /// straight onto the firing record.
    /// </summary>
    public static RuleOwnStateReading Read(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return RuleOwnStateReading.Refused(
                "the agent was asked whose state the cited line reports and gave no answer at all, so " +
                "nothing was typed.");

        var json = OnlyTheJson(raw);
        if (json is null)
            return RuleOwnStateReading.Refused(
                "the agent's answer about whose state the cited line reports was not the answer shape that " +
                "was asked for. It said: " + Shorten(raw));

        JsonElement root;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
            root = document.RootElement;
        }
        catch (JsonException ex)
        {
            return RuleOwnStateReading.Refused(
                "the agent's answer about whose state the cited line reports could not be read (" +
                ex.Message + "). It said: " + Shorten(raw));
        }

        using (document)
        {
            if (root.ValueKind != JsonValueKind.Object)
                return RuleOwnStateReading.Refused(
                    "the agent's answer about whose state the cited line reports was not the answer shape " +
                    "that was asked for. It said: " + Shorten(raw));

            var verdict = (RuleCallJson.Text(root, Field) ?? "").Trim().ToLowerInvariant();
            if (verdict != RuleOwnState.Own && verdict != RuleOwnState.Elsewhere)
                return RuleOwnStateReading.Refused(
                    $"the agent answered '{Shorten(verdict)}' about whose state the cited line reports, and " +
                    $"the only answers there are are '{RuleOwnState.Own}' and '{RuleOwnState.Elsewhere}'.");

            var reason = (RuleCallJson.Text(root, "reason") ?? "").Trim();

            // THE REASON IS REQUIRED ON THE SIDE THAT RECORDS ONE, AND ONLY THERE. An "elsewhere" verdict
            // refuses the act, and its reason is what the refusal is recorded as, so a blank one leaves a
            // record nobody could account for. An "own" verdict records nothing of its own - the act's
            // record is made of the judgement's reason and its citation - so requiring a sentence there
            // could only turn a correct, grounded act into a refusal over a missing field, which buys no
            // safety at all. Same asymmetry, and the same reason for it, as the citation on a decline.
            if (verdict == RuleOwnState.Elsewhere && reason.Length == 0)
                return RuleOwnStateReading.Refused(
                    "the agent answered that the cited line is not this session's own state and gave no " +
                    "reason for it, so nothing was typed and there is nothing to record.");

            return RuleOwnStateReading.Read(verdict, reason);
        }
    }

    /// <summary>The JSON out of a reply a chat model wrapped in prose or a fenced block, or null when there
    /// is none. The same deliberately narrow rule the judgement question uses: first '{' to last '}'.</summary>
    private static string? OnlyTheJson(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return raw[start..(end + 1)];
    }

    private static string Shorten(string? value)
    {
        var text = (value ?? "").Trim().ReplaceLineEndings(" ");
        return text.Length <= 200 ? text : text[..200] + "...";
    }
}
