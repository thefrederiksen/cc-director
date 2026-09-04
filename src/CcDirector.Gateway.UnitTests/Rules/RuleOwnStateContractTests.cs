using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE SECOND QUESTION, read on its own (phase 1, the targeted fix). The evaluator's side of it is proved
/// in <see cref="RuleEvaluatorOwnStateTests"/>; this is the reader, and the property that matters here is
/// that EVERY way of not answering is a refusal. The pass condition is one explicit verdict, so a blank
/// reply, prose, a missing field and a value outside the two must all come back as refusals - if any of
/// them came back as a verdict, the check would certify a call it never got an answer to.
/// </summary>
public sealed class RuleOwnStateContractTests
{
    private const string TheNotice =
        "You've reached your Fable 5 limit. Run /usage-credits to continue or switch models with /model.";

    [Fact]
    public void An_own_answer_is_read_and_can_carry_an_act()
    {
        var reading = RuleOwnStateContract.Read(
            $$"""{ "{{RuleOwnStateContract.Field}}": "own", "reason": "The agent printed this about itself." }""");

        Assert.Null(reading.Refusal);
        Assert.Equal(RuleOwnState.Own, reading.Verdict);
        Assert.Equal("The agent printed this about itself.", reading.Reason);
        Assert.True(reading.CanCarryAnAct);
    }

    [Fact]
    public void An_elsewhere_answer_is_read_with_its_reason_and_cannot_carry_an_act()
    {
        var reading = RuleOwnStateContract.Read(
            $$"""{ "{{RuleOwnStateContract.Field}}": "elsewhere", "reason": "It is a report about another session." }""");

        Assert.Null(reading.Refusal);
        Assert.Equal(RuleOwnState.Elsewhere, reading.Verdict);
        Assert.Equal("It is a report about another session.", reading.Reason);
        Assert.False(reading.CanCarryAnAct);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I would rather not say.")]
    [InlineData("{ \"reason\": \"no verdict field at all\" }")]
    [InlineData("{ \"whose_state\": \"maybe\", \"reason\": \"neither of the two\" }")]
    [InlineData("{ \"whose_state\": \"own\", ")]
    public void Every_way_of_not_answering_is_a_refusal_and_none_of_them_can_carry_an_act(string? raw)
    {
        var reading = RuleOwnStateContract.Read(raw);

        Assert.NotNull(reading.Refusal);
        Assert.NotEqual("", reading.Refusal);
        Assert.Null(reading.Verdict);
        Assert.False(reading.CanCarryAnAct);
    }

    /// <summary>The asymmetry, pinned so it cannot drift into either extreme: an "elsewhere" verdict with
    /// no reason is refused, because that reason is what the refusal is recorded as; an "own" verdict with
    /// no reason is read, because nothing is recorded from it and refusing there would only cost correct
    /// acts.</summary>
    [Fact]
    public void An_elsewhere_answer_with_no_reason_is_refused_and_an_own_answer_with_none_is_not()
    {
        var elsewhere = RuleOwnStateContract.Read(
            $$"""{ "{{RuleOwnStateContract.Field}}": "elsewhere", "reason": "" }""");
        Assert.NotNull(elsewhere.Refusal);
        Assert.False(elsewhere.CanCarryAnAct);

        var own = RuleOwnStateContract.Read(
            $$"""{ "{{RuleOwnStateContract.Field}}": "own", "reason": "" }""");
        Assert.Null(own.Refusal);
        Assert.True(own.CanCarryAnAct);
    }

    /// <summary>The question is about the LINE and the SCREEN, and it carries neither the instruction nor
    /// anything that would let it re-judge whether the rule applies. A second opinion from the same model
    /// on the same question is not a check; asking the one thing the first question is worst at is.</summary>
    [Fact]
    public void The_question_carries_the_cited_line_and_the_screen_and_asks_only_whose_state_it_is()
    {
        var excerpt = "C:\\scratch>echo hello\n" + TheNotice + "\nC:\\scratch>";
        var facts = new RuleSessionFacts("sid-1", "RawCli", @"D:\ReposFred\zz-distinctive", "SOREN_NORTH", "m", "WaitingForInput");

        var prompt = RuleOwnStateContract.BuildPrompt(TheNotice, excerpt, facts);

        // Presences first, so nothing below passes over an empty question.
        Assert.Contains(TheNotice, prompt, StringComparison.Ordinal);
        Assert.Contains(excerpt, prompt, StringComparison.Ordinal);
        Assert.Contains(RuleOwnStateContract.Field, prompt, StringComparison.Ordinal);
        Assert.Contains(RuleOwnState.Own, prompt, StringComparison.Ordinal);
        Assert.Contains(RuleOwnState.Elsewhere, prompt, StringComparison.Ordinal);
        Assert.Contains("RawCli", prompt, StringComparison.Ordinal);

        // And what must not be in it: the account's instruction, which would make this the first question
        // again, and the machine state ruling A11 keeps out of every run-time question.
        Assert.DoesNotContain("the account said", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rule_id", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("zz-distinctive", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SOREN_NORTH", prompt, StringComparison.Ordinal);
    }
}
