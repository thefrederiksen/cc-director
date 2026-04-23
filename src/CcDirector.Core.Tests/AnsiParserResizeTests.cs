using CcDirector.Terminal.Core;
using Xunit;
using static CcDirector.Core.Tests.TerminalTestHelper;

namespace CcDirector.Core.Tests;

/// <summary>
/// Resize behavior of the AnsiParser. The terminal control creates the parser
/// with default 120x30 dimensions before layout completes, then calls
/// UpdateGrid once Bounds are known. The scroll region must follow the new
/// screen size or LineFeed scrolls only the original 30 rows -- leaving a dead
/// band below the prompt and feeding blank rows into scrollback.
/// </summary>
public class AnsiParserResizeTests
{
    [Fact]
    public void UpdateGrid_GrowingFromDefault_ExpandsScrollRegionToFullScreen()
    {
        var (parser, _, _) = CreateParser(cols: 120, rows: 30);

        var newCells = new TerminalCell[147, 50];
        parser.UpdateGrid(newCells, 147, 50);

        var diag = parser.GetDiagnosticState();
        Assert.Equal(0, diag.ScrollTop);
        Assert.Equal(49, diag.ScrollBottom);
    }

    [Fact]
    public void UpdateGrid_PreservesPartialScrollRegionWhenIntentionallySet()
    {
        var (parser, _, _) = CreateParser(cols: 120, rows: 30);
        Parse(parser, "\x1b[5;20r"); // DECSTBM: scroll region rows 5-20 (1-based)

        var newCells = new TerminalCell[120, 50];
        parser.UpdateGrid(newCells, 120, 50);

        var diag = parser.GetDiagnosticState();
        Assert.Equal(4, diag.ScrollTop);
        Assert.Equal(19, diag.ScrollBottom);
    }

    [Fact]
    public void UpdateGrid_ShrinkingScreen_ClampsScrollRegion()
    {
        var (parser, _, _) = CreateParser(cols: 120, rows: 50);

        var newCells = new TerminalCell[120, 25];
        parser.UpdateGrid(newCells, 120, 25);

        var diag = parser.GetDiagnosticState();
        Assert.Equal(0, diag.ScrollTop);
        Assert.Equal(24, diag.ScrollBottom);
    }

    /// <summary>
    /// Reproduces the gap-below-prompt bug. Before the fix, LineFeed past the
    /// stale 30-row scrollBottom kept scrolling rows 0..29 and the new bottom
    /// rows (30..49) were a dead zone. After the fix, LineFeed reaches all the
    /// way to row 49 with no scrolling, so no blank lines leak into scrollback.
    /// </summary>
    [Fact]
    public void LineFeedAfterGrow_ScrollsAtNewBottom_NotOldBottom()
    {
        var (parser, _, scrollback) = CreateParser(cols: 120, rows: 30);

        var newCells = new TerminalCell[120, 50];
        parser.UpdateGrid(newCells, 120, 50);

        // Cursor home, then 49 line feeds: should reach row 49 without scrolling.
        Parse(parser, "\x1b[H");
        for (int i = 0; i < 49; i++) Parse(parser, "\n");

        var diag = parser.GetDiagnosticState();
        Assert.Equal(49, diag.CursorRow);
        Assert.Empty(scrollback);

        // One more LF: cursor at scrollBottom, scrolls one row into scrollback.
        Parse(parser, "\n");
        Assert.Single(scrollback);
        Assert.Equal(49, parser.GetDiagnosticState().CursorRow);
    }
}
