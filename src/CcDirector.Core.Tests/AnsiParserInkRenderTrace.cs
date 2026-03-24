using System.Text;
using CcDirector.Terminal.Core;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.Tests;

public class AnsiParserInkRenderTrace
{
    private readonly ITestOutputHelper _output;

    public AnsiParserInkRenderTrace(ITestOutputHelper output) => _output = output;

    private static byte[] LoadCapture(string filename)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData");
        var path = Path.Combine(dir, filename);
        if (File.Exists(path)) return File.ReadAllBytes(path);
        var projectDir = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "TestData");
        path = Path.Combine(projectDir, filename);
        if (File.Exists(path)) return File.ReadAllBytes(path);
        throw new FileNotFoundException($"Capture file not found: {filename}");
    }

    [Fact]
    public void TraceHowStrayCharsGetOntoRow()
    {
        // We know cell [5,17] ends up with 'e'. Let's trace backwards:
        // It gets there via scroll from row 18. Before that, what was at [5,18]?
        // And before THAT scroll, what wrote 'e' to that position?
        //
        // Strategy: parse in 100-byte chunks, track cell [5, R] for ALL rows.
        // When a scroll happens, we can see what row the 'e' was originally on.

        var rawBytes = LoadCapture("claude-stray-chars.bin");
        var cells = new TerminalCell[147, 50];
        var scrollback = new List<TerminalCell[]>();
        var parser = new AnsiParser(cells, 147, 50, scrollback, 1000);

        // Track which row the stray 'e' is on at col 5
        // It starts at some row and moves up via scrolls
        int prevScrollback = 0;

        // Parse in chunks and watch for the 'e' at col 5 appearing on any row
        for (int offset = 0; offset < rawBytes.Length; offset += 100)
        {
            int len = Math.Min(100, rawBytes.Length - offset);
            var chunk = new byte[len];
            Array.Copy(rawBytes, offset, chunk, 0, len);
            parser.Parse(chunk);

            // Detect scrolls
            if (scrollback.Count != prevScrollback)
            {
                int scrollsDone = scrollback.Count - prevScrollback;
                prevScrollback = scrollback.Count;

                // After scroll, check if 'e' exists at col 5 on any row
                for (int r = 0; r < 50; r++)
                {
                    if (cells[5, r].Character == 'e')
                    {
                        // Check if this is an isolated 'e' (stray char pattern)
                        bool leftEmpty = cells[4, r].Character == '\0' || cells[4, r].Character == ' ';
                        bool rightEmpty = cells[6, r].Character == '\0' || cells[6, r].Character == ' ';
                        if (leftEmpty && rightEmpty)
                        {
                            // Found isolated 'e' - this might be our stray
                            // Don't log every occurrence, just significant ones
                        }
                    }
                }
            }
        }

        // Now do a targeted analysis: parse up to the point where row 17 gets the stray 'e'
        // from the earlier trace we know it's at byte ~438300
        // Let's parse to ~438200 and dump the rows around where the content is

        cells = new TerminalCell[147, 50];
        scrollback = new List<TerminalCell[]>();
        parser = new AnsiParser(cells, 147, 50, scrollback, 1000);

        // Parse to just before the stray chars appear
        var beforeStray = new byte[438200];
        Array.Copy(rawBytes, 0, beforeStray, 0, 438200);
        parser.Parse(beforeStray);

        _output.WriteLine($"State at byte 438200 (scrollback={scrollback.Count}):");
        _output.WriteLine($"Cursor: {parser.GetCursorPosition()}");
        _output.WriteLine("");

        // Find which rows have isolated chars at col 5
        for (int r = 0; r < 50; r++)
        {
            char ch5 = cells[5, r].Character;
            if (ch5 != '\0' && ch5 != ' ')
            {
                bool leftEmpty = cells[4, r].Character == '\0' || cells[4, r].Character == ' ';
                bool rightEmpty = cells[6, r].Character == '\0' || cells[6, r].Character == ' ';

                if (leftEmpty && rightEmpty)
                {
                    // Dump this row
                    var sb = new StringBuilder();
                    for (int c = 0; c < 147; c++)
                    {
                        char ch = cells[c, r].Character;
                        sb.Append(ch == '\0' ? '.' : ch);
                    }
                    _output.WriteLine($"Row {r:D2} (isolated '{ch5}' at col 5): {sb.ToString().TrimEnd('.')}");
                }
            }
        }

        // Now parse the remaining bytes to see the stray chars move to their final positions
        var remaining = new byte[rawBytes.Length - 438200];
        Array.Copy(rawBytes, 438200, remaining, 0, remaining.Length);
        parser.Parse(remaining);

        _output.WriteLine($"\nFinal state (scrollback={scrollback.Count}):");
        // Dump row 17
        var finalRow17 = new StringBuilder();
        for (int c = 0; c < 147; c++)
        {
            char ch = cells[c, 17].Character;
            finalRow17.Append(ch == '\0' ? '.' : ch);
        }
        _output.WriteLine($"Row 17: {finalRow17.ToString().TrimEnd('.')}");
    }

    [Fact]
    public void FindWhereIsolatedCharsFirstAppear()
    {
        // Parse byte-by-byte (in 100-byte chunks) and find the FIRST time
        // an isolated char appears at col 5 on ANY row.
        // This tells us which ANSI command creates it.

        var rawBytes = LoadCapture("claude-stray-chars.bin");
        var cells = new TerminalCell[147, 50];
        var scrollback = new List<TerminalCell[]>();
        var parser = new AnsiParser(cells, 147, 50, scrollback, 1000);

        bool found = false;

        for (int offset = 0; offset < rawBytes.Length && !found; offset += 50)
        {
            int len = Math.Min(50, rawBytes.Length - offset);
            var chunk = new byte[len];
            Array.Copy(rawBytes, offset, chunk, 0, len);

            // Snapshot col 5 on all rows before parsing
            var beforeSnapshot = new char[50];
            for (int r = 0; r < 50; r++)
                beforeSnapshot[r] = cells[5, r].Character;

            parser.Parse(chunk);

            // Check if any isolated 'e' appeared at col 5
            for (int r = 0; r < 50; r++)
            {
                char after = cells[5, r].Character;
                if (after != beforeSnapshot[r] && after == 'e')
                {
                    bool leftEmpty = cells[4, r].Character == '\0' || cells[4, r].Character == ' ';
                    bool rightEmpty = cells[6, r].Character == '\0' || cells[6, r].Character == ' ';
                    if (leftEmpty && rightEmpty)
                    {
                        var (curCol, curRow) = parser.GetCursorPosition();
                        _output.WriteLine($"FIRST isolated 'e' at col 5 row {r} appeared at byte offset {offset}");
                        _output.WriteLine($"Cursor: ({curCol},{curRow}), scrollback: {scrollback.Count}");

                        // Dump ANSI context
                        int ctxStart = Math.Max(0, offset - 300);
                        int ctxEnd = Math.Min(rawBytes.Length, offset + len + 100);
                        var ctx = new byte[ctxEnd - ctxStart];
                        Array.Copy(rawBytes, ctxStart, ctx, 0, ctx.Length);
                        var text = Encoding.UTF8.GetString(ctx)
                            .Replace("\x1b", "\nESC")
                            .Replace("\r", "\\r")
                            .Replace("\n", "\n");
                        _output.WriteLine($"\nANSI context (bytes {ctxStart}-{ctxEnd}):\n{text}");

                        // Dump the row
                        var sb = new StringBuilder();
                        for (int c = 0; c < 147; c++)
                        {
                            char ch = cells[c, r].Character;
                            sb.Append(ch == '\0' ? '.' : ch);
                        }
                        _output.WriteLine($"\nRow {r}: {sb.ToString().TrimEnd('.')}");

                        found = true;
                        break;
                    }
                }
            }
        }

        if (!found)
            _output.WriteLine("No isolated 'e' found at col 5");
    }
}
