using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// AN ACT'S REASON MUST BE GROUNDED IN THE SCREEN IT WAS GIVEN (Architect ruling A12).
///
/// This is the check itself. The evaluator's half - an act refused, a decline recorded with the mismatch
/// noted - is in <see cref="RuleEvaluatorTests"/>.
///
/// The case that produced this ruling is the first test: on a live run a rule declined and quoted a
/// sentence that had been on that session's screen twelve minutes earlier, in an unrelated run. The
/// decline was safe. The same unfaithfulness pointed the other way is a rule acting on evidence that was
/// not there.
/// </summary>
public sealed class RuleReasonGroundingTests
{
    private const string TheScreen =
        "C:\\scratch> echo notice\n" +
        "Claude usage limit reached. Your limit will reset at 2:30pm.\n" +
        "C:\\scratch>";

    [Fact]
    public void A_reason_quoting_words_that_are_not_on_the_screen_is_not_grounded_and_names_them()
    {
        // The real sentence from the live run, quoted from the firing record.
        const string reason =
            "the echo output explicitly says 'THE SCREEN HAS MOVED ON WHILE THE RULE WAS THINKING', " +
            "confirming it is no longer stopped on the allowance notice.";

        var verdict = RuleReasonGrounding.Check(reason, TheScreen);

        Assert.False(verdict.IsGrounded);
        Assert.Contains("THE SCREEN HAS MOVED ON", string.Join(" | ", verdict.NotOnTheScreen),
            StringComparison.Ordinal);
        Assert.Contains("THE SCREEN HAS MOVED ON", verdict.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reason_quoting_words_that_ARE_on_the_screen_is_grounded()
    {
        const string reason =
            "the screen says 'Claude usage limit reached', which is the notice the instruction is about.";

        var verdict = RuleReasonGrounding.Check(reason, TheScreen);

        Assert.True(verdict.IsGrounded, verdict.Statement);
        Assert.Empty(verdict.NotOnTheScreen);
        Assert.NotEqual("", verdict.Statement);
    }

    [Fact]
    public void A_quotation_the_terminal_wrapped_across_lines_still_counts_as_being_on_the_screen()
    {
        var wrapped = "Claude usage limit\nreached. Your limit will reset at 2:30pm.";
        const string reason = "the screen says 'Claude usage limit reached'.";

        var verdict = RuleReasonGrounding.Check(reason, wrapped);

        Assert.True(verdict.IsGrounded, verdict.Statement);
    }

    [Fact]
    public void A_reason_that_quotes_nothing_says_so_rather_than_saying_nothing()
    {
        // THE PRESENCE. "Nothing was quoted, so there was nothing to check" is a different fact from "the
        // check never ran", and a statement that was blank in both cases could not tell them apart.
        var verdict = RuleReasonGrounding.Check(
            "the session is stopped on an allowance notice and the instruction covers it.", TheScreen);

        Assert.True(verdict.IsGrounded);
        Assert.NotEqual("", verdict.Statement);
        Assert.Contains("quoted", verdict.Statement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_reason_that_is_missing_altogether_is_not_silently_grounded()
    {
        var verdict = RuleReasonGrounding.Check(null, TheScreen);
        Assert.NotEqual("", verdict.Statement);
    }

    [Fact]
    public void A_screen_that_could_not_be_read_never_grounds_a_quotation()
    {
        // If the screen is empty, a quoted passage cannot have come from it. Answering "grounded" here
        // would be the fail-open: an absent screen certifying every claim made about it.
        var verdict = RuleReasonGrounding.Check("the screen says 'Claude usage limit reached'.", "");

        Assert.False(verdict.IsGrounded);
        Assert.NotEqual("", verdict.Statement);
    }

    [Fact]
    public void A_one_word_quotation_is_not_treated_as_a_claim_about_the_screen()
    {
        // A reason that puts the word 'act' in quotes is talking about its own decision, not quoting the
        // terminal, and matching a three-letter string against forty lines of output would be noise in
        // both directions.
        var verdict = RuleReasonGrounding.Check("the decision is 'act' because the notice is present.", TheScreen);

        Assert.True(verdict.IsGrounded, verdict.Statement);
    }

    [Fact]
    public void The_typographic_quotation_marks_a_model_writes_without_being_asked_are_read_too()
    {
        var reason = "the screen says \u201Cwhat is definitely not there at all\u201D.";

        var verdict = RuleReasonGrounding.Check(reason, TheScreen);

        Assert.False(verdict.IsGrounded);
    }
}
