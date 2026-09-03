using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE ONE AGENT CALL, IN ITS PHASE 1 SHAPE: a yes/no question plus one line copied from the screen.
///
/// Through phase 2 the question asked for an understanding, a reason, a list of checks and the text to
/// type, and the phase 0 harness measured what that cost on real screens: timeouts on the positives, and
/// no act ever grounded because no model quoted the screen. So the shape here is the one that was always
/// meant. What these tests hold, each as a presence:
///
///   * the question carries every candidate by id and the account's own sentence, and the exact excerpt
///     the citation will be checked against;
///   * the question asks for a decision and ONE copied line, and does NOT ask for text to type or offer
///     any checks - both are on the rule already;
///   * a reply is read only if it names something that was offered; a decision outside the closed set,
///     an unknown rule id and a missing reason are all stated refusals, never decisions;
///   * the reply record has no field that could carry a keystroke (see RulesAgentReplyGuardTests for the
///     assertion against the built assembly).
/// </summary>
public sealed class RuleAgentContractTests
{
    private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string TheSentence =
        "If a session's screen says it has run out of its model allowance, type the command that shows me what is left.";

    private static SessionRule Rule(Guid? id = null, string? instruction = null) => new(
        id ?? Guid.NewGuid(),
        instruction ?? TheSentence,
        "A session stopped on a provider allowance notice.",
        "/status",
        new[] { "reached your", "limit" },
        Array.Empty<RulePrimitiveCall>(),
        RuleScope.AllSessions,
        300,
        5,
        RuleState.DryRun,
        "",
        Now,
        Now);

    private const string TheNotice = "You've reached your Fable 5 limit. Run /usage-credits to continue.";

    private static readonly string TheScreen = string.Join("\n", new[]
    {
        "C:\\scratch>echo " + TheNotice,
        TheNotice,
        "C:\\scratch>",
    });

    private static string Excerpt() => RuleScreenExcerpt.Of(TheScreen);

    private static string ActReply(Guid ruleId, string quote = TheNotice, string reason = "The session itself is blocked on its allowance.") => $$"""
        {
          "rule_id": "{{ruleId}}",
          "decision": "act",
          "quote": "{{quote}}",
          "reason": "{{reason}}"
        }
        """;

    // ---- the question ------------------------------------------------------------------------------

    [Fact]
    public void The_prompt_carries_every_candidate_rules_id_and_the_sentence_the_account_said()
    {
        var first = Rule(instruction: "first instruction, in the account's own words");
        var second = Rule(instruction: "second instruction, also the account's own words");

        var prompt = RuleAgentContract.BuildPrompt(new[] { first, second }, Excerpt());

        Assert.Contains(first.Id.ToString(), prompt, StringComparison.Ordinal);
        Assert.Contains(second.Id.ToString(), prompt, StringComparison.Ordinal);
        Assert.Contains("first instruction, in the account's own words", prompt, StringComparison.Ordinal);
        Assert.Contains("second instruction, also the account's own words", prompt, StringComparison.Ordinal);
    }

    /// <summary>The excerpt in the question is the excerpt the citation is checked against - the same
    /// string, produced once by the one function the authoring path uses (ruling D2).</summary>
    [Fact]
    public void The_prompt_carries_the_exact_excerpt_it_is_asking_about()
    {
        var prompt = RuleAgentContract.BuildPrompt(new[] { Rule() }, Excerpt());

        Assert.Contains(Excerpt(), prompt, StringComparison.Ordinal);
        Assert.Contains(TheNotice, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_names_the_two_decisions_it_will_accept()
    {
        var prompt = RuleAgentContract.BuildPrompt(new[] { Rule() }, Excerpt());

        Assert.Contains("\"" + RuleDecisions.Act + "\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"" + RuleDecisions.Decline + "\"", prompt, StringComparison.Ordinal);
    }

    /// <summary>THE SHAPE: a decision and ONE copied line. No text to type, because the rule holds it;
    /// no checks, because the rule holds those too and a model given a list invents arguments for them.</summary>
    [Fact]
    public void The_prompt_asks_for_one_copied_line_and_does_not_ask_for_text_or_offer_checks()
    {
        var prompt = RuleAgentContract.BuildPrompt(new[] { Rule() }, Excerpt());

        Assert.Contains("\"quote\"", prompt, StringComparison.Ordinal);
        Assert.Contains("copied from the screen", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\"", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"checks\"", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"understanding\"", prompt, StringComparison.Ordinal);
        foreach (var primitive in RulePrimitiveRegistry.Default.Primitives)
            Assert.DoesNotContain(primitive.Name + "(", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_says_declining_is_a_correct_answer_and_that_the_instruction_is_the_authority()
    {
        var prompt = RuleAgentContract.BuildPrompt(new[] { Rule() }, Excerpt());

        Assert.Contains("DECLINE", prompt, StringComparison.Ordinal);
        Assert.Contains("INSTRUCTION IS THE AUTHORITY", prompt, StringComparison.Ordinal);
    }

    // ---- reading a reply ---------------------------------------------------------------------------

    [Fact]
    public void A_well_formed_act_is_read_with_its_quote_and_reason()
    {
        var rule = Rule();

        var reading = RuleAgentContract.Read(ActReply(rule.Id), new[] { rule });

        Assert.Null(reading.Refusal);
        var reply = Assert.IsType<RuleAgentReply>(reading.Reply);
        Assert.Equal(rule.Id, reply.RuleId);
        Assert.Equal(RuleDecisions.Act, reply.Decision);
        Assert.Equal(TheNotice, reply.Quote);
        Assert.Equal("The session itself is blocked on its allowance.", reply.Reason);
    }

    /// <summary>A reply that carries a "type" of its own is read - the extra field is not an error - but
    /// nothing of it survives: the reply record has nowhere to put it.</summary>
    [Fact]
    public void A_reply_that_offers_text_to_type_is_read_without_it_because_the_record_has_nowhere_to_put_it()
    {
        var rule = Rule();
        var reply = $$"""
        {
          "rule_id": "{{rule.Id}}",
          "decision": "act",
          "quote": "{{TheNotice}}",
          "reason": "blocked on its allowance.",
          "type": "/usage-credits",
          "checks": [ { "name": "matches_any", "arguments": { "text": "<screen_text>", "terms": ["reached your"] } } ]
        }
        """;

        var reading = RuleAgentContract.Read(reply, new[] { rule });

        Assert.Null(reading.Refusal);
        var properties = typeof(RuleAgentReply).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "Decision", "Quote", "Reason", "RuleId" }, properties);
    }

    [Fact]
    public void A_reply_wrapped_in_a_fenced_code_block_is_still_read()
    {
        var rule = Rule();
        var wrapped = "Here is my answer:\n```json\n" + ActReply(rule.Id) + "\n```\n";

        var reading = RuleAgentContract.Read(wrapped, new[] { rule });

        Assert.Null(reading.Refusal);
        Assert.Equal(RuleDecisions.Act, reading.Reply!.Decision);
    }

    [Fact]
    public void A_decline_needs_no_quote_and_carries_its_reason()
    {
        var rule = Rule();
        var reply = $$"""
        {
          "rule_id": "{{rule.Id}}",
          "decision": "decline",
          "reason": "the words are in a document the session is reading, not in its own state."
        }
        """;

        var reading = RuleAgentContract.Read(reply, new[] { rule });

        Assert.Null(reading.Refusal);
        Assert.Equal(RuleDecisions.Decline, reading.Reply!.Decision);
        Assert.Equal("", reading.Reply.Quote);
        Assert.Equal("the words are in a document the session is reading, not in its own state.", reading.Reply.Reason);
    }

    [Fact]
    public void A_quote_is_carried_as_written_and_whether_it_is_on_the_screen_is_not_this_readers_question()
    {
        var rule = Rule();

        var reading = RuleAgentContract.Read(ActReply(rule.Id, quote: "  words that are on no screen  "), new[] { rule });

        Assert.Null(reading.Refusal);
        Assert.Equal("words that are on no screen", reading.Reply!.Quote);
    }

    [Fact]
    public void No_answer_at_all_is_a_refusal_and_never_a_decision()
    {
        var reading = RuleAgentContract.Read(null, new[] { Rule() });

        Assert.Null(reading.Reply);
        Assert.Contains("no answer at all", reading.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_answer_that_is_not_an_answer_shape_is_a_refusal()
    {
        var reading = RuleAgentContract.Read("I think you should probably act here.", new[] { Rule() });

        Assert.Null(reading.Reply);
        Assert.Contains("not the answer shape", reading.Refusal!, StringComparison.Ordinal);
        Assert.Contains("I think you should probably act here.", reading.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Broken_json_is_a_refusal_and_never_partly_read()
    {
        var reading = RuleAgentContract.Read("{ \"rule_id\": }", new[] { Rule() });

        Assert.Null(reading.Reply);
        Assert.Contains("could not be read", reading.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_id_that_was_not_offered_is_refused_and_the_reason_names_it()
    {
        var offered = Rule();
        var somebodyElse = Guid.NewGuid();

        var reading = RuleAgentContract.Read(ActReply(somebodyElse), new[] { offered });

        Assert.Null(reading.Reply);
        Assert.Contains(somebodyElse.ToString(), reading.Refusal!, StringComparison.Ordinal);
        Assert.Contains("was not one of the 1", reading.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_id_that_is_not_an_id_at_all_is_refused()
    {
        var reading = RuleAgentContract.Read(
            "{ \"rule_id\": \"the allowance one\", \"decision\": \"act\", \"quote\": \"x\", \"reason\": \"y\" }",
            new[] { Rule() });

        Assert.Null(reading.Reply);
        Assert.Contains("not an instruction id", reading.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_decision_outside_the_closed_set_is_refused()
    {
        var rule = Rule();
        var reply = $$"""
        { "rule_id": "{{rule.Id}}", "decision": "maybe", "quote": "", "reason": "not sure." }
        """;

        var reading = RuleAgentContract.Read(reply, new[] { rule });

        Assert.Null(reading.Reply);
        Assert.Contains("'maybe'", reading.Refusal!, StringComparison.Ordinal);
        Assert.Contains(RuleDecisions.Act, reading.Refusal, StringComparison.Ordinal);
        Assert.Contains(RuleDecisions.Decline, reading.Refusal, StringComparison.Ordinal);
    }

    /// <summary>A decision with no reason cannot be recorded - the record is the product - so it is refused
    /// at this boundary, for EITHER decision: a decline is a recorded firing too, and a store refusal on
    /// a decline would otherwise surface as an exception out of the pass rather than a stated refusal.</summary>
    [Theory]
    [InlineData("act")]
    [InlineData("decline")]
    public void A_decision_with_a_blank_reason_is_refused_at_the_reply_boundary(string decision)
    {
        var rule = Rule();
        var reply = $$"""
        { "rule_id": "{{rule.Id}}", "decision": "{{decision}}", "quote": "{{TheNotice}}", "reason": "   " }
        """;

        var reading = RuleAgentContract.Read(reply, new[] { rule });

        Assert.Null(reading.Reply);
        Assert.Contains("gave no reason", reading.Refusal!, StringComparison.Ordinal);
        Assert.Contains(decision, reading.Refusal, StringComparison.Ordinal);
    }
}
