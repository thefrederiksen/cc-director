using System.Text;
using CcDirector.Terminal.Core;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// SegmentedReplay parses raw terminal bytes at the geometry each byte was originally emitted
/// for, applying recorded resizes at their byte positions (issue #1304). The invariant under
/// test: a segmented replay produces exactly the grid and scrollback that a live parser
/// produced when it was resized at the same points - and therefore wide committed history is
/// never falsely re-wrapped at a narrower later width.
/// </summary>
public class SegmentedReplayTests
{
    private const int MaxScrollback = 1000;

    // ---------------------------------------------------------------- helpers

    private static byte[] Lines(string prefix, int count, int width)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            string label = $"{prefix}{i:D2}";
            sb.Append(label).Append('=', width - label.Length).Append("\r\n");
        }
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string RowText(TerminalCell[] row)
    {
        var chars = new char[row.Length];
        for (int i = 0; i < row.Length; i++)
            chars[i] = row[i].Character == '\0' ? ' ' : row[i].Character;
        return new string(chars).TrimEnd();
    }

    private static string GridText(TerminalCell[,] cells)
    {
        int cols = cells.GetLength(0), rows = cells.GetLength(1);
        var sb = new StringBuilder();
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
                sb.Append(cells[c, r].Character == '\0' ? ' ' : cells[c, r].Character);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The live resize path: truncate-copy into a new grid, then UpdateGrid.</summary>
    private static TerminalCell[,] LiveResize(AnsiParser parser, TerminalCell[,] cells, int newCols, int newRows)
    {
        var next = new TerminalCell[newCols, newRows];
        int copyCols = Math.Min(cells.GetLength(0), newCols);
        int copyRows = Math.Min(cells.GetLength(1), newRows);
        for (int r = 0; r < copyRows; r++)
            for (int c = 0; c < copyCols; c++)
                next[c, r] = cells[c, r];
        parser.UpdateGrid(next, newCols, newRows);
        return next;
    }

    // ------------------------------------------------------------------ tests

    [Fact]
    public void Replay_NoResizes_MatchesSinglePassParse()
    {
        byte[] data = Lines("line", 30, 70);

        var singleScrollback = new List<TerminalCell[]>();
        var singleCells = new TerminalCell[80, 10];
        var singleParser = new AnsiParser(singleCells, 80, 10, singleScrollback, MaxScrollback);
        singleParser.Parse(data);

        var replayScrollback = new List<TerminalCell[]>();
        var (replayCells, _) = SegmentedReplay.Replay(
            data, 80, 10, Array.Empty<ReplayResize>(), 80, 10, replayScrollback, MaxScrollback);

        Assert.Equal(GridText(singleCells), GridText(replayCells));
        Assert.Equal(singleScrollback.Count, replayScrollback.Count);
        for (int i = 0; i < singleScrollback.Count; i++)
            Assert.Equal(RowText(singleScrollback[i]), RowText(replayScrollback[i]));
    }

    [Fact]
    public void Replay_MatchesLiveParserAcrossResize()
    {
        // Live history: 20 wide lines at 150 columns, then a shrink to 129, then 10 more lines.
        byte[] chunk1 = Lines("WIDE", 20, 142);
        byte[] chunk2 = Lines("post", 10, 100);
        byte[] all = chunk1.Concat(chunk2).ToArray();

        // The live parser saw: parse chunk1 at 150x8, resize to 129x8, parse chunk2.
        var liveScrollback = new List<TerminalCell[]>();
        var liveCells = new TerminalCell[150, 8];
        var liveParser = new AnsiParser(liveCells, 150, 8, liveScrollback, MaxScrollback);
        liveParser.Parse(chunk1);
        liveCells = LiveResize(liveParser, liveCells, 129, 8);
        liveParser.Parse(chunk2);

        // The segmented replay of the recorded bytes plus the recorded resize.
        var replayScrollback = new List<TerminalCell[]>();
        var (replayCells, _) = SegmentedReplay.Replay(
            all, 150, 8,
            new[] { new ReplayResize(chunk1.Length, 129, 8) },
            129, 8, replayScrollback, MaxScrollback);

        Assert.Equal(GridText(liveCells), GridText(replayCells));
        Assert.Equal(liveScrollback.Count, replayScrollback.Count);
        for (int i = 0; i < liveScrollback.Count; i++)
        {
            Assert.Equal(liveScrollback[i].Length, replayScrollback[i].Length);
            Assert.Equal(RowText(liveScrollback[i]), RowText(replayScrollback[i]));
        }
    }

    [Fact]
    public void Replay_WideHistoryShrunkLater_KeepsOriginalWidthIntact()
    {
        // The bug in issue #1304: 142-column content written at 150 columns, replayed after
        // the pane shrank to 129, was falsely wrapped into a full row plus a remainder row.
        byte[] chunk1 = Lines("WIDE", 20, 142);
        byte[] chunk2 = Lines("post", 10, 100);
        byte[] all = chunk1.Concat(chunk2).ToArray();

        var scrollback = new List<TerminalCell[]>();
        SegmentedReplay.Replay(
            all, 150, 8,
            new[] { new ReplayResize(chunk1.Length, 129, 8) },
            129, 8, scrollback, MaxScrollback);

        // Rows that reached scrollback BEFORE the shrink keep their full emission
        // width. (Rows still on screen at the shrink are truncated by the live
        // truncate-copy - faithful to what the live parser did - and the real
        // application repaints those; a synthetic stream has no repaint.)
        var intactWideRows = scrollback.Where(row => RowText(row).Length == 142).ToList();
        Assert.NotEmpty(intactWideRows);
        foreach (var row in intactWideRows)
        {
            Assert.StartsWith("WIDE", RowText(row));
            Assert.Equal(150, row.Length); // stored at its emission width, not re-wrapped
        }

        // The defining symptom of the bug is absent: no shattered remainder rows,
        // which a 142-column line falsely wrapped at 129 columns would leave as a
        // bare run of the padding character.
        Assert.DoesNotContain(scrollback, row =>
        {
            string text = RowText(row);
            return text.Length > 0 && text.Length <= 142 - 129 && text.All(ch => ch == '=');
        });
    }

    [Fact]
    public void Replay_GrowingBackAfterShrink_RestoresWideHistory()
    {
        // Shrink to 129 then widen back to 150: wide rows stay intact throughout,
        // which the old parse-everything-at-current-width rebuild could not do.
        byte[] chunk1 = Lines("WIDE", 20, 142);
        byte[] chunk2 = Lines("post", 10, 100);
        byte[] all = chunk1.Concat(chunk2).ToArray();

        var scrollback = new List<TerminalCell[]>();
        var (cells, _) = SegmentedReplay.Replay(
            all, 150, 8,
            new[] { new ReplayResize(chunk1.Length, 129, 8) },
            150, 8, scrollback, MaxScrollback);

        Assert.Equal(150, cells.GetLength(0));
        // The rows that reached scrollback before the shrink survived it at full
        // width and are fully visible again at 150 columns.
        var intactWideRows = scrollback.Where(row => RowText(row).Length == 142).ToList();
        Assert.NotEmpty(intactWideRows);
        foreach (var row in intactWideRows)
            Assert.Equal(150, row.Length);
    }

    [Fact]
    public void Replay_FinalGeometryIsAlwaysApplied()
    {
        byte[] data = Lines("line", 5, 40);

        var (cells, parser) = SegmentedReplay.Replay(
            data, 150, 40, Array.Empty<ReplayResize>(), 129, 41,
            new List<TerminalCell[]>(), MaxScrollback);

        Assert.Equal(129, cells.GetLength(0));
        Assert.Equal(41, cells.GetLength(1));
        // The parser was resized along with the grid, ready for live continuation.
        var (cursorCol, cursorRow) = parser.GetCursorPosition();
        Assert.InRange(cursorCol, 0, 128);
        Assert.InRange(cursorRow, 0, 40);
    }

    [Fact]
    public void Replay_EmptyData_ReturnsGridAtFinalGeometry()
    {
        var (cells, _) = SegmentedReplay.Replay(
            Array.Empty<byte>(), 150, 40, Array.Empty<ReplayResize>(), 129, 41,
            new List<TerminalCell[]>(), MaxScrollback);

        Assert.Equal(129, cells.GetLength(0));
        Assert.Equal(41, cells.GetLength(1));
    }

    [Fact]
    public void Replay_MultipleResizes_AppliedInOrder()
    {
        // Three eras: 150, 133, 129 - the exact sequence observed in issue #1304.
        byte[] chunk1 = Lines("era1", 10, 142);
        byte[] chunk2 = Lines("era2", 10, 120);
        byte[] chunk3 = Lines("era3", 10, 100);
        byte[] all = chunk1.Concat(chunk2).Concat(chunk3).ToArray();

        var scrollback = new List<TerminalCell[]>();
        SegmentedReplay.Replay(
            all, 150, 8,
            new[]
            {
                new ReplayResize(chunk1.Length, 133, 8),
                new ReplayResize(chunk1.Length + chunk2.Length, 129, 8),
            },
            129, 8, scrollback, MaxScrollback);

        // Rows that reached scrollback within their own era keep that era's width.
        var era1Intact = scrollback.Where(r => RowText(r).StartsWith("era1") && RowText(r).Length == 142).ToList();
        Assert.NotEmpty(era1Intact);
        foreach (var row in era1Intact)
            Assert.Equal(150, row.Length);

        // Every era2 row's 120-character content is intact (120 fits both later widths,
        // so none may wrap). Rows scrolled out during their own era are stored at 133;
        // rows still on screen at the era3 shrink are stored at 129 - both unharmed.
        var era2Intact = scrollback.Where(r => RowText(r).StartsWith("era2") && RowText(r).Length == 120).ToList();
        Assert.NotEmpty(era2Intact);
        Assert.All(era2Intact, row => Assert.True(row.Length >= 120));
        Assert.Contains(era2Intact, row => row.Length == 133);
    }
}
