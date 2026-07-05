using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// Scrollback behavior of the production <see cref="TerminalControl"/> (issue #761):
///  - the normal buffer must PIN a scrolled-up viewport to the same content as new
///    output arrives, instead of letting it drift upward; and
///  - the alternate screen (full-screen agents) must expose local scrollback so the
///    user can read back through a running session.
/// These run headless and drive the control's real parse + scroll paths.
/// </summary>
public sealed class TerminalScrollbackTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static string PlainLines(int from, int toExclusive)
    {
        var sb = new StringBuilder();
        for (int i = from; i < toExclusive; i++) sb.Append($"L{i}\r\n");
        return sb.ToString();
    }

    // A Claude-style alternate-screen repaint frame: bare ESC[H marker + absolute-positioned
    // line writes, content scrolling up one line per frame, with a fixed 2-row input box.
    private static string AltFrame(int f, int rows)
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

    private static TerminalControl NewTerminal()
    {
        var terminal = new TerminalControl();
        var window = new Window { Width = 800, Height = 400, Content = terminal };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        terminal.HarnessSetGrid(20, 10);
        return terminal;
    }

    [AvaloniaFact]
    public void NormalBuffer_ScrolledUpViewport_StaysPinnedAsOutputArrives()
    {
        var terminal = NewTerminal();

        // Build history well beyond the 10-row viewport, then scroll up.
        terminal.HarnessRebuild(Bytes(PlainLines(0, 40)));
        terminal.HarnessScrollUp(5);

        string topBefore = terminal.HarnessVisibleLine(0);
        int offsetBefore = terminal.HarnessScrollOffset;
        Assert.StartsWith("L", topBefore);

        // More output arrives while the user is reading back.
        terminal.HarnessFeed(Bytes(PlainLines(40, 60)));

        // The pinned line must still be at the top of the viewport (no drift), and the
        // offset must have grown to keep it there as scrollback advanced.
        Assert.Equal(topBefore, terminal.HarnessVisibleLine(0));
        Assert.True(terminal.HarnessScrollOffset > offsetBefore,
            $"offset should advance to keep content pinned (before={offsetBefore}, after={terminal.HarnessScrollOffset})");
    }

    [AvaloniaFact]
    public void NormalBuffer_AtBottom_KeepsFollowingLiveOutput()
    {
        var terminal = NewTerminal();
        terminal.HarnessRebuild(Bytes(PlainLines(0, 40)));

        // Not scrolled up: stays attached to the live bottom.
        terminal.HarnessFeed(Bytes(PlainLines(40, 60)));

        Assert.Equal(0, terminal.HarnessScrollOffset);
        Assert.EndsWith("59", terminal.HarnessVisibleLine(terminal.HarnessRows - 2));
    }

    [AvaloniaFact]
    public void AlternateScreen_ExposesLocalScrollbackAndRendersIt()
    {
        var terminal = NewTerminal();

        var sb = new StringBuilder();
        sb.Append("\x1b[?1049h");
        for (int f = 0; f < 12; f++) sb.Append(AltFrame(f, rows: 10));
        sb.Append("\x1b[H");
        terminal.HarnessRebuild(Bytes(sb.ToString()));

        var parser = terminal.HarnessParser;
        Assert.NotNull(parser);
        Assert.True(parser!.IsAlternateScreen, "fixture should leave the parser on the alternate screen");
        Assert.True(parser.AltScrollbackCount > 0, "alternate-screen history should have been recovered");

        // The host reads scroll extents from the snapshot; on the alt screen it must report
        // the alternate-screen history (so the scrollbar becomes usable), not the empty primary.
        Assert.Equal(parser.AltScrollbackCount, terminal.GetScrollSnapshot().ScrollbackCount);

        // Scrolling up renders the recovered alternate-screen lines.
        terminal.HarnessScrollUp(3);
        Assert.True(terminal.HarnessScrollOffset > 0);
        Assert.StartsWith("L", terminal.HarnessVisibleLine(0));
    }
}
