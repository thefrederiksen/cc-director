using CcDirector.Terminal.Core;
using Xunit;
using static CcDirector.Core.Tests.TerminalTestHelper;

namespace CcDirector.Core.Tests;

/// <summary>
/// Background-Color-Erase (BCE) semantics for explicit erase commands.
/// Scroll-induced clears (ScrollUp/ScrollDown/InsertLines/DeleteLines) are
/// covered by AnsiParserXtermSnapshotTests, which validates the whole grid
/// against @xterm/headless for real capture streams. Any divergence in
/// scroll-BCE behavior shows up there.
/// </summary>
public class AnsiParserScrollBceTests
{
    private static readonly TerminalColor Blue = TerminalColor.FromRgb(36, 114, 200);
    private static readonly TerminalColor Cyan = TerminalColor.FromRgb(17, 168, 205);
    private static readonly TerminalColor Red = TerminalColor.FromRgb(205, 49, 49);

    [Fact]
    public void ExplicitEraseDisplay_ShouldUseBce()
    {
        var (parser, cells, _) = CreateParser(cols: 80, rows: 5);
        Parse(parser, "\x1b[44m"); // blue bg
        Parse(parser, "\x1b[2J");  // clear display
        Assert.Equal(Blue, cells[0, 0].Background);
        Assert.Equal(Blue, cells[79, 4].Background);
    }

    [Fact]
    public void ExplicitEraseLine_ShouldUseBce()
    {
        var (parser, cells, _) = CreateParser(cols: 80, rows: 5);
        Parse(parser, "Hello");
        Parse(parser, "\x1b[46m"); // cyan bg
        Parse(parser, "\x1b[2K");  // clear entire line
        Assert.Equal(Cyan, cells[0, 0].Background);
        Assert.Equal(Cyan, cells[79, 0].Background);
    }

    [Fact]
    public void EraseToEndOfLine_ShouldUseBce()
    {
        var (parser, cells, _) = CreateParser(cols: 80, rows: 5);
        Parse(parser, "ABCDEFGH");
        Parse(parser, "\x1b[5G");  // cursor to col 5 (1-based)
        Parse(parser, "\x1b[41m"); // red bg
        Parse(parser, "\x1b[K");   // erase to end of line

        Assert.Equal('A', cells[0, 0].Character);
        Assert.Equal(default(TerminalColor), cells[0, 0].Background);

        Assert.Equal(Red, cells[4, 0].Background);
        Assert.Equal(Red, cells[79, 0].Background);
    }
}
