using System.Text;
using System.Text.Json;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// One reply from the agent, after every part of it has been checked against what was actually offered.
/// Nothing reaches this record that was not validated: the rule was a candidate this pass and the decision
/// is one of the two.
///
/// THERE IS NO TEXT TO TYPE ON THIS RECORD, AND THERE MUST NEVER BE ONE (phase 1). The text a rule types is
/// decided when the rule is written, confirmed by a person, and stored with the rule; the evaluator types
/// <see cref="SessionRule.TextToType"/> and nothing else. A field here for text would be a path by which a
/// model composes a keystroke at run time, which is the thing this phase removed. <c>RulesAgentReplyGuardTests</c>
/// asserts against the built assembly that this record carries exactly these four parts.
/// </summary>
/// <param name="RuleId">The one instruction the answer is about.</param>
/// <param name="Decision">Act or decline. Nothing else is read as a decision.</param>
/// <param name="Quote">ONE line copied from the screen, as the model wrote it - the citation an act has to
/// carry (Architect ruling A12, asked for as a field by ruling P1-A). Empty when the model gave none. It
/// is checked against the excerpt the model was shown by <see cref="RuleReasonGrounding.CheckQuote"/>;
/// this record does not say whether it is on the screen, only what was cited.</param>
/// <param name="Reason">Why, in the model's words. Required for both decisions, because the firing record
/// is made of it and a record with no reason is a record of nothing.</param>
public sealed record RuleAgentReply(
    Guid RuleId,
    string Decision,
    string Quote,
    string Reason);

/// <summary>A reply, or a stated refusal. Exactly one of the two is set.</summary>
public sealed record RuleAgentReading(RuleAgentReply? Reply, string? Refusal)
{
    /// <summary>A reply that passed every check.</summary>
    public static RuleAgentReading Accepted(RuleAgentReply reply) => new(reply, null);

    /// <summary>A refusal, with the reason that goes on the record.</summary>
    public static RuleAgentReading Refused(string reason) => new(null, reason);
}

/// <summary>
/// THE ONE AGENT CALL (Architect ruling A5). One question per screen covering EVERY candidate rule - not
/// one call per rule - and a reply whose every part is validated against what was offered.
///
/// THE QUESTION IS YES OR NO, PLUS ONE COPIED LINE (phase 1). Through phase 2 this asked for an
/// understanding, a decision, a reason, a list of checks and the text to type - about 600 characters of
/// JSON - and the phase 0 harness measured what that cost on 32 real screens: the thinking model timed out
/// on nine of the twelve real limit screens, the fast model pattern-matched the words on seven of twenty
/// negatives, and neither model ever quoted the screen, so the grounding check refused every act on every
/// real limit screen. The engine as shipped never acted on the case it was built for.
///
/// So the question is now the one that was always meant: is this screen the situation the instruction is
/// about? The text to type is on the rule, decided at authoring and confirmed by a person, and is never
/// asked for here. The checks are on the rule too, so there is nothing for a model to invent an argument
/// for. What IS asked for, as its own named field, is one line copied from the screen - the citation an act
/// must carry so that a person can go back and check it (ruling A12, kept exactly; ruling P1-A changed only
/// the way it is asked for). It is checked against the very excerpt this question carried.
///
/// WHAT IS OFFERED IS WHAT MAY BE NAMED. The question carries the candidate rules by id and the account's
/// own sentence for each; coming back, an id that was not a candidate is refused and a decision outside
/// the closed set is refused.
///
/// A REFUSAL IS NOT A DECISION. An answer nobody can read never degrades into "do nothing quietly": it
/// comes back as a stated reason, and the evaluator writes it down against every rule that was in play.
/// A model that mumbles must never be read as permission to type into somebody's session.
/// </summary>
public static class RuleAgentContract
{
    /// <summary>
    /// THE SAMPLING TEMPERATURE THE RUN-TIME QUESTION IS ASKED AT: zero. Measured on 3 September 2026 with
    /// the provider's default, the fast model answered the same negative screen "decline" on one run and
    /// "act" on the next, and a rule whose verdict on one screen is a dice roll would type on the idle
    /// transition it happened to land on. A judgement about a fixed screen should be the same judgement
    /// every time. Whether the hosted endpoint honours the setting is measured by the screen harness, which
    /// runs every case several times and reports the flip rate; it is not assumed here.
    /// </summary>
    public const double JudgementTemperature = 0.0;

    /// <summary>How many lines of the screen tail the question carries. The excerpt itself is produced by
    /// <see cref="RuleScreenExcerpt.Of"/> - the one function the authoring path uses too - so this is the
    /// same number as <see cref="RuleScreenExcerpt.Lines"/> and is kept for the callers that name it.</summary>
    public const int ScreenTailLines = RuleScreenExcerpt.Lines;

    /// <summary>
    /// Build the one question for this screen: what a rule is, the rules in play, the screen excerpt, and
    /// the exact shape of the answer.
    /// </summary>
    /// <param name="candidates">The rules in play, every one with the text it types already stored.</param>
    /// <param name="screenExcerpt">THE EXACT TEXT the model is shown - <see cref="RuleScreenExcerpt.Of"/>
    /// of the screen - which is also the text the quote is checked against. One string, produced once, so
    /// the prompt and the check cannot see different lengths of the same screen.</param>
    /// <param name="facts">The session being judged, when known. ONLY the agent is taken from it: a screen
    /// means something relative to the agent that printed it, so the judge is told which one. The machine,
    /// the repository and the clock are NOT put in the question - ruling A11 says the screen is the only
    /// input to the decision, and <c>RuleEvaluatorTests</c> asserts the machine name stays out. The agent
    /// does not decide WHETHER the instruction applies (scope already removed every other agent's session
    /// before this was asked); it decides how the screen is read.</param>
    public static string BuildPrompt(
        IReadOnlyList<SessionRule> candidates, string screenExcerpt, RuleSessionFacts? facts = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("An account has given you standing instructions about its coding sessions. One of its");
        sb.AppendLine("sessions has just gone idle. Below is the tail of that session's terminal screen, and the");
        sb.AppendLine("instructions that might apply to it.");
        sb.AppendLine();
        sb.AppendLine("Answer ONE question: is this screen the situation one of these instructions is about?");
        sb.AppendLine("The INSTRUCTION IS THE AUTHORITY. It names a situation, and the question is whether the");
        sb.AppendLine("session is IN that situation right now - its own agent reporting its own state, at the");
        sb.AppendLine("bottom of its own screen. If the words merely appear in something the session is reading,");
        sb.AppendLine("writing, quoting, summarising or discussing - documentation, code, a diff, a log, a test,");
        sb.AppendLine("a report about some other session - then the session is not in that situation: DECLINE");
        sb.AppendLine("and say why. If the screen shows a similar but different situation from the one the");
        sb.AppendLine("instruction names, decline: the instruction is about the situation it describes and not");
        sb.AppendLine("the family of situations it belongs to. Declining is a correct and expected answer, not");
        sb.AppendLine("a failure.");
        sb.AppendLine();

        sb.AppendLine("--- the standing instructions in play ---");
        foreach (var rule in candidates ?? Array.Empty<SessionRule>())
        {
            sb.AppendLine($"rule_id: {rule.Id}");
            sb.AppendLine($"  the account said: {rule.Instruction}");
            sb.AppendLine($"  what it is watching for: {rule.ScreenDescription}");
        }
        sb.AppendLine("--- end of instructions ---");
        sb.AppendLine();

        if (facts is not null && !string.IsNullOrWhiteSpace(facts.Agent))
        {
            sb.AppendLine($"The session is running the agent {facts.Agent}. Read the screen as that agent's screen.");
            sb.AppendLine();
        }

        sb.AppendLine("--- the session's screen ---");
        sb.AppendLine(screenExcerpt ?? "");
        sb.AppendLine("--- end of screen ---");
        sb.AppendLine();

        sb.AppendLine("Act only when the screen plainly shows the session itself in the situation the instruction");
        sb.AppendLine("names. If it could be read either way, decline.");
        sb.AppendLine();
        sb.AppendLine("Answer with JSON and nothing else, in exactly this shape. Every field is filled in:");
        sb.AppendLine("{");
        sb.AppendLine("  \"rule_id\": \"the id of the ONE instruction you are answering about\",");
        sb.AppendLine($"  \"decision\": \"{RuleDecisions.Act}\" or \"{RuleDecisions.Decline}\",");
        sb.AppendLine("  \"reason\": \"why, in one sentence - never empty\",");
        // WORDS, NOT GLYPHS. A real terminal line often starts with spinner glyphs and redraw fragments,
        // and a model asked to reproduce the whole line mangles them - measured on the first smoke run of
        // this contract, where the fast model turned the prefix into different glyphs and the notice's own
        // first words into a token that was never on the screen. The words are what a person checks.
        // THE REASON COMES BEFORE THE QUOTE: on the second smoke run the same model filled the quote and
        // left the reason empty on every act, and a decision with no reason is refused because the record
        // is made of it. Asked for in this order, it writes both.
        sb.AppendLine($"  \"quote\": \"when the decision is {RuleDecisions.Act}: the words on ONE line of the screen above that show the session is in this situation, copied from the screen exactly as they appear - at least ten characters, character for character. Leave out any drawing, spinner or box characters around the words; do not shorten, tidy or paraphrase the words themselves. When the decision is {RuleDecisions.Decline}, an empty string.\"");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Read a reply, refusing anything that names something it was not offered. Every refusal says what was
    /// wrong in words that go straight onto the firing record.
    /// </summary>
    public static RuleAgentReading Read(string? raw, IReadOnlyList<SessionRule> offered)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return RuleAgentReading.Refused(
                "the agent gave no answer at all, so nothing was decided and nothing was done.");

        var json = OnlyTheJson(raw);
        if (json is null)
            return RuleAgentReading.Refused(
                "the agent's answer was not the answer shape that was asked for, so it was not read as a " +
                "decision. It said: " + Shorten(raw));

        JsonElement root;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
            root = document.RootElement;
        }
        catch (JsonException ex)
        {
            return RuleAgentReading.Refused(
                "the agent's answer could not be read as the answer shape that was asked for (" +
                ex.Message + "). It said: " + Shorten(raw));
        }

        using (document)
        {
            if (root.ValueKind != JsonValueKind.Object)
                return RuleAgentReading.Refused(
                    "the agent's answer was not the answer shape that was asked for. It said: " + Shorten(raw));

            var ruleIdText = RuleCallJson.Text(root, "rule_id");
            if (!Guid.TryParse(ruleIdText, out var ruleId))
                return RuleAgentReading.Refused(
                    $"the agent's answer names the instruction '{Shorten(ruleIdText)}', which is not an " +
                    "instruction id at all.");

            var rule = (offered ?? Array.Empty<SessionRule>()).FirstOrDefault(r => r.Id == ruleId);
            if (rule is null)
                return RuleAgentReading.Refused(
                    $"the agent's answer names the instruction {ruleId}, which was not one of the " +
                    $"{(offered?.Count ?? 0)} it was asked about. Nothing was done.");

            var decision = (RuleCallJson.Text(root, "decision") ?? "").Trim().ToLowerInvariant();
            if (decision != RuleDecisions.Act && decision != RuleDecisions.Decline)
                return RuleAgentReading.Refused(
                    $"the agent answered '{Shorten(decision)}', and the only answers there are are " +
                    $"'{RuleDecisions.Act}' and '{RuleDecisions.Decline}'.");

            // A DECISION WITH NO REASON CANNOT BE RECORDED, so it is not an answer. The store refuses a
            // firing with a blank reason - the record is the product - and this is the boundary that can
            // still refuse it in words, for either decision: an act because the send would already have
            // happened by the time the store spoke, a decline because a decline is a recorded firing too.
            var statedReason = (RuleCallJson.Text(root, "reason") ?? "").Trim();
            if (statedReason.Length == 0)
                return RuleAgentReading.Refused(
                    $"the agent decided to {decision} and gave no reason for it. The reason is what the " +
                    "firing record is made of, so a decision with none is one nobody could account for afterwards.");

            // THE CITATION, as written. Whether it is on the screen is the grounding check's question,
            // asked by the evaluator against the excerpt this reply was about; this reader only carries it.
            var quote = RuleTriggerWords.Normalise(RuleCallJson.Text(root, "quote"));

            return RuleAgentReading.Accepted(new RuleAgentReply(ruleId, decision, quote, statedReason));
        }
    }

    /// <summary>
    /// The JSON out of a reply a chat model wrapped in prose or a fenced block, or null when there is none.
    /// Deliberately narrow: the first '{' to the last '}'. A reply with no braces at all is not an answer,
    /// and is refused rather than guessed at.
    /// </summary>
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
