using CcDirector.Launcher;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// The matching rule shared by the application catalogue and the file search. These tests pin the two
/// behaviours a caller relies on without being told: a wildcard pattern anchors, and plain text does not.
/// </summary>
public sealed class SearchPatternTests
{
    [Fact]
    public void Parse_EmptyQuery_MatchesEverything()
    {
        var pattern = SearchPattern.Parse("");

        Assert.True(pattern.MatchesEverything);
        Assert.True(pattern.IsMatch("anything at all"));
    }

    [Fact]
    public void Parse_NullQuery_MatchesEverything()
    {
        Assert.True(SearchPattern.Parse(null).IsMatch("anything at all"));
    }

    [Fact]
    public void IsMatch_PlainText_MatchesAnywhereInTheName()
    {
        var pattern = SearchPattern.Parse("budget");

        Assert.True(pattern.IsMatch("Q3-budget-final.xlsx"));
        Assert.True(pattern.IsMatch("budget.txt"));
        Assert.False(pattern.IsMatch("forecast.xlsx"));
    }

    [Fact]
    public void IsMatch_PlainText_IgnoresCase()
    {
        Assert.True(SearchPattern.Parse("BUDGET").IsMatch("q3-budget.xlsx"));
    }

    /// <summary>
    /// The anchoring rule, and the reason it exists: an unanchored "*.pptx" would also match a text file whose
    /// name merely contains "pptx", which is not what anyone typing that means.
    /// </summary>
    [Fact]
    public void IsMatch_Wildcard_MustMatchTheWholeName()
    {
        var pattern = SearchPattern.Parse("*.pptx");

        Assert.True(pattern.IsMatch("quarterly-review.pptx"));
        Assert.False(pattern.IsMatch("pptx-notes.txt"));
        Assert.False(pattern.IsMatch("deck.pptx.bak"));
    }

    [Fact]
    public void IsMatch_QuestionMarkWildcard_MatchesExactlyOneCharacter()
    {
        var pattern = SearchPattern.Parse("report?.pdf");

        Assert.True(pattern.IsMatch("report1.pdf"));
        Assert.False(pattern.IsMatch("report.pdf"));
        Assert.False(pattern.IsMatch("report12.pdf"));
    }

    /// <summary>
    /// Regression guard: the dot in "*.pptx" must be a literal dot, not the regular-expression "any
    /// character". Without escaping, "*.pptx" would match "deckXpptx".
    /// </summary>
    [Fact]
    public void IsMatch_Wildcard_TreatsRegularExpressionPunctuationAsLiteralText()
    {
        Assert.False(SearchPattern.Parse("*.pptx").IsMatch("deckXpptx"));
        Assert.True(SearchPattern.Parse("notes+*.txt").IsMatch("notes+draft.txt"));
        Assert.False(SearchPattern.Parse("notes+*.txt").IsMatch("notesss.txt"));
    }
}
