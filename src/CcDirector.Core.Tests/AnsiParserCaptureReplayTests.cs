using System.Text;
using CcDirector.Terminal.Core;
using Xunit;
using static CcDirector.Core.Tests.TerminalTestHelper;

namespace CcDirector.Core.Tests;

/// <summary>
/// Tests that replay captured raw ANSI byte streams from real Claude Code sessions
/// and verify the cell grid has correct backgrounds.
///
/// The captured data is from a real Claude Code startup showing the bug:
/// grey backgrounds leak across full row width on the banner, prompt, and status bar lines.
/// </summary>
public class AnsiParserCaptureReplayTests
{
    private static byte[] LoadCapture(string filename)
    {
        // Try test data directory relative to test assembly
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData");
        var path = Path.Combine(dir, filename);
        if (File.Exists(path))
            return File.ReadAllBytes(path);

        // Try relative to project dir
        var projectDir = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "TestData");
        path = Path.Combine(projectDir, filename);
        if (File.Exists(path))
            return File.ReadAllBytes(path);

        throw new FileNotFoundException($"Capture file not found: {filename}. Searched: {dir} and {projectDir}");
    }

    [Fact]
    public void ClaudeStartup_NoBackgroundLeakingOnEmptyCells()
    {
        // Replay the exact bytes Claude Code sends on startup
        var rawBytes = LoadCapture("claude-startup.bin");

        // Use 120x30 grid (same as SessionManager default / ConPTY initial size)
        var (parser, cells, _) = CreateParser(cols: 120, rows: 30);
        parser.Parse(rawBytes);

        // Check rows that should NOT have backgrounds on empty cells:
        // Row 0-2: banner area (logo + version text) -- text cells may have colors,
        //          but empty cells to the RIGHT of text should have default bg
        // Row 4: horizontal rule (box drawing chars) -- these have fg color but bg should be default
        // Row 5: prompt line "> " -- the cursor space may have reverse bg,
        //        but empty cells after the prompt should have default bg
        // Row 7: horizontal rule below prompt
        // Rows 8+: should be completely empty with default bg

        // Check that empty cells on row 4 (horizontal rule) have default bg
        // The rule is drawn with foreground color, NOT background
        for (int c = 0; c < 120; c++)
        {
            var cell = cells[c, 3]; // row 4 is index 3 (0-based, but let's check the actual row)
        }

        // The key assertion: scan all UNWRITTEN cells (null char) for leaked backgrounds.
        // Space chars with backgrounds may be intentional (e.g., cursor position with reverse video).
        var leaked = new List<string>();
        for (int r = 0; r < 30; r++)
        {
            for (int c = 0; c < 120; c++)
            {
                var cell = cells[c, r];
                bool isUnwritten = cell.Character == '\0';
                bool hasBg = cell.Background != default;

                if (isUnwritten && hasBg)
                {
                    leaked.Add($"[{c},{r}] bg=#{cell.Background.R:X2}{cell.Background.G:X2}{cell.Background.B:X2}");
                }
            }
        }

        Assert.True(leaked.Count == 0,
            $"Found {leaked.Count} empty cells with leaked backgrounds:\n" +
            string.Join("\n", leaked.Take(20)));
    }

    [Fact]
    public void ClaudeStartup_HorizontalRuleHasDefaultBackground()
    {
        var rawBytes = LoadCapture("claude-startup.bin");
        var (parser, cells, _) = CreateParser(cols: 120, rows: 30);
        parser.Parse(rawBytes);

        // Row 4 (0-based) has the horizontal rule above the prompt.
        // The box drawing chars should have a foreground color but DEFAULT background.
        // Bug: the entire row gets a grey/colored background.
        for (int c = 0; c < 120; c++)
        {
            var cell = cells[c, 4];
            if (cell.Character != '\0')
            {
                Assert.True(cell.Background == default,
                    $"Horizontal rule cell [{c},4] char='{cell.Character}' has bg=#{cell.Background.R:X2}{cell.Background.G:X2}{cell.Background.B:X2} -- should be default");
            }
        }
    }

    [Fact]
    public void ClaudeStartup_PromptRowEmptyCellsHaveDefaultBackground()
    {
        var rawBytes = LoadCapture("claude-startup.bin");
        var (parser, cells, _) = CreateParser(cols: 120, rows: 30);
        parser.Parse(rawBytes);

        // Row 5 (0-based) is the prompt line "> [cursor]"
        // The first few cells have content, but cells after col ~5 should be empty with default bg
        for (int c = 10; c < 120; c++)
        {
            var cell = cells[c, 5];
            if (cell.Character == '\0' || cell.Character == ' ')
            {
                Assert.True(cell.Background == default,
                    $"Prompt row empty cell [{c},5] has bg=#{cell.Background.R:X2}{cell.Background.G:X2}{cell.Background.B:X2} -- should be default");
            }
        }
    }

    [Fact]
    public void ClaudeStartup_EmptyRowsBelowContentHaveDefaultBackground()
    {
        var rawBytes = LoadCapture("claude-startup.bin");
        var (parser, cells, _) = CreateParser(cols: 120, rows: 30);
        parser.Parse(rawBytes);

        // Rows 9+ should be completely empty (Claude Code only renders ~8 rows of content)
        for (int r = 9; r < 30; r++)
        {
            for (int c = 0; c < 120; c++)
            {
                var cell = cells[c, r];
                Assert.True(cell.Background == default,
                    $"Empty cell [{c},{r}] has bg=#{cell.Background.R:X2}{cell.Background.G:X2}{cell.Background.B:X2} -- should be default");
            }
        }
    }
}
