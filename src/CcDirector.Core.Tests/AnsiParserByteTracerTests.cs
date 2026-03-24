using System.Text;
using CcDirector.Terminal.Core;
using Xunit;
using Xunit.Abstractions;
using static CcDirector.Core.Tests.TerminalTestHelper;

namespace CcDirector.Core.Tests;

/// <summary>
/// Traces every write to specific cells to find what ANSI sequence puts stray chars there.
/// We know row 17 col 5 has a stray 'e'. This test instruments the parser to find
/// every time cell [5,17] is written to.
/// </summary>
public class AnsiParserByteTracerTests
{
    private readonly ITestOutputHelper _output;

    public AnsiParserByteTracerTests(ITestOutputHelper output) => _output = output;

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
    public void TraceWhatWritesToStrayPositions()
    {
        var rawBytes = LoadCapture("claude-stray-chars.bin");

        // Target cells where stray chars appear
        var targets = new HashSet<(int col, int row)>
        {
            (5, 17), (10, 17), (16, 17), (71, 17), (74, 17),
            (5, 25), (8, 25), (15, 25), (26, 25), (29, 25),
        };

        // We can't easily instrument AnsiParser without modifying it.
        // Instead, parse in chunks and check after each chunk if target cells changed.
        var cells = new TerminalCell[147, 50];
        var scrollback = new List<TerminalCell[]>();
        var parser = new AnsiParser(cells, 147, 50, scrollback, 1000);

        // Track what's in target cells
        var prevState = new Dictionary<(int, int), char>();
        foreach (var t in targets)
            prevState[t] = '\0';

        // Parse byte by byte in small chunks to catch the exact moment
        int chunkSize = 100;
        for (int offset = 0; offset < rawBytes.Length; offset += chunkSize)
        {
            int len = Math.Min(chunkSize, rawBytes.Length - offset);
            var chunk = new byte[len];
            Array.Copy(rawBytes, offset, chunk, 0, len);
            parser.Parse(chunk);

            // Check if any target cell changed
            foreach (var t in targets)
            {
                char current = cells[t.col, t.row].Character;
                if (current != prevState[t])
                {
                    var (curCol, curRow) = parser.GetCursorPosition();
                    _output.WriteLine(
                        $"Cell [{t.col},{t.row}] changed: '{prevState[t]}' -> '{current}' " +
                        $"at byte offset {offset}-{offset + len} " +
                        $"(cursor now at col={curCol} row={curRow}, scrollback={scrollback.Count})");
                    prevState[t] = current;
                }
            }
        }

        _output.WriteLine("");
        _output.WriteLine("=== Final state of target cells ===");
        foreach (var t in targets)
        {
            char ch = cells[t.col, t.row].Character;
            _output.WriteLine($"  [{t.col},{t.row}] = '{(ch == '\0' ? '.' : ch)}'");
        }
    }

    [Fact]
    public void TraceRow17ByteByByte()
    {
        // Parse byte-by-byte near where row 17 gets written
        // First, find roughly where row 17 content starts by parsing in large chunks
        var rawBytes = LoadCapture("claude-stray-chars.bin");
        var cells = new TerminalCell[147, 50];
        var scrollback = new List<TerminalCell[]>();
        var parser = new AnsiParser(cells, 147, 50, scrollback, 1000);

        // Find the byte offset where cell [5,17] first gets the 'e'
        int targetOffset = -1;
        int bigChunk = 1000;
        for (int offset = 0; offset < rawBytes.Length; offset += bigChunk)
        {
            int len = Math.Min(bigChunk, rawBytes.Length - offset);
            var chunk = new byte[len];
            Array.Copy(rawBytes, offset, chunk, 0, len);
            parser.Parse(chunk);

            if (cells[5, 17].Character == 'e' && targetOffset == -1)
            {
                targetOffset = offset;
                _output.WriteLine($"Cell [5,17] got 'e' somewhere in bytes {offset}-{offset + len}");
                break;
            }
        }

        if (targetOffset == -1)
        {
            _output.WriteLine("Cell [5,17] never got 'e' -- stray char might not reproduce with this grid size");
            return;
        }

        // Now re-parse from scratch, but go byte-by-byte in the region around targetOffset
        cells = new TerminalCell[147, 50];
        scrollback = new List<TerminalCell[]>();
        parser = new AnsiParser(cells, 147, 50, scrollback, 1000);

        // Parse everything before the target region in one go
        int preambleEnd = Math.Max(0, targetOffset - 2000);
        if (preambleEnd > 0)
        {
            var preamble = new byte[preambleEnd];
            Array.Copy(rawBytes, 0, preamble, 0, preambleEnd);
            parser.Parse(preamble);
        }

        // Now parse byte-by-byte from preambleEnd to targetOffset + bigChunk
        int traceEnd = Math.Min(rawBytes.Length, targetOffset + bigChunk + 2000);
        char prevChar = cells[5, 17].Character;

        for (int i = preambleEnd; i < traceEnd; i++)
        {
            parser.Parse(new byte[] { rawBytes[i] });

            char current = cells[5, 17].Character;
            if (current != prevChar)
            {
                var (curCol, curRow) = parser.GetCursorPosition();

                // Show surrounding bytes for context
                int contextStart = Math.Max(0, i - 20);
                int contextEnd = Math.Min(rawBytes.Length, i + 5);
                var contextBytes = new byte[contextEnd - contextStart];
                Array.Copy(rawBytes, contextStart, contextBytes, 0, contextBytes.Length);
                var hexContext = BitConverter.ToString(contextBytes).Replace("-", " ");

                // Try to decode as text
                string textContext;
                try { textContext = Encoding.UTF8.GetString(contextBytes).Replace("\x1b", "ESC").Replace("\r", "\\r").Replace("\n", "\\n"); }
                catch { textContext = "(decode failed)"; }

                _output.WriteLine(
                    $"BYTE {i}: cell [5,17] '{prevChar}' -> '{current}' " +
                    $"cursor=({curCol},{curRow}) scrollback={scrollback.Count}");
                _output.WriteLine($"  Hex context: {hexContext}");
                _output.WriteLine($"  Text context: {textContext}");
                _output.WriteLine("");
                prevChar = current;
            }
        }
    }
}
