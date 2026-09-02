using System.Text;
using System.Text.Json;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// One reply from the agent, after every part of it has been checked against what was actually offered.
/// Nothing reaches this record that was not validated: the rule was a candidate this pass, the decision is
/// one of the two, and every check is a real check with real arguments.
/// </summary>
public sealed record RuleAgentReply(
    Guid RuleId,
    string Understanding,
    string Decision,
    string Reason,
    IReadOnlyList<RulePrimitiveCall> Checks,
    string TextToType);

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
/// WHAT IS OFFERED IS WHAT MAY BE NAMED. The question carries the candidate rules by id, the account's own
/// sentence for each, the screen, and the checks the product ships - and that last list is READ OFF THE
/// DERIVED REGISTRY, so the question can never advertise a check that does not exist. Coming back, an id
/// that was not a candidate is refused, a decision outside the closed set is refused, and a check that is
/// not in the registry (or is given the wrong arguments) is refused by the SAME validator that guards the
/// store. There is one definition of a legal call in this feature, not two.
///
/// A REFUSAL IS NOT A DECISION. An answer nobody can read never degrades into "do nothing quietly": it
/// comes back as a stated reason, and the evaluator writes it down against every rule that was in play.
/// A model that mumbles must never be read as permission to type into somebody's session.
/// </summary>
public static class RuleAgentContract
{
    /// <summary>How many lines of the screen tail the question carries.</summary>
    public const int ScreenTailLines = 40;

    private static readonly string InputSource = RuleWireNames.ToWireName(nameof(RuleArgumentSource.Input));
    private static readonly string LiteralSource = RuleWireNames.ToWireName(nameof(RuleArgumentSource.Literal));

    /// <summary>
    /// Build the one question for this screen: what a rule is, the rules in play, the screen, the checks
    /// that exist, and the exact shape of the answer.
    /// </summary>
    /// <exception cref="ArgumentNullException">The registry is null.</exception>
    public static string BuildPrompt(
        IReadOnlyList<SessionRule> candidates,
        IReadOnlyList<string> screenRows,
        RulePrimitiveRegistry registry)
    {
        if (registry is null) throw new ArgumentNullException(nameof(registry));

        var sb = new StringBuilder();
        sb.AppendLine("An account has given you standing instructions about its coding sessions. One of its");
        sb.AppendLine("sessions has just gone idle. Below is the tail of that session's terminal screen, and the");
        sb.AppendLine("instructions that might apply to it.");
        sb.AppendLine();
        sb.AppendLine("Read the screen against the instruction and decide. The INSTRUCTION IS THE AUTHORITY: do");
        sb.AppendLine("what it says and nothing more. If the screen is not what the instruction is about - if the");
        sb.AppendLine("words merely appear in something the session is reading, writing or discussing rather than");
        sb.AppendLine("in the session's own report of its own state - then DECLINE and say why. Declining is a");
        sb.AppendLine("correct and expected answer, not a failure.");
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

        sb.AppendLine("--- the session's screen ---");
        foreach (var line in Tail(screenRows, ScreenTailLines)) sb.AppendLine(line);
        sb.AppendLine("--- end of screen ---");
        sb.AppendLine();

        sb.AppendLine("You may ask for any of these checks to be run. They are the ONLY checks that exist; you");
        sb.AppendLine("cannot write code and you cannot invent one. Name a check only if its answer is a condition");
        sb.AppendLine("you are staking your decision on - a check that answers no will abandon the act.");
        foreach (var primitive in registry.Primitives)
        {
            sb.AppendLine($"  {primitive.Name}({string.Join(", ", primitive.Parameters.Select(p => p.Name))}) - {primitive.Summary}");
        }
        sb.AppendLine();
        sb.AppendLine("An argument's value is either written out, or one of these things read when the rule runs,");
        sb.AppendLine("written in angle brackets: " + string.Join(", ", RuleInputs.Names.Select(n => "<" + n + ">")) + ".");
        sb.AppendLine();

        sb.AppendLine("Answer with JSON and nothing else, in exactly this shape:");
        sb.AppendLine("{");
        sb.AppendLine("  \"rule_id\": \"the id of the ONE instruction you are answering about\",");
        sb.AppendLine("  \"understanding\": \"what you think this screen shows, in one or two sentences\",");
        sb.AppendLine($"  \"decision\": \"{RuleDecisions.Act}\" or \"{RuleDecisions.Decline}\",");
        sb.AppendLine("  \"reason\": \"why, in one or two sentences\",");
        sb.AppendLine("  \"checks\": [ { \"name\": \"a check from the list above\", \"arguments\": { \"parameter\": \"value\" } } ],");
        sb.AppendLine($"  \"type\": \"the exact text to type into the session (only when the decision is {RuleDecisions.Act})\"");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Read a reply, refusing anything that names something it was not offered. Every refusal says what was
    /// wrong in words that go straight onto the firing record.
    /// </summary>
    /// <exception cref="ArgumentNullException">The registry is null.</exception>
    public static RuleAgentReading Read(
        string? raw,
        IReadOnlyList<SessionRule> offered,
        RulePrimitiveRegistry registry)
    {
        if (registry is null) throw new ArgumentNullException(nameof(registry));

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

            var ruleIdText = Text(root, "rule_id");
            if (!Guid.TryParse(ruleIdText, out var ruleId))
                return RuleAgentReading.Refused(
                    $"the agent's answer names the instruction '{Shorten(ruleIdText)}', which is not an " +
                    "instruction id at all.");

            var rule = (offered ?? Array.Empty<SessionRule>()).FirstOrDefault(r => r.Id == ruleId);
            if (rule is null)
                return RuleAgentReading.Refused(
                    $"the agent's answer names the instruction {ruleId}, which was not one of the " +
                    $"{(offered?.Count ?? 0)} it was asked about. Nothing was done.");

            var decision = (Text(root, "decision") ?? "").Trim().ToLowerInvariant();
            if (decision != RuleDecisions.Act && decision != RuleDecisions.Decline)
                return RuleAgentReading.Refused(
                    $"the agent answered '{Shorten(decision)}', and the only answers there are are " +
                    $"'{RuleDecisions.Act}' and '{RuleDecisions.Decline}'.");

            var checks = new List<RulePrimitiveCall>();
            if (root.TryGetProperty("checks", out var checkArray) && checkArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in checkArray.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        return RuleAgentReading.Refused(
                            "the agent asked for a check that is not a check at all: " + Shorten(entry.ToString()));
                    checks.Add(ReadCall(entry));
                }
            }

            var validation = RuleCallValidator.ValidateAll(checks, registry);
            if (!validation.IsValid)
                return RuleAgentReading.Refused(
                    "the agent asked for a check that cannot be run: " + validation.Reason);

            var textToType = (Text(root, "type") ?? "").Trim();
            if (decision == RuleDecisions.Act && textToType.Length == 0)
                return RuleAgentReading.Refused(
                    "the agent decided to act and then gave nothing to type. An act with nothing to type is " +
                    "not a decline - it is an answer that was not finished, so nothing was done.");

            return RuleAgentReading.Accepted(new RuleAgentReply(
                ruleId,
                (Text(root, "understanding") ?? "").Trim(),
                decision,
                (Text(root, "reason") ?? "").Trim(),
                checks,
                decision == RuleDecisions.Act ? textToType : ""));
        }
    }

    /// <summary>One asked-for check, as data. Nothing is checked here - <see cref="RuleCallValidator"/> is
    /// the one place that decides whether a call is legal, so a name that does not exist arrives with its
    /// name intact and is refused there by name.</summary>
    private static RulePrimitiveCall ReadCall(JsonElement entry)
    {
        var call = new RulePrimitiveCall { Name = (Text(entry, "name") ?? "").Trim() };

        if (!entry.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
            return call;

        foreach (var argument in arguments.EnumerateObject())
            call.Arguments.Add(ReadArgument(argument.Name, argument.Value));

        return call;
    }

    /// <summary>
    /// One argument's value. A string in angle brackets is a request to read something when the rule runs;
    /// a list is a list of literal terms; anything else is one written-down value. The angle-bracket form is
    /// the same rendering the firing record uses, so what the agent writes and what a person later reads are
    /// the same notation.
    /// </summary>
    private static RuleArgument ReadArgument(string parameter, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return new RuleArgument
            {
                Parameter = parameter,
                Source = LiteralSource,
                Values = value.EnumerateArray().Select(Scalar).ToList(),
            };

        var text = Scalar(value);
        if (text.Length >= 2 && text[0] == '<' && text[^1] == '>')
            return new RuleArgument
            {
                Parameter = parameter,
                Source = InputSource,
                Values = new List<string> { text[1..^1].Trim() },
            };

        return RuleArgument.Literal(parameter, text);
    }

    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null => "",
        _ => value.ToString(),
    };

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? Scalar(value) : null;

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

    private static IReadOnlyList<string> Tail(IReadOnlyList<string>? rows, int tailLines)
    {
        if (rows is null || rows.Count == 0 || tailLines <= 0) return Array.Empty<string>();
        var content = rows.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.TrimEnd()).ToList();
        if (content.Count <= tailLines) return content;
        return content.GetRange(content.Count - tailLines, tailLines);
    }
}
