using CcDirector.Terminal.Core;
using Xunit;
using static CcDirector.Core.Tests.TerminalTestHelper;

namespace CcDirector.Core.Tests;

/// <summary>
/// Tests for the blue-background-on-scroll bug.
///
/// The bug: ScrollUp/ScrollDown/InsertLines/DeleteLines clear newly exposed rows
/// using BceCell() which inherits the current _bg. If _bg is blue/cyan at scroll time,
/// the cleared row gets blue backgrounds on every cell. Claude Code's differential
/// rendering then only overwrites the cells it needs, leaving blue on the rest.
/// Resize fixes it because it triggers a full CLI redraw.
///
/// The fix: scroll operations should clear with default background.
/// Explicit erase (ESC[J, ESC[K) should still use BCE -- that's correct behavior.
/// </summary>
public class AnsiParserScrollBceTests
{
    private static readonly TerminalColor Blue = TerminalColor.FromRgb(36, 114, 200);
    private static readonly TerminalColor Cyan = TerminalColor.FromRgb(17, 168, 205);
    private static readonly TerminalColor Red = TerminalColor.FromRgb(205, 49, 49);

    // ---------------------------------------------------------------
    // These tests should FAIL on the buggy code, PASS after the fix.
    // ---------------------------------------------------------------

    [Fact]
    public void ScrollUp_WithColoredBg_BottomRowShouldHaveDefaultBg()
    {
        // 5-row terminal so scroll triggers quickly
        var (parser, cells, _) = CreateParser(cols: 80, rows: 5);

        // Fill screen with normal text
        for (int i = 0; i < 5; i++)
            Parse(parser, $"Line {i}\r\n");

        // Set blue background, then write more (triggers scroll)
        Parse(parser, "\x1b[44m");      // SGR 44 = blue bg
        Parse(parser, "Blue line\r\n"); // this scroll clears bottom row

        // The bottom row was cleared by scroll.
        // BUG: it gets blue bg from BceCell(). FIX: it should get default bg.
        AssertRangeDefaultBackground(cells, row: 4, startCol: 10, endCol: 79);
    }

    [Fact]
    public void ScrollDown_WithColoredBg_TopRowShouldHaveDefaultBg()
    {
        var (parser, cells, _) = CreateParser(cols: 80, rows: 5);

        // Fill screen
        for (int i = 0; i < 5; i++)
            Parse(parser, $"Line {i}\r\n");

        // Set cyan bg, then scroll down (ESC[T)
        Parse(parser, "\x1b[46m");  // SGR 46 = cyan bg
        Parse(parser, "\x1b[T");    // CSI T = scroll down 1

        // Top row was cleared by ScrollDown.
        // BUG: it gets cyan bg. FIX: should be default.
        AssertRangeDefaultBackground(cells, row: 0, startCol: 0, endCol: 79);
    }

    [Fact]
    public void InsertLines_WithColoredBg_ClearedRowShouldHaveDefaultBg()
    {
        var (parser, cells, _) = CreateParser(cols: 80, rows: 5);

        // Fill screen
        for (int i = 0; i < 5; i++)
            Parse(parser, $"Line {i}\r\n");

        // Move cursor to row 1, set blue bg, insert a line
        Parse(parser, "\x1b[44m");   // blue bg
        Parse(parser, "\x1b[2;1H");  // cursor to row 2 (1-based)
        Parse(parser, "\x1b[L");     // CSI L = insert 1 line

        // The inserted row (at cursor position, row 1) was cleared.
        // BUG: gets blue bg. FIX: should be default.
        AssertRangeDefaultBackground(cells, row: 1, startCol: 0, endCol: 79);
    }

    [Fact]
    public void DeleteLines_WithColoredBg_ExposedRowShouldHaveDefaultBg()
    {
        var (parser, cells, _) = CreateParser(cols: 80, rows: 5);

        // Fill screen
        for (int i = 0; i < 5; i++)
            Parse(parser, $"Line {i}\r\n");

        // Move cursor to row 1, set red bg, delete a line
        Parse(parser, "\x1b[41m");   // red bg
        Parse(parser, "\x1b[2;1H");  // cursor to row 2 (1-based)
        Parse(parser, "\x1b[M");     // CSI M = delete 1 line

        // Bottom row was exposed by shifting up.
        // BUG: gets red bg. FIX: should be default.
        AssertRangeDefaultBackground(cells, row: 4, startCol: 0, endCol: 79);
    }

    [Fact]
    public void MultipleScrolls_WithColoredBg_NoLeaking()
    {
        var (parser, cells, _) = CreateParser(cols: 80, rows: 5);

        // Set cyan bg, write enough lines to scroll multiple times
        Parse(parser, "\x1b[46m");
        for (int i = 0; i < 10; i++)
            Parse(parser, $"Scroll line {i}\r\n");

        // Reset, write clean text
        Parse(parser, "\x1b[0m");
        Parse(parser, "Clean line");

        // Every empty cell in the grid should have default bg
        // (the written cells will have cyan bg from when they were written,
        //  but the EMPTY cells from scroll clears should not)
        for (int c = 10; c < 80; c++)
        {
            var cell = cells[c, 4];
            Assert.True(
                cell.Background == default,
                $"Empty cell [{c},4] has leaked bg -- should be default after scroll");
        }
    }

    // ---------------------------------------------------------------
    // These tests should PASS both BEFORE and AFTER the fix.
    // They verify that explicit erase commands still use BCE correctly.
    // ---------------------------------------------------------------

    [Fact]
    public void ExplicitEraseDisplay_ShouldStillUseBce()
    {
        var (parser, cells, _) = CreateParser(cols: 80, rows: 5);

        // Set blue bg, then clear entire display
        Parse(parser, "\x1b[44m"); // blue bg
        Parse(parser, "\x1b[2J");  // ESC[2J = clear display

        // Explicit erase SHOULD use BCE (blue bg)
        Assert.Equal(Blue, cells[0, 0].Background);
        Assert.Equal(Blue, cells[79, 4].Background);
    }

    [Fact]
    public void ExplicitEraseLine_ShouldStillUseBce()
    {
        var (parser, cells, _) = CreateParser(cols: 80, rows: 5);

        // Write text, set cyan bg, erase entire line
        Parse(parser, "Hello");
        Parse(parser, "\x1b[46m"); // cyan bg
        Parse(parser, "\x1b[2K");  // ESC[2K = clear line

        // Explicit line erase SHOULD use BCE (cyan bg)
        Assert.Equal(Cyan, cells[0, 0].Background);
        Assert.Equal(Cyan, cells[79, 0].Background);
    }

    [Fact]
    public void EraseToEndOfLine_ShouldStillUseBce()
    {
        var (parser, cells, _) = CreateParser(cols: 80, rows: 5);

        // Write text, move cursor, set red bg, erase to end
        Parse(parser, "ABCDEFGH");
        Parse(parser, "\x1b[5G");  // cursor to col 5 (1-based)
        Parse(parser, "\x1b[41m"); // red bg
        Parse(parser, "\x1b[K");   // ESC[K = erase to end of line

        // Cells before cursor should be unchanged
        Assert.Equal('A', cells[0, 0].Character);
        Assert.Equal(default(TerminalColor), cells[0, 0].Background);

        // Cells from cursor onward should have red bg (BCE)
        Assert.Equal(Red, cells[4, 0].Background);
        Assert.Equal(Red, cells[79, 0].Background);
    }
}
