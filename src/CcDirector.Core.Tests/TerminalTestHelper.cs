using System.Text;
using CcDirector.Terminal.Core;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Shared helpers for AnsiParser tests.
/// </summary>
public static class TerminalTestHelper
{
    public static (AnsiParser Parser, TerminalCell[,] Cells, List<TerminalCell[]> Scrollback) CreateParser(
        int cols = 80, int rows = 24)
    {
        var cells = new TerminalCell[cols, rows];
        var scrollback = new List<TerminalCell[]>();
        var parser = new AnsiParser(cells, cols, rows, scrollback, 100);
        return (parser, cells, scrollback);
    }

    public static void Parse(AnsiParser parser, string text)
    {
        parser.Parse(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// Assert that every cell in a row has the expected background color.
    /// </summary>
    public static void AssertRowBackground(TerminalCell[,] cells, int row, TerminalColor expected, int cols)
    {
        for (int c = 0; c < cols; c++)
        {
            var actual = cells[c, row].Background;
            Assert.True(
                actual == expected,
                $"Cell [{c},{row}]: expected bg {FormatColor(expected)} but got {FormatColor(actual)}");
        }
    }

    /// <summary>
    /// Assert that all empty/space cells in the grid have default (transparent) background.
    /// This catches background color leaking into cells the CLI never wrote to.
    /// </summary>
    public static void AssertNoBackgroundLeaking(TerminalCell[,] cells, int cols, int rows)
    {
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var cell = cells[c, r];
                if (cell.Character == '\0' || cell.Character == ' ')
                {
                    Assert.True(
                        cell.Background == default,
                        $"Empty cell [{c},{r}] has leaked bg {FormatColor(cell.Background)} -- should be default");
                }
            }
        }
    }

    /// <summary>
    /// Assert that all cells in a specific range of a row have default background.
    /// </summary>
    public static void AssertRangeDefaultBackground(TerminalCell[,] cells, int row, int startCol, int endCol)
    {
        for (int c = startCol; c <= endCol; c++)
        {
            var actual = cells[c, row].Background;
            Assert.True(
                actual == default,
                $"Cell [{c},{row}]: expected default bg but got {FormatColor(actual)}");
        }
    }

    private static string FormatColor(TerminalColor c)
    {
        if (c == default) return "default(0,0,0,A=0)";
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}(A={c.A})";
    }
}
