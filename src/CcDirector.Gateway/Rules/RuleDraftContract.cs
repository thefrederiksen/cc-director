using System.Text;
using System.Text.Json;
using CcDirector.Gateway.Api;

namespace CcDirector.Gateway.Rules;

/// <summary>Who said one thing in an authoring conversation. A closed set, because the prompt renders the
/// two sides differently and a third speaker would silently be rendered as one of them.</summary>
public static class RuleDraftSpeakers
{
    /// <summary>The account holder, in their own words. This is the authority.</summary>
    public const string Person = "person";

    /// <summary>What DevThrottle asked back, so the model can see the question its answer belongs to.</summary>
    public const string DevThrottle = "devthrottle";
}

/// <summary>What the model may answer when it is asked to turn a sentence into a rule.</summary>
public static class RuleDraftAnswers
{
    /// <summary>It has a rule to propose.</summary>
    public const string Propose = "propose";

    /// <summary>It cannot write the rule yet and needs one thing answered first.</summary>
    public const string Ask = "ask";
}

/// <summary>One thing that was said while a rule was being worked out.</summary>
/// <param name="Who">One of <see cref="RuleDraftSpeakers"/>.</param>
/// <param name="Text">What was said.</param>
public sealed record RuleDraftTurn(string Who, string Text);

/// <summary>
/// The session a captured screen came from - the two facts the engine already holds about every session
/// that change what a screen MEANS. A usage-limit notice on Claude Code reads "Claude usage limit
/// reached"; on Codex or Gemini it reads something else, so a rule's trigger words are agent-specific
/// whether anyone says so or not. Since fix round D these come from the roster row the Gateway holds for
/// the session, never from the caller; an origin that names no agent is REFUSED, because there is then no
/// fact to pin the agent scope to and the model must never choose it.
/// </summary>
public sealed record RuleSessionOrigin(string Agent, string Machine)
{
    /// <summary>No agent known. Authoring refuses rather than letting the model choose the scope.</summary>
    public static RuleSessionOrigin None { get; } = new("", "");

    /// <summary>Whether a real session is known.</summary>
    public bool IsKnown => !string.IsNullOrWhiteSpace(Agent);
}

/// <summary>
/// A rule the model has worked out from what the account said, NOT YET STORED and not yet live.
///
/// <see cref="Instruction"/> is deliberately not something the model produces: it is the account's own
/// words, carried through unchanged, because the store treats the instruction as the authority and a model
/// rewrite of it would make the authority a paraphrase. Everything else here is derived from those words
/// and is checked against the same gate that guards the store before this record is ever handed back.
/// </summary>
/// <param name="SessionId">The session whose screen the rule was grounded in. Carried in the write body,
/// so the write route can read that session's screen again and run the same check (ruling D2).</param>
/// <param name="AllAgents">The account said this rule is for every agent - the star. Carried in the write
/// body so the write route can hold the agent scope to the same choice.</param>
/// <param name="ExampleScreen">The EXACT excerpt the model was shown and every trigger word was checked
/// against - <see cref="RuleScreenReading.Excerpt"/>, not a second reading of the screen.</param>
public sealed record RuleProposal(
    string Instruction,
    string SessionId,
    bool AllAgents,
    string ExampleScreen,
    string ScreenDescription,
    IReadOnlyList<string> TriggerWords,
    IReadOnlyList<RulePrimitiveCall> Calls,
    RuleScope Scope,
    int CooldownSeconds,
    int DailyCap,
    string ReadBack);

/// <summary>
/// The answer to one authoring turn: a rule to confirm, a question to answer, or a stated refusal. Exactly
/// one of the three is set.
///
/// A QUESTION IS A FIRST-CLASS ANSWER, exactly as a decline is for the evaluator. A model that does not
/// know which sessions a rule is for must be able to say so; the alternative is a model that picks the
/// widest scope it can and hands back something the account did not ask for.
/// </summary>
public sealed record RuleDraftReading(RuleProposal? Proposal, string? Question, string? Refusal)
{
    /// <summary>A rule to put in front of the person.</summary>
    public static RuleDraftReading Proposed(RuleProposal proposal) => new(proposal, null, null);

    /// <summary>One thing that has to be answered before a rule can be written.</summary>
    public static RuleDraftReading Asked(string question) => new(null, question, null);

    /// <summary>Nothing was drafted, and this is why, in words the account reads.</summary>
    public static RuleDraftReading Refused(string reason) => new(null, null, reason);
}

/// <summary>
/// THE AUTHORING CALL: the account says a sentence, and a model turns it into a rule the product can
/// actually hold. This is the half the Session Rules mission named as missing - until it existed the
/// trigger words and the checks had to be worked out by hand, while the store refused a rule without them
/// on the stated grounds that "the words are worked out from the instruction, not chosen by hand".
///
/// IT IS A SECOND CALL AND NOT THE EVALUATOR'S. <see cref="RuleAgentContract"/> asks whether a standing
/// instruction reaches a screen that exists now. This one asks what a standing instruction IS, before any
/// screen exists. They share the registry, the argument notation and the reader for a check written as
/// JSON, so a check means one thing in this feature; they share nothing else, because the questions are
/// not the same question.
///
/// WHAT IS OFFERED IS WHAT MAY BE NAMED, exactly as at evaluation time. The checks in the question are
/// read off the derived registry, so the question can never advertise a check we do not ship, and a reply
/// naming one that is not in the registry - or handing it the wrong arguments - is refused by the SAME
/// validator that guards the store.
///
/// NOTHING HERE IS SHAPED AROUND ONE KIND OF TROUBLE. The mission that built the evaluator was
/// demonstrated on a provider allowance notice, and the temptation is to write this question as though an
/// allowance notice were what a rule is for. It is not: an account may just as well be describing a
/// provider that has stopped answering, a session sitting on a question, or a build that failed the same
/// way twice. The question below names no kind of trouble as the expected one, and deliberately carries no
/// worked example, because an example is the fastest way to make every rule an account writes come back
/// shaped like the example.
///
/// THE REPLY IS NEVER STORED FROM HERE. This type reads an answer; it writes nothing. A person posts the
/// proposal to the writing route, which stores it in dry run, and a person promotes it after that. Both
/// confirmations that already existed are still there, and this adds no third path into the store.
/// </summary>
public static class RuleDraftContract
{
    /// <summary>
    /// Build the question that turns what the account said into a rule: what a rule is made of, the
    /// conversation so far, the checks that exist, and the exact shape of the answer.
    /// </summary>
    /// <exception cref="ArgumentNullException">The registry or the screen is null.</exception>
    /// <param name="turns">The conversation so far.</param>
    /// <param name="registry">The checks this build ships.</param>
    /// <param name="screen">The REAL screen, read by the Gateway from the session the rule is about. There
    /// is no overload without one: authoring from memory is not a mode (fix round D, ruling D2). The
    /// trigger words are read off <see cref="RuleScreenReading.Excerpt"/>, which is also the exact text the
    /// grounding check runs against. The model is told which agent it is looking at so its words fit THAT
    /// agent's screens, and told that the agent scope is already decided - see
    /// <paramref name="allAgents"/>.</param>
    /// <param name="allAgents">The account said this rule is for every agent (the star). Otherwise a rule
    /// written against a session is for that session's agent alone, which is the default the owner set:
    /// trigger words are agent-specific, so a rule that claims every agent with one agent's words silently
    /// never fires on the others while looking correct in the list.</param>
    public static string BuildDraftPrompt(
        IReadOnlyList<RuleDraftTurn> turns,
        RulePrimitiveRegistry registry,
        RuleScreenReading screen,
        bool allAgents = false)
    {
        if (registry is null) throw new ArgumentNullException(nameof(registry));
        if (screen is null) throw new ArgumentNullException(nameof(screen));
        var origin = screen.Origin;

        var sb = new StringBuilder();
        sb.AppendLine("Somebody is telling you what they want their coding sessions to do without them. Turn");
        sb.AppendLine("what they said into a standing instruction the product can hold, or ask them the ONE thing");
        sb.AppendLine("you need to know before you can.");
        sb.AppendLine();
        sb.AppendLine("A standing instruction works like this. When one of their sessions stops and goes idle, the");
        sb.AppendLine("tail of its terminal screen is read. If any of the instruction's trigger words is on that");
        sb.AppendLine("screen, a model is asked whether the instruction reaches this screen, and if it does, the");
        sb.AppendLine("model composes text and it is typed into the session. So the trigger words are a cheap");
        sb.AppendLine("first filter and not the decision: they decide what is worth looking at closely.");
        sb.AppendLine();
        sb.AppendLine("The instruction can be about anything a session's screen can show once it has stopped. Do");
        sb.AppendLine("not assume you know which kind of trouble is meant, and do not widen what they said: if");
        sb.AppendLine("they described one situation, the rule is about that situation and not about the family of");
        sb.AppendLine("situations it belongs to.");
        sb.AppendLine();
        sb.AppendLine("You are NOT asked to restate their instruction. Their own words are kept as written.");
        sb.AppendLine();

        sb.AppendLine("--- what has been said so far ---");
        foreach (var turn in turns ?? Array.Empty<RuleDraftTurn>())
        {
            var who = string.Equals(turn?.Who, RuleDraftSpeakers.DevThrottle, StringComparison.Ordinal)
                ? "you asked"
                : "they said";
            sb.AppendLine($"{who}: {(turn?.Text ?? "").Trim()}");
        }
        sb.AppendLine("--- end of what has been said ---");
        sb.AppendLine();

        // THE REAL SCREEN, ALWAYS. Without it the model is guessing what the screen will say, and it
        // guesses plausibly and wrongly: asked about a provider outage with no screen, a live model
        // proposed ECONNREFUSED, ETIMEDOUT and 429 as the words to watch for - reasonable-sounding strings
        // that a coding agent's screen may never print. A rule whose trigger words are not on the screen
        // never fires, and looks perfectly good sitting in the list. The excerpt below is the EXACT text
        // the grounding check in Read runs against - the same string, produced once.
        sb.AppendLine("This is the screen of a real session, read just now. It is an EXAMPLE of the situation");
        sb.AppendLine("they mean.");
        if (origin.IsKnown)
        {
            // WHICH AGENT, said plainly. The same trouble prints different words on different agents,
            // so a model that does not know which one it is looking at cannot know which words are
            // this agent's and which are universal.
            sb.AppendLine($"The session is running the agent {origin.Agent}" +
                          (string.IsNullOrWhiteSpace(origin.Machine) ? "." : $" on the machine {origin.Machine}."));
            sb.AppendLine(allAgents
                ? "They said this rule is for EVERY agent, so choose words that any agent's screen would show."
                : $"This rule is for {origin.Agent} sessions only - that is already decided, do not put an agent in " +
                  "the scope yourself. Choose words as they appear on THIS agent's screens.");
        }
        sb.AppendLine();
        sb.AppendLine("--- the screen they captured ---");
        sb.AppendLine(screen.Excerpt);
        sb.AppendLine("--- end of the captured screen ---");
        sb.AppendLine();
        sb.AppendLine("TAKE THE TRIGGER WORDS FROM THIS SCREEN, word for word. Every one has to appear on");
        sb.AppendLine("it exactly as written above - do not invent likely-looking error strings, and do not");
        sb.AppendLine("tidy the spelling or the case. Choose words that would NOT be on an ordinary screen.");
        sb.AppendLine();

        sb.AppendLine("You may ask for any of these checks to be run when the rule fires. They are the ONLY checks");
        sb.AppendLine("that exist; you cannot write code and you cannot invent one. Ask for a check only when its");
        sb.AppendLine("answer is a condition the act depends on - a check whose plain yes-or-no answer is no will");
        sb.AppendLine("stop the act. Most instructions need none at all, and none is a perfectly good answer.");
        foreach (var primitive in registry.Primitives)
        {
            sb.AppendLine($"  {primitive.Name}({string.Join(", ", primitive.Parameters.Select(p => p.Name))}) - {primitive.Summary}");
        }
        sb.AppendLine();
        sb.AppendLine("An argument's value is either written out, or one of these things read when the rule runs,");
        sb.AppendLine("written in angle brackets: " + string.Join(", ", RuleInputs.Names.Select(n => "<" + n + ">")) + ".");
        sb.AppendLine();

        sb.AppendLine("The two ceilings are what stop a rule that is wrong from being wrong forever, so each has to");
        sb.AppendLine("be a whole number chosen for THIS instruction, within these bounds: the cooldown is");
        sb.AppendLine(RuleCeilings.CooldownStated + ", and the daily cap is " + RuleCeilings.DailyCapStated + ".");
        sb.AppendLine("The cooldown is also the waiting: an instruction that should leave something alone for a");
        sb.AppendLine("while and then try again is an instruction with a long cooldown.");
        sb.AppendLine();

        sb.AppendLine("Answer with JSON and nothing else, in exactly this shape:");
        sb.AppendLine("{");
        sb.AppendLine($"  \"answer\": \"{RuleDraftAnswers.Propose}\" or \"{RuleDraftAnswers.Ask}\",");
        sb.AppendLine($"  \"question\": \"the one thing you need answered (only when the answer is {RuleDraftAnswers.Ask})\",");
        sb.AppendLine("  \"screen_description\": \"what the screen looks like when this instruction applies, in plain words\",");
        sb.AppendLine("  \"trigger_words\": [\"words that would be on such a screen and not on an ordinary one\"],");
        sb.AppendLine("  \"checks\": [ { \"name\": \"a check from the list above\", \"arguments\": { \"parameter\": \"value\" } } ],");
        sb.AppendLine($"  \"scope\": \"{SessionRuleWire.AllSessionsWireValue}\", or an object with any of agent, repository, machine, mission,");
        sb.AppendLine("  \"cooldown_seconds\": how long to wait before acting on the same session again,");
        sb.AppendLine("  \"daily_cap\": how many times a day it may act on one session,");
        sb.AppendLine("  \"read_back\": \"what will actually happen, in two or three sentences, addressed to them\"");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Read the model's answer, refusing anything that names something it was not offered. Every refusal
    /// says what was wrong in words the account reads.
    /// </summary>
    /// <param name="raw">What the model said.</param>
    /// <param name="instruction">The account's own words, which become the rule's instruction unchanged.</param>
    /// <param name="registry">The checks this build ships.</param>
    /// <param name="screen">The screen the model was shown. Every trigger word is checked against ITS
    /// excerpt - the same string the prompt carried - and the agent scope is pinned to ITS origin.</param>
    /// <param name="allAgents">The account said every agent (the star).</param>
    /// <exception cref="ArgumentNullException">The registry or the screen is null.</exception>
    public static RuleDraftReading Read(
        string? raw,
        string instruction,
        RulePrimitiveRegistry registry,
        RuleScreenReading screen,
        bool allAgents = false)
    {
        if (registry is null) throw new ArgumentNullException(nameof(registry));
        if (screen is null) throw new ArgumentNullException(nameof(screen));
        var origin = screen.Origin;

        if (string.IsNullOrWhiteSpace(raw))
            return RuleDraftReading.Refused(
                "the model gave no answer at all, so no rule was drafted and nothing was stored.");

        var json = OnlyTheJson(raw);
        if (json is null)
            return RuleDraftReading.Refused(
                "the model's answer was not the answer shape that was asked for, so it was not read as a " +
                "rule. It said: " + Shorten(raw));

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return RuleDraftReading.Refused(
                "the model's answer could not be read as the answer shape that was asked for (" +
                ex.Message + "). It said: " + Shorten(raw));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return RuleDraftReading.Refused(
                    "the model's answer was not the answer shape that was asked for. It said: " + Shorten(raw));

            var answer = (RuleCallJson.Text(root, "answer") ?? "").Trim().ToLowerInvariant();
            if (answer != RuleDraftAnswers.Propose && answer != RuleDraftAnswers.Ask)
                return RuleDraftReading.Refused(
                    $"the model answered '{Shorten(answer)}', and the only answers there are are " +
                    $"'{RuleDraftAnswers.Propose}' and '{RuleDraftAnswers.Ask}'.");

            if (answer == RuleDraftAnswers.Ask)
            {
                var question = (RuleCallJson.Text(root, "question") ?? "").Trim();
                // A QUESTION WITH NOTHING IN IT IS NOT A QUESTION. Read as one it would put an empty
                // prompt in front of the account, who would have nothing to answer and no way to tell that
                // from the product having failed.
                return question.Length == 0
                    ? RuleDraftReading.Refused(
                        "the model said it needed something answered and then asked nothing, so there is " +
                        "nothing to put in front of you and no rule was drafted.")
                    : RuleDraftReading.Asked(question);
            }

            var checks = RuleCallJson.ReadChecks(root, "checks", required: true, out var checksProblem);
            if (checks is null)
                return RuleDraftReading.Refused("the drafted rule does not say what it wants checked: " + checksProblem);

            var validation = RuleCallValidator.ValidateAll(checks, registry);
            if (!validation.IsValid)
                return RuleDraftReading.Refused("the drafted rule asks for a check that cannot be run: " + validation.Reason);

            // THE SAME SCOPE READER THE WRITING ROUTE USES. An absent or empty scope comes back null here
            // exactly as it does there, and is refused rather than being read as every session the account
            // has - the fail-open that reading omitted as widest would be.
            var scope = SessionRuleWire.ReadScope(root);
            if (scope is null)
                return RuleDraftReading.Refused(
                    "the drafted rule does not say which sessions it may act on. Every session is a choice " +
                    "that can be made, but it has to be said, so nothing was drafted.");

            // THE AGENT PART OF THE SCOPE IS OURS TO DECIDE, NEVER THE MODEL'S (fix round D, ruling D3).
            // The rule is for the session's agent unless the account said "every agent" - whatever the
            // model wrote. It is a fact we hold, so asking a model to guess it and then checking the guess
            // would be the long way round to the same answer with a failure mode added. And when the fact
            // is NOT held there is nothing to pin it to, so the answer is refused: an originless answer
            // used to let the model's own scope stand, which was every agent chosen by the answer.
            if (!origin.IsKnown)
                return RuleDraftReading.Refused(
                    "the session this rule is about does not say which agent it runs, so there is no fact " +
                    "to scope the rule to and the model is never allowed to choose that. Nothing was drafted.");
            scope = scope with { Agent = allAgents ? null : origin.Agent };

            // A PROPOSAL NOBODY CAN READ IS NOT A PROPOSAL. The read-back is the whole point of this step:
            // it is what the person confirms. A rule offered without one would be asking somebody to agree
            // to a list of trigger words.
            var readBack = (RuleCallJson.Text(root, "read_back") ?? "").Trim();
            if (readBack.Length == 0)
                return RuleDraftReading.Refused(
                    "the drafted rule does not say what it would actually do, and a rule you cannot read " +
                    "back is a rule you cannot agree to. Nothing was drafted.");

            // THE WORDS, IN THE FORM THE STORE WILL KEEP THEM. One normaliser, shared with the store, so the
            // word that is checked below is the word that is stored later - not a padded one checked
            // narrow and stored wide.
            var triggerWords = RuleTriggerWords.NormaliseAll(SessionRuleWire.Strings(root, "trigger_words"));

            // THE WORDS HAVE TO BE ON THE SCREEN THEY WERE TAKEN FROM - and "the screen" is the excerpt the
            // prompt carried, not a longer reading of the same text. A word that is not on it is a word
            // the model invented, and a rule whose trigger words never appear is a rule that sits in the
            // list looking correct and never fires once. Refusing here is cheap; discovering it at 3am is
            // not. The same function runs again at the write gate.
            var notGrounded = RuleTriggerWords.WhyNotGrounded(triggerWords, screen, "the screen you captured");
            if (notGrounded is not null)
                return RuleDraftReading.Refused("the drafted " + notGrounded + " Nothing was drafted.");

            // A NUMBER THAT CANNOT BE READ IS A REFUSAL, NEVER AN EXCEPTION (fix round D, ruling D7). A
            // decimal or an out-of-range integer used to throw past every catch and come out as a server
            // error with the reason lost.
            if (!SessionRuleWire.TryNumber(root, "cooldown_seconds", out var cooldown, out var cooldownProblem))
                return RuleDraftReading.Refused("the drafted rule's ceiling cannot be read: " + cooldownProblem);
            if (!SessionRuleWire.TryNumber(root, "daily_cap", out var dailyCap, out var capProblem))
                return RuleDraftReading.Refused("the drafted rule's ceiling cannot be read: " + capProblem);

            return RuleDraftReading.Proposed(new RuleProposal(
                Instruction: instruction ?? "",
                SessionId: screen.SessionId,
                AllAgents: allAgents,
                ExampleScreen: screen.Excerpt,
                ScreenDescription: (RuleCallJson.Text(root, "screen_description") ?? "").Trim(),
                TriggerWords: triggerWords,
                Calls: checks,
                Scope: scope,
                CooldownSeconds: cooldown,
                DailyCap: dailyCap,
                ReadBack: readBack));
        }
    }

    /// <summary>
    /// The JSON out of a reply a chat model wrapped in prose or a fenced block, or null when there is none.
    /// Deliberately narrow: the first open brace to the last close brace. A reply with no braces at all is
    /// not an answer, and is refused rather than guessed at.
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
