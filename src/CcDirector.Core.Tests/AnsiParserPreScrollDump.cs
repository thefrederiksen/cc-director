using System.Text;
using CcDirector.Terminal.Core;
using Xunit;
using Xunit.Abstractions;
using static CcDirector.Core.Tests.TerminalTestHelper;

namespace CcDirector.Core.Tests;

public class AnsiParserPreScrollDump
{
    private readonly ITestOutputHelper _output;

    public AnsiParserPreScrollDump(ITestOutputHelper output) => _output = output;

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
    public void DumpGridBeforeAndAfterCriticalScroll()
    {
        var rawBytes = LoadCapture("claude-stray-chars.bin");
        var cells = new TerminalCell[147, 50];
        var scrollback = new List<TerminalCell[]>();
        var parser = new AnsiParser(cells, 147, 50, scrollback, 1000);

        // Parse up to just before byte 196029 (where the scroll happens that puts 'e' on row 17)
        // Parse up to byte 196028
        var before = new byte[196028];
        Array.Copy(rawBytes, 0, before, 0, 196028);
        parser.Parse(before);

        _output.WriteLine($"=== BEFORE scroll (scrollback={scrollback.Count}) ===");
        _output.WriteLine($"Cursor: {parser.GetCursorPosition()}");
        _output.WriteLine("");

        // Dump rows 16-20 to see what will scroll up
        for (int r = 15; r <= 22; r++)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < 147; c++)
            {
                char ch = cells[c, r].Character;
                sb.Append(ch == '\0' ? '.' : ch);
            }
            _output.WriteLine($"Row {r:D2}: {sb.ToString().TrimEnd('.')}");
        }

        _output.WriteLine("");

        // Now parse the next few bytes that trigger the scroll
        var trigger = new byte[10];
        int triggerLen = Math.Min(10, rawBytes.Length - 196028);
        Array.Copy(rawBytes, 196028, trigger, 0, triggerLen);
        parser.Parse(trigger);

        _output.WriteLine($"=== AFTER scroll (scrollback={scrollback.Count}) ===");
        _output.WriteLine($"Cursor: {parser.GetCursorPosition()}");
        _output.WriteLine("");

        for (int r = 15; r <= 22; r++)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < 147; c++)
            {
                char ch = cells[c, r].Character;
                sb.Append(ch == '\0' ? '.' : ch);
            }
            _output.WriteLine($"Row {r:D2}: {sb.ToString().TrimEnd('.')}");
        }
    }
}
