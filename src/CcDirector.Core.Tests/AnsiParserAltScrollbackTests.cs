using System.Text;
using CcDirector.Terminal.Core;
using Xunit;
using static CcDirector.Core.Tests.TerminalTestHelper;

namespace CcDirector.Core.Tests;

/// <summary>
/// Alternate-screen local scrollback (issue #761). Full-screen agents (Claude Code,
/// Codex, Grok, Copilot) run on the alternate screen, which traditionally has no
/// scrollback -- so a user could not read back through a running session. The parser
/// now recovers alternate-screen history the same way it does for the primary buffer
/// (ScrollUp and the in-place repaint diff), but into a dedicated <see cref="AnsiParser.AltScrollback"/>
/// that is cleared whenever the app enters or leaves the alternate screen.
/// </summary>
public class AnsiParserAltScrollbackTests
{
    private const string EnterAlt = "\x1b[?1049h";
    private const string LeaveAlt = "\x1b[?1049l";

    // One Claude-style repaint frame on the alternate screen: bare ESC[H frame marker
    // then absolute-positioned line writes (CUP with params, not a frame marker). The
    // content rows scroll up by one per frame; the bottom two rows are a fixed input box.
    private static string Frame(int f, int rows)
    {
        int contentRows = rows - 2;
        var sb = new StringBuilder();
        sb.Append("\x1b[H");
        for (int r = 0; r < rows; r++)
        {
            sb.Append($"\x1b[{r + 1};1H");
            if (r < contentRows) sb.Append($"L{f + r}");
            else if (r == rows - 2) sb.Append("--------");
            else sb.Append(">box");
            sb.Append("\x1b[K");
        }
        return sb.ToString();
    }

    private static string RowText(TerminalCell[] row)
    {
        var sb = new StringBuilder();
        foreach (var cell in row) sb.Append(cell.Character == '\0' ? ' ' : cell.Character);
        return sb.ToString().TrimEnd();
    }

    [Fact]
    public void AlternateScreen_InPlaceRepaint_RecoversHistoryIntoAltScrollback()
    {
        var (parser, _, primary) = CreateParser(cols: 20, rows: 10);

        Parse(parser, EnterAlt);
        Assert.True(parser.IsAlternateScreen);

        for (int f = 0; f < 12; f++)
            Parse(parser, Frame(f, rows: 10));
        Parse(parser, "\x1b[H"); // commit the final frame

        // The bug was AltScrollbackCount == 0. Now the lines that scrolled off the
        // top are recovered, in order, into the dedicated alt buffer.
        Assert.True(parser.AltScrollbackCount >= 10,
            $"expected recovered alt history, got {parser.AltScrollbackCount} lines");
        Assert.Equal("L0", RowText(parser.AltScrollback[0]));
        Assert.Equal("L1", RowText(parser.AltScrollback[1]));

        // The primary buffer's scrollback must NOT be touched while on the alt screen.
        Assert.Empty(primary);
    }

    [Fact]
    public void AlternateScreen_PlainLineFeed_CapturesIntoAltScrollback()
    {
        var (parser, _, primary) = CreateParser(cols: 20, rows: 10);

        Parse(parser, EnterAlt);
        var sb = new StringBuilder();
        for (int i = 0; i < 21; i++) sb.Append($"L{i}\r\n");
        Parse(parser, sb.ToString());

        Assert.True(parser.AltScrollbackCount >= 10,
            $"expected ScrollUp alt history, got {parser.AltScrollbackCount}");
        Assert.Equal("L0", RowText(parser.AltScrollback[0]));
        Assert.Empty(primary);
    }

    [Fact]
    public void LeavingAlternateScreen_ClearsAltScrollback()
    {
        var (parser, _, _) = CreateParser(cols: 20, rows: 10);

        Parse(parser, EnterAlt);
        for (int f = 0; f < 12; f++)
            Parse(parser, Frame(f, rows: 10));
        Parse(parser, "\x1b[H");
        Assert.True(parser.AltScrollbackCount > 0);

        Parse(parser, LeaveAlt);

        Assert.False(parser.IsAlternateScreen);
        Assert.Equal(0, parser.AltScrollbackCount);
    }

    [Fact]
    public void EnteringAlternateScreen_LeavesPrimaryScrollbackIntact()
    {
        var (parser, _, primary) = CreateParser(cols: 20, rows: 10);

        // Build real primary-buffer history first.
        var sb = new StringBuilder();
        for (int i = 0; i < 15; i++) sb.Append($"P{i}\r\n");
        Parse(parser, sb.ToString());
        int primaryBefore = primary.Count;
        Assert.True(primaryBefore > 0);

        // Enter the alt screen and repaint; the primary history is preserved untouched
        // for when the app exits, and the alt history is kept separately.
        Parse(parser, EnterAlt);
        for (int f = 0; f < 12; f++)
            Parse(parser, Frame(f, rows: 10));
        Parse(parser, "\x1b[H");

        Assert.Equal(primaryBefore, primary.Count);
        Assert.True(parser.AltScrollbackCount > 0);
    }

    [Fact]
    public void TotalLinesScrolled_CountsPrimaryAndAlternate()
    {
        var (parser, _, _) = CreateParser(cols: 20, rows: 10);

        var sb = new StringBuilder();
        for (int i = 0; i < 5; i++) sb.Append($"P{i}\r\n"); // no scroll yet (fits in 10 rows)
        for (int i = 5; i < 21; i++) sb.Append($"P{i}\r\n"); // now scrolling
        Parse(parser, sb.ToString());

        long afterPrimary = parser.TotalLinesScrolled;
        Assert.True(afterPrimary > 0, "counter should advance for primary scrollback");

        Parse(parser, EnterAlt);
        var sb2 = new StringBuilder();
        for (int i = 0; i < 21; i++) sb2.Append($"A{i}\r\n");
        Parse(parser, sb2.ToString());

        Assert.True(parser.TotalLinesScrolled > afterPrimary,
            "counter should keep advancing for alternate-screen scrollback");
    }
}
