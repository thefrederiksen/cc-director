using System.Text;
using CcDirector.Terminal.Core;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.Tests;

public class AnsiParserRow17FinalWrite
{
    private readonly ITestOutputHelper _output;

    public AnsiParserRow17FinalWrite(ITestOutputHelper output) => _output = output;

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
    public void FindLastWriteToRow17()
    {
        var rawBytes = LoadCapture("claude-stray-chars.bin");
        var cells = new TerminalCell[147, 50];
        var scrollback = new List<TerminalCell[]>();
        var parser = new AnsiParser(cells, 147, 50, scrollback, 1000);

        // Parse in 100-byte chunks, track every time ANY cell on row 17 changes
        int lastChangeOffset = -1;
        var prevRow17 = new char[147];

        for (int offset = 0; offset < rawBytes.Length; offset += 100)
        {
            int len = Math.Min(100, rawBytes.Length - offset);
            var chunk = new byte[len];
            Array.Copy(rawBytes, offset, chunk, 0, len);
            parser.Parse(chunk);

            for (int c = 0; c < 147; c++)
            {
                char current = cells[c, 17].Character;
                if (current != prevRow17[c])
                {
                    lastChangeOffset = offset;
                    prevRow17[c] = current;
                }
            }
        }

        _output.WriteLine($"Last change to row 17 at byte offset ~{lastChangeOffset}");
        _output.WriteLine($"Total bytes: {rawBytes.Length}");
        _output.WriteLine($"Bytes from end: {rawBytes.Length - lastChangeOffset}");

        // Now re-parse to just before and dump the ANSI around that offset
        int dumpStart = Math.Max(0, lastChangeOffset - 200);
        int dumpEnd = Math.Min(rawBytes.Length, lastChangeOffset + 300);
        var context = new byte[dumpEnd - dumpStart];
        Array.Copy(rawBytes, dumpStart, context, 0, context.Length);

        var text = Encoding.UTF8.GetString(context)
            .Replace("\x1b", "\nESC")
            .Replace("\r", "\\r")
            .Replace("\n", "\n");

        _output.WriteLine($"\nANSI context around last write to row 17 (bytes {dumpStart}-{dumpEnd}):");
        _output.WriteLine(text);

        // Also dump the final state of row 17
        _output.WriteLine("\n=== Final row 17 ===");
        var sb = new StringBuilder();
        for (int c = 0; c < 147; c++)
        {
            char ch = cells[c, 17].Character;
            sb.Append(ch == '\0' ? '.' : ch);
        }
        _output.WriteLine(sb.ToString());

        // And row 16 for comparison
        _output.WriteLine("\n=== Final row 16 ===");
        sb.Clear();
        for (int c = 0; c < 147; c++)
        {
            char ch = cells[c, 16].Character;
            sb.Append(ch == '\0' ? '.' : ch);
        }
        _output.WriteLine(sb.ToString());
    }
}
