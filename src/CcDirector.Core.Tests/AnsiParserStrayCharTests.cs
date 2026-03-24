using System.Text;
using CcDirector.Terminal.Core;
using Xunit;
using Xunit.Abstractions;
using static CcDirector.Core.Tests.TerminalTestHelper;

namespace CcDirector.Core.Tests;

/// <summary>
/// Diagnostic test to replay a captured Claude Code session and dump the cell grid
/// to find stray characters that shouldn't be there.
/// </summary>
public class AnsiParserStrayCharTests
{
    private readonly ITestOutputHelper _output;

    public AnsiParserStrayCharTests(ITestOutputHelper output) => _output = output;

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
    public void DumpGridToFindStrayChars()
    {
        var rawBytes = LoadCapture("claude-stray-chars.bin");
        // 147 cols x 50 rows (from metadata)
        var (parser, cells, scrollback) = CreateParser(cols: 147, rows: 50);
        parser.Parse(rawBytes);

        _output.WriteLine($"Scrollback: {scrollback.Count} lines");
        _output.WriteLine($"Parsed {rawBytes.Length} bytes");
        _output.WriteLine("");

        // Dump visible rows looking for isolated characters
        // (characters surrounded by empty cells on both sides)
        for (int r = 0; r < 50; r++)
        {
            var sb = new StringBuilder();
            bool hasContent = false;
            int isolatedCount = 0;

            for (int c = 0; c < 147; c++)
            {
                var cell = cells[c, r];
                char ch = cell.Character;
                if (ch == '\0')
                {
                    sb.Append('.');
                }
                else
                {
                    sb.Append(ch);
                    hasContent = true;

                    // Check if this char is isolated (empty on both sides)
                    bool leftEmpty = c == 0 || cells[c - 1, r].Character == '\0';
                    bool rightEmpty = c == 146 || cells[c + 1, r].Character == '\0';
                    if (leftEmpty && rightEmpty && ch != ' ')
                        isolatedCount++;
                }
            }

            if (hasContent)
            {
                string marker = isolatedCount > 0 ? $" *** {isolatedCount} ISOLATED CHARS ***" : "";
                _output.WriteLine($"Row {r:D2}: {sb.ToString().TrimEnd('.')}{marker}");
            }
        }
    }
}
