using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// RULING A12, CHECKED AS A FIELD (phase 1, ruling P1-A): an act must cite one line of the screen it was
/// given, and the line must be there. The check runs against the excerpt the model was shown, with the
/// same normaliser and the same comparison a trigger word gets at authoring (fix round D, ruling D2) - so
/// a citation that would be accepted as a trigger word is accepted here, and one that would not is not.
///
/// A DECLINE needs no citation, and its record says which it had. The asymmetry is deliberate: declining
/// does nothing, so an unfaithful decline is recorded as it happened with the mismatch noted.
/// </summary>
public sealed class RuleReasonGroundingTests
{
    private const string TheNotice = "You've reached your Fable 5 limit. Run /usage-credits to continue.";

    private static readonly string TheExcerpt = RuleScreenExcerpt.Of(string.Join("\n", new[]
    {
        "> carry on with the refactor",
        "",
        TheNotice,
        "",
        ">",
    }));

    [Fact]
    public void A_line_that_is_on_the_screen_is_grounded_and_can_carry_an_act()
    {
        var grounding = RuleReasonGrounding.CheckQuote(TheNotice, TheExcerpt);

        Assert.True(grounding.IsGrounded);
        Assert.True(grounding.HasCitation);
        Assert.True(grounding.CanCarryAnAct);
        Assert.Empty(grounding.NotOnTheScreen);
        Assert.Contains(TheNotice, grounding.Statement, StringComparison.Ordinal);
        Assert.Contains("it is on it", grounding.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_that_is_not_on_the_screen_is_not_grounded_and_the_statement_names_it()
    {
        var grounding = RuleReasonGrounding.CheckQuote("YOUR SUBSCRIPTION HAS BEEN CANCELLED", TheExcerpt);

        Assert.False(grounding.IsGrounded);
        Assert.True(grounding.HasCitation);
        Assert.False(grounding.CanCarryAnAct);
        Assert.Equal("YOUR SUBSCRIPTION HAS BEEN CANCELLED", Assert.Single(grounding.NotOnTheScreen));
        Assert.Contains("does not contain", grounding.Statement, StringComparison.Ordinal);
        Assert.Contains("YOUR SUBSCRIPTION HAS BEEN CANCELLED", grounding.Statement, StringComparison.Ordinal);
    }

    /// <summary>
    /// NO CITATION CANNOT CARRY AN ACT, and the record says so. The first version of A12 called a reason
    /// that quoted nothing "grounded" because there was nothing to check - an absence read as a positive
    /// result - and an agent could act on evidence nobody could go back and verify.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_citation_cannot_carry_an_act_and_the_record_says_nothing_was_cited(string? quote)
    {
        var grounding = RuleReasonGrounding.CheckQuote(quote, TheExcerpt);

        Assert.True(grounding.IsGrounded, "nothing it said was contradicted");
        Assert.False(grounding.HasCitation);
        Assert.False(grounding.CanCarryAnAct);
        Assert.Contains("cites no line", grounding.Statement, StringComparison.Ordinal);
    }

    /// <summary>The word 'limit' on its own would be on forty lines of terminal output and prove nothing
    /// about which of them the model meant. A citation has to be long enough to be a claim.</summary>
    [Fact]
    public void A_citation_too_short_to_be_a_claim_about_the_screen_is_not_a_citation()
    {
        var grounding = RuleReasonGrounding.CheckQuote("limit", TheExcerpt);

        Assert.False(grounding.HasCitation);
        Assert.False(grounding.CanCarryAnAct);
        Assert.Contains("too short", grounding.Statement, StringComparison.Ordinal);
        Assert.Contains(RuleReasonGrounding.ShortestCheckablePassage.ToString(), grounding.Statement, StringComparison.Ordinal);
    }

    /// <summary>A partial line, copied exactly, is still a line of the screen. The model is asked for a
    /// whole line; a faithful fragment long enough to be a claim is not refused for being a fragment.</summary>
    [Fact]
    public void A_faithful_fragment_of_a_line_is_on_the_screen()
    {
        var grounding = RuleReasonGrounding.CheckQuote("reached your Fable 5 limit", TheExcerpt);

        Assert.True(grounding.CanCarryAnAct);
    }

    /// <summary>THE SAME COMPARISON A TRIGGER WORD GETS (ruling D2): case ignored, ends trimmed. A guard
    /// stricter than the matching it guards would refuse citations of lines the rule was allowed to
    /// fire on.</summary>
    [Fact]
    public void The_comparison_is_the_trigger_word_comparison_so_case_and_padding_do_not_refuse_a_real_line()
    {
        var padded = "   you've REACHED YOUR fable 5 LIMIT.   ";

        var grounding = RuleReasonGrounding.CheckQuote(padded, TheExcerpt);

        Assert.True(grounding.CanCarryAnAct);
        Assert.Empty(RuleTriggerWords.NotOn(new[] { padded }, TheExcerpt));
    }

    /// <summary>A citation is checked against the EXCERPT the model was shown, not the whole screen: a line
    /// above the forty-line window was never in front of the model, so citing it is citing from memory.</summary>
    [Fact]
    public void A_line_above_the_excerpt_the_model_was_shown_is_not_on_the_screen()
    {
        var lines = new List<string> { "THIS LINE IS ABOVE THE WINDOW AND WAS NEVER SHOWN" };
        for (var i = 0; i < RuleScreenExcerpt.Lines; i++) lines.Add("line " + i + " of ordinary output");
        var whole = string.Join("\n", lines);
        var excerpt = RuleScreenExcerpt.Of(whole);
        Assert.DoesNotContain("NEVER SHOWN", excerpt, StringComparison.Ordinal);

        var grounding = RuleReasonGrounding.CheckQuote("THIS LINE IS ABOVE THE WINDOW AND WAS NEVER SHOWN", excerpt);

        Assert.False(grounding.CanCarryAnAct);
        Assert.False(grounding.IsGrounded);
    }

    [Fact]
    public void A_screen_that_could_not_be_read_never_grounds_a_citation()
    {
        var grounding = RuleReasonGrounding.CheckQuote(TheNotice, null);

        Assert.False(grounding.IsGrounded);
        Assert.False(grounding.CanCarryAnAct);
    }

    [Fact]
    public void The_gateways_own_reason_has_a_statement_that_says_there_was_nothing_of_the_agents_to_check()
    {
        Assert.Contains("not applicable", RuleReasonGrounding.NotTheAgentsReason, StringComparison.Ordinal);
        Assert.Contains("nothing of the agent's", RuleReasonGrounding.NotTheAgentsReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE REAL LINE, GLYPHS AND ALL. This is the p01 corpus line as the terminal held it: spinner glyphs
    /// and redraw fragments, then the notice. On the first smoke run of the phase 1 contract the fast
    /// model was asked to copy the whole line and mangled the prefix - and the notice's first words - so
    /// the contract asks for the words only. There is NO glyph normaliser on either side: the quote is
    /// trimmed and looked for on the exact excerpt the question carried, with the same comparison a
    /// trigger word gets. A faithful words-only quote passes because it is a substring of the line; an
    /// invented one still fails. If a faithful quote ever fails here, the comparison is NOT to be loosened
    /// - a looser comparison is how a false act gets in - it is to be reported (Architect ruling, phase 1).
    /// </summary>
    private const string TheRealGlyphLadenLine =
        "\u2736*\u2722\u00b7\u2026g\u2722n*i\u2026\u2736rg\u273b\u273dendi\u273bnr\u2736*aeed\u2722\u00b7Mna\u2722e*M\u2736\u273b\u273d  \u23bf \u00a0" +
        "You've reached your Fable 5 limit. Run /usage-credits to continue or switch models with /model.";

    [Fact]
    public void A_faithful_words_only_quote_of_a_glyph_laden_real_line_is_on_the_screen()
    {
        var excerpt = RuleScreenExcerpt.Of("> carry on\n\n" + TheRealGlyphLadenLine + "\n\n>");
        Assert.Contains("\u2736", excerpt, StringComparison.Ordinal);

        var grounding = RuleReasonGrounding.CheckQuote(
            "You've reached your Fable 5 limit. Run /usage-credits to continue or switch models with /model.", excerpt);

        Assert.True(grounding.CanCarryAnAct, grounding.Statement);
    }

    [Fact]
    public void An_invented_quote_against_the_same_glyph_laden_line_still_fails()
    {
        var excerpt = RuleScreenExcerpt.Of("> carry on\n\n" + TheRealGlyphLadenLine + "\n\n>");

        var grounding = RuleReasonGrounding.CheckQuote("GETGLOBAL limit. Run /usage-credits to continue", excerpt);

        Assert.False(grounding.CanCarryAnAct);
        Assert.False(grounding.IsGrounded);
    }
}
