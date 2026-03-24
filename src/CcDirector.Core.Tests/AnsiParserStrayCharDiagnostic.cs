using System.Text;
using CcDirector.Terminal.Core;
using Xunit;
using Xunit.Abstractions;
using static CcDirector.Core.Tests.TerminalTestHelper;

namespace CcDirector.Core.Tests;

/// <summary>
/// Detailed diagnostic: instrument the AnsiParser to log every cursor movement
/// and character write around the rows where stray chars appear.
/// </summary>
public class AnsiParserStrayCharDiagnostic
{
    private readonly ITestOutputHelper _output;

    public AnsiParserStrayCharDiagnostic(ITestOutputHelper output) => _output = output;

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
    public void AnalyzeStrayCharPositions()
    {
        var rawBytes = LoadCapture("claude-stray-chars.bin");
        var (parser, cells, scrollback) = CreateParser(cols: 147, rows: 50);
        parser.Parse(rawBytes);

        // Row 17 has stray chars. Let's find their exact positions and compare to row 16
        _output.WriteLine("=== Row 16 vs Row 17 comparison ===");
        _output.WriteLine("");

        var row16 = new StringBuilder();
        var row17 = new StringBuilder();

        for (int c = 0; c < 147; c++)
        {
            char ch16 = cells[c, 16].Character;
            char ch17 = cells[c, 17].Character;

            row16.Append(ch16 == '\0' ? '.' : ch16);
            row17.Append(ch17 == '\0' ? '.' : ch17);
        }

        _output.WriteLine($"R16: {row16}");
        _output.WriteLine($"R17: {row17}");
        _output.WriteLine("");

        // Find stray chars on row 17 and check if they match row 16 at same position
        _output.WriteLine("Stray chars on row 17:");
        for (int c = 0; c < 147; c++)
        {
            char ch17 = cells[c, 17].Character;
            if (ch17 != '\0' && ch17 != ' ')
            {
                char ch16 = cells[c, 16].Character;
                bool match = ch16 == ch17;
                _output.WriteLine($"  col {c}: '{ch17}' (row16 same pos: '{(ch16 == '\0' ? '.' : ch16)}' {(match ? "MATCH" : "no match")})");
            }
        }

        _output.WriteLine("");
        _output.WriteLine("=== Row 24 vs Row 25 comparison ===");
        _output.WriteLine("");

        var row24 = new StringBuilder();
        var row25 = new StringBuilder();

        for (int c = 0; c < 147; c++)
        {
            char ch24 = cells[c, 24].Character;
            char ch25 = cells[c, 25].Character;

            row24.Append(ch24 == '\0' ? '.' : ch24);
            row25.Append(ch25 == '\0' ? '.' : ch25);
        }

        _output.WriteLine($"R24: {row24}");
        _output.WriteLine($"R25: {row25}");
        _output.WriteLine("");

        _output.WriteLine("Stray chars on row 25:");
        for (int c = 0; c < 147; c++)
        {
            char ch25 = cells[c, 25].Character;
            if (ch25 != '\0' && ch25 != ' ')
            {
                char ch24 = cells[c, 24].Character;
                bool match = ch24 == ch25;
                _output.WriteLine($"  col {c}: '{ch25}' (row24 same pos: '{(ch24 == '\0' ? '.' : ch24)}' {(match ? "MATCH" : "no match")})");
            }
        }
    }
}
