using CcDirector.ControlApi;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Screens;

/// <summary>
/// Inspection 01, finding 4 - the half that runs in the default gate.
///
/// THE DEFECT THIS EXISTS FOR. The inspector changed one line in <see cref="GatewayScreenSink"/> so that
/// every pushed screen carried the single row "MANGLED CONSTANT" instead of the terminal's own content,
/// and then ran the complete Gateway unit project: 3,189 passed, 3 skipped, 0 failed, exit 0. Nothing
/// anywhere compared what came out of the sink with what went into it. The store's own tests seed the
/// store by hand, row 0 stops before the sink, and the end-to-end rig only asked whether SOMETHING
/// nonblank had arrived. A push path that replaced every screen with arbitrary text was, to the whole
/// suite, indistinguishable from a correct one.
///
/// WHAT THIS ASSERTS. Every field of the <c>ScreenPush</c> equals the field it came from on the
/// <see cref="TurnEndScreen"/>, and the rows are compared ELEMENT BY ELEMENT rather than by count or by
/// "not empty" - a count matches a mangled row, and a fixed-height terminal grid is full of rows even
/// when nothing is on it. The values are deliberately all DIFFERENT from each other and from any
/// plausible default, so a mapping that crossed two fields over, or that returned a fresh object with
/// its defaults, fails rather than passing by coincidence.
///
/// It does NOT cover the hub method, the transport or the store write; those are the rig's, and the rig
/// now compares content too. What it does cover is the seam where a screen becomes a push, which is
/// where the mutation lived and where the fast gate can see it.
/// </summary>
public class GatewayScreenSinkMappingTests
{
    private static readonly string[] TerminalRows =
    {
        "SINK_MAPPING_ROW_ONE the agent finished",
        "",
        "  SINK_MAPPING_ROW_THREE waiting for input",
    };

    private static TurnEndScreen Captured() => new()
    {
        SessionId = "44444444-4444-4444-4444-444444444444",
        CapturedAtUtc = new DateTime(2026, 9, 2, 15, 44, 3, 17, DateTimeKind.Utc),
        Rows = TerminalRows.ToArray(),
        CursorRow = 6,
        CursorCol = 11,
        CursorVisible = true,
        IsAlternateScreen = true,
        HasGrid = true,
        BufferBytes = 987654,
        ActivityState = "WaitingForInput",
        Agent = "Codex",
    };

    [Fact]
    public void The_push_carries_the_captured_screen_across_field_for_field()
    {
        var screen = Captured();

        var push = GatewayScreenSink.ToPush(screen);

        // THE ROWS, ELEMENT BY ELEMENT. This is the assertion the mutation broke, and it is first because
        // it is the one that matters: the stored screen has to BE the terminal's screen.
        Assert.Equal(TerminalRows, push.Rows);
        Assert.Equal(screen.Rows.Length, push.Rows.Count);
        for (var i = 0; i < screen.Rows.Length; i++)
            Assert.Equal(screen.Rows[i], push.Rows[i]);

        Assert.Equal(screen.SessionId, push.SessionId);
        Assert.Equal(screen.CapturedAtUtc, push.CapturedAtUtc);
        Assert.Equal(screen.CursorRow, push.CursorRow);
        Assert.Equal(screen.CursorCol, push.CursorCol);
        Assert.Equal(screen.CursorVisible, push.CursorVisible);
        Assert.Equal(screen.IsAlternateScreen, push.IsAlternateScreen);
        Assert.Equal(screen.HasGrid, push.HasGrid);
        Assert.Equal(screen.BufferBytes, push.BufferBytes);
        Assert.Equal(screen.ActivityState, push.ActivityState);
        Assert.Equal(screen.Agent, push.Agent);
    }

    /// <summary>
    /// The push must be its own object holding its own copy of the rows. If it shared the captured array,
    /// a later repaint of the same session could rewrite rows already handed to the transport, and the
    /// screen that reached the Gateway would not be the screen that was captured.
    /// </summary>
    [Fact]
    public void The_push_holds_its_own_copy_of_the_rows()
    {
        var screen = Captured();
        var push = GatewayScreenSink.ToPush(screen);

        screen.Rows[0] = "REWRITTEN AFTER THE PUSH WAS BUILT";

        Assert.Equal(TerminalRows[0], push.Rows[0]);
    }
}
