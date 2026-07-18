namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The resolved LIVE terminal screen grid for a session (issue #1777): the on-screen rows exactly as the
/// emulator resolves them right now, the live cursor cell, and whether the agent has the terminal in the
/// alternate screen buffer. This is the alternate-screen-correct read the wingman menu detector needs: when
/// an agent draws a full-screen picker it switches to the alternate screen, where the scrollback (the
/// <c>buffer</c> verb) is empty by design, so the menu is only visible on this grid.
///
/// Deliberately NOT the styled HTML snapshot (<c>buffer-html</c>): that is a presentation format. This
/// carries the grid as plain, trailing-trimmed rows plus the cursor cell and the active-buffer identity, so a
/// consumer reasons about the live screen without parsing HTML. The grid is a Session concern (the emulator
/// resolves it the same way for every agent CLI), so it is agent-uniform - no per-agent data source.
/// </summary>
public sealed class ScreenGridResponse
{
    public string SessionId { get; set; } = "";

    /// <summary>
    /// The current visible grid as plain-text rows, top to bottom, each trailing-trimmed. Empty when the
    /// session has no server-side grid parser (an Embedded session) - see <see cref="HasGrid"/>.
    /// </summary>
    public List<string> Rows { get; set; } = new();

    /// <summary>0-based grid row of the live cursor, or -1 when there is no grid.</summary>
    public int CursorRow { get; set; } = -1;

    /// <summary>0-based grid column of the live cursor, or -1 when there is no grid.</summary>
    public int CursorCol { get; set; } = -1;

    /// <summary>
    /// True when the terminal hardware cursor is VISIBLE (DECTCEM ?25h). The discriminator between a text
    /// composer (cursor visible in its input box) and a drawn full-screen menu (Claude Code's Ink picker HIDES
    /// the cursor and draws its own selection marker). <see cref="CursorRow"/> / <see cref="CursorCol"/> carry a
    /// position even when the cursor is hidden - a STALE value - so a consumer must check this before trusting
    /// the cursor cell. A menu is owned by its drawn marker, not the hardware cursor; typing requires a VISIBLE
    /// cursor inside the composer.
    /// </summary>
    public bool CursorVisible { get; set; }

    /// <summary>
    /// True when the agent currently has the terminal in the alternate screen buffer (a full-screen picker,
    /// <c>ESC[?1049h</c>). While true the scrollback is intentionally empty, so a menu is visible only on
    /// <see cref="Rows"/>.
    /// </summary>
    public bool IsAlternateScreen { get; set; }

    /// <summary>
    /// True when the session actually has a resolved live grid to read (a real agent terminal). False for an
    /// Embedded session with no server-side parser - the screen is UNREADABLE and a caller must fail closed
    /// rather than treat an empty grid as "not a menu".
    /// </summary>
    public bool HasGrid { get; set; }
}
