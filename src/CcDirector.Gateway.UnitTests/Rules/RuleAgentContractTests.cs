using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE ONE AGENT CALL (Architect ruling A5): one question per screen covering every candidate rule, and a
/// reply every part of which is validated against what was actually OFFERED. An id that was not a
/// candidate is refused; a check that is not in the derived registry is refused; a decision outside the
/// closed set is refused. A refusal is a stated reason, never a shrug.
///
/// The prompt's expectations are DERIVED from the registry, never hand-kept: a test holding its own copy
/// of the check list would pass while the shipped list said something else.
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
        new[] { "reached your", "limit" },
        Array.Empty<RulePrimitiveCall>(),
        RuleScope.AllSessions,
        300,
        5,
        RuleState.DryRun,
        "",
        Now,
        Now);

    private static readonly string[] Screen =
    {
        "C:\\scratch>echo You've reached your Fable 5 limit. Run /usage-credits to continue.",
        "You've reached your Fable 5 limit. Run /usage-credits to continue.",
        "C:\\scratch>",
    };

    private static RulePrimitiveRegistry Registry => RulePrimitiveRegistry.Default;

    // ---- the prompt --------------------------------------------------------------------------------

    [Fact]
    public void The_prompt_carries_every_candidate_rules_id_and_the_sentence_the_account_said()
    {
        var one = Rule(instruction: "First instruction, in the account's own words.");
        var two = Rule(instruction: "Second instruction, also in the account's own words.");

        var prompt = RuleAgentContract.BuildPrompt(new[] { one, two }, Screen, Registry);

        Assert.Contains(one.Id.ToString(), prompt);
        Assert.Contains(two.Id.ToString(), prompt);
        Assert.Contains("First instruction, in the account's own words.", prompt);
        Assert.Contains("Second instruction, also in the account's own words.", prompt);
    }

    [Fact]
    public void The_prompt_carries_the_screen_it_is_asking_about()
    {
        var prompt = RuleAgentContract.BuildPrompt(new[] { Rule() }, Screen, Registry);

        Assert.Contains("You've reached your Fable 5 limit.", prompt);
    }

    [Fact]
    public void The_prompt_offers_exactly_the_checks_the_registry_derives_and_no_others()
    {
        var prompt = RuleAgentContract.BuildPrompt(new[] { Rule() }, Screen, Registry);

        Assert.NotEmpty(Registry.Primitives);   // instrument: an empty registry would make the rest vacuous
        foreach (var primitive in Registry.Primitives)
        {
            Assert.Contains(primitive.Name, prompt);
            Assert.Contains(primitive.Summary, prompt);
        }
    }

    [Fact]
    public void The_prompt_names_the_two_decisions_it_will_accept()
    {
        var prompt = RuleAgentContract.BuildPrompt(new[] { Rule() }, Screen, Registry);

        Assert.Contains(RuleDecisions.Act, prompt);
        Assert.Contains(RuleDecisions.Decline, prompt);
    }

    // ---- reading the reply -------------------------------------------------------------------------

    [Fact]
    public void A_well_formed_act_is_read_with_its_understanding_reason_checks_and_text()
    {
        var rule = Rule();
        var raw = $$"""
        {
          "rule_id": "{{rule.Id}}",
          "understanding": "The session is blocked on its Fable 5 allowance and cannot run a turn.",
          "decision": "act",
          "reason": "The screen is the session's own state, not a discussion of one.",
          "checks": [
            { "name": "matches_any", "arguments": { "text": "<screen_text>", "terms": ["reached your", "limit"] } }
          ],
          "type": "/usage-credits"
        }
        """;

        var reading = RuleAgentContract.Read(raw, new[] { rule }, Registry);

        Assert.Null(reading.Refusal);
        var reply = reading.Reply!;
        Assert.Equal(rule.Id, reply.RuleId);
        Assert.Equal(RuleDecisions.Act, reply.Decision);
        Assert.Contains("blocked on its Fable 5 allowance", reply.Understanding);
        Assert.Contains("not a discussion", reply.Reason);
        Assert.Equal("/usage-credits", reply.TextToType);

        var call = Assert.Single(reply.Checks);
        Assert.Equal("matches_any", call.Name);
        Assert.Equal("matches_any(text=<screen_text>, terms=reached your,limit)", call.Describe());
    }

    [Fact]
    public void A_reply_wrapped_in_a_fenced_code_block_is_still_read()
    {
        var rule = Rule();
        var raw = "```json\n" + $$"""
        { "rule_id": "{{rule.Id}}", "understanding": "u", "decision": "decline", "reason": "r" }
        """ + "\n```";

        var reading = RuleAgentContract.Read(raw, new[] { rule }, Registry);

        Assert.Null(reading.Refusal);
        Assert.Equal(RuleDecisions.Decline, reading.Reply!.Decision);
    }

    [Fact]
    public void A_decline_needs_no_text_and_carries_its_reason()
    {
        var rule = Rule();
        var raw = $$"""
        {
          "rule_id": "{{rule.Id}}",
          "understanding": "The screen is a document that quotes an allowance notice.",
          "decision": "decline",
          "reason": "The notice is being discussed, not reported by the session about itself."
        }
        """;

        var reading = RuleAgentContract.Read(raw, new[] { rule }, Registry);

        Assert.Null(reading.Refusal);
        Assert.Equal(RuleDecisions.Decline, reading.Reply!.Decision);
        Assert.Equal("", reading.Reply.TextToType);
        Assert.Contains("discussed", reading.Reply.Reason);
    }

    // ---- the refusals ------------------------------------------------------------------------------

    [Fact]
    public void No_answer_at_all_is_a_refusal_and_never_a_decision()
    {
        var reading = RuleAgentContract.Read(null, new[] { Rule() }, Registry);

        Assert.Null(reading.Reply);
        Assert.Contains("gave no answer", reading.Refusal);
    }

    [Fact]
    public void An_answer_that_is_not_an_answer_shape_is_a_refusal()
    {
        var reading = RuleAgentContract.Read("I had a look and I think you should probably switch models.",
            new[] { Rule() }, Registry);

        Assert.Null(reading.Reply);
        Assert.NotNull(reading.Refusal);
    }

    [Fact]
    public void A_rule_id_that_was_not_offered_is_refused_and_the_reason_names_it()
    {
        var offered = Rule();
        var neverOffered = Guid.NewGuid();
        var raw = $$"""
        { "rule_id": "{{neverOffered}}", "understanding": "u", "decision": "act", "reason": "r", "type": "/model opus" }
        """;

        var reading = RuleAgentContract.Read(raw, new[] { offered }, Registry);

        Assert.Null(reading.Reply);
        Assert.Contains(neverOffered.ToString(), reading.Refusal);
    }

    [Fact]
    public void A_decision_outside_the_closed_set_is_refused()
    {
        var rule = Rule();
        var raw = $$"""
        { "rule_id": "{{rule.Id}}", "understanding": "u", "decision": "maybe", "reason": "r" }
        """;

        var reading = RuleAgentContract.Read(raw, new[] { rule }, Registry);

        Assert.Null(reading.Reply);
        Assert.Contains("maybe", reading.Refusal);
    }

    [Fact]
    public void A_check_the_product_does_not_ship_is_refused_and_the_reason_lists_what_it_does_ship()
    {
        var rule = Rule();
        var raw = $$"""
        {
          "rule_id": "{{rule.Id}}", "understanding": "u", "decision": "act", "reason": "r", "type": "/model opus",
          "checks": [ { "name": "run_shell", "arguments": { "command": "rm -rf /" } } ]
        }
        """;

        var reading = RuleAgentContract.Read(raw, new[] { rule }, Registry);

        Assert.Null(reading.Reply);
        Assert.Contains("run_shell", reading.Refusal);
        foreach (var primitive in Registry.Primitives)
            Assert.Contains(primitive.Name, reading.Refusal);
    }

    [Fact]
    public void A_check_given_the_wrong_arguments_is_refused()
    {
        var rule = Rule();
        var raw = $$"""
        {
          "rule_id": "{{rule.Id}}", "understanding": "u", "decision": "act", "reason": "r", "type": "/model opus",
          "checks": [ { "name": "matches_any", "arguments": { "text": "<screen_text>" } } ]
        }
        """;

        var reading = RuleAgentContract.Read(raw, new[] { rule }, Registry);

        Assert.Null(reading.Reply);
        Assert.Contains("terms", reading.Refusal);
    }

    [Fact]
    public void An_act_with_nothing_to_type_is_refused_rather_than_treated_as_a_decline()
    {
        var rule = Rule();
        var raw = $$"""
        { "rule_id": "{{rule.Id}}", "understanding": "u", "decision": "act", "reason": "r", "type": "" }
        """;

        var reading = RuleAgentContract.Read(raw, new[] { rule }, Registry);

        Assert.Null(reading.Reply);
        Assert.Contains("nothing to type", reading.Refusal);
    }

    [Fact]
    public void An_argument_naming_something_the_rule_cannot_read_is_refused()
    {
        var rule = Rule();
        var raw = $$"""
        {
          "rule_id": "{{rule.Id}}", "understanding": "u", "decision": "act", "reason": "r", "type": "/model opus",
          "checks": [ { "name": "matches_any", "arguments": { "text": "<the_users_password>", "terms": ["x"] } } ]
        }
        """;

        var reading = RuleAgentContract.Read(raw, new[] { rule }, Registry);

        Assert.Null(reading.Reply);
        Assert.Contains("the_users_password", reading.Refusal);
    }
}
