namespace CcDirector.Gateway.Wingman;

/// <summary>
/// What a session's live waiting screen is (issue #1777). This is the fail-closed floor, and fail closed is
/// the DEFAULT - anything the grid does not POSITIVELY resolve to a composer or a menu is <see cref="Blocked"/>.
/// </summary>
public enum WaitingScreenKind
{
    /// <summary>A menu, owned by its DRAWN selection marker on the grid (not the hardware cursor, which the Ink
    /// picker hides). The caller extracts and presses off the LIVE grid.</summary>
    Menu,

    /// <summary>The VISIBLE hardware cursor is positively inside the agent's composer input box and no menu is
    /// on the grid - the spoken words go there.</summary>
    PlainText,

    /// <summary>Unreadable, blank, alternate-screen non-menu, ambiguous, hidden/off-target cursor with no menu
    /// marker: never type, never press. The default.</summary>
    Blocked,
}

/// <summary>
/// The pure (no-brain) waiting-screen classifier, using TWO DIFFERENT ANCHORS with fail-closed as the default
/// (issue #1777, round-4). A text composer and an Ink menu cannot be told apart by the hardware cursor: Claude
/// Code HIDES the cursor on its permission menu and draws its own selection marker, and the parser still
/// reports a (stale) cursor cell. So:
///   - TYPING is allowed only when ALL hold: NOT the alternate screen; the cursor is VISIBLE; the cursor is
///     positively inside the agent's framed composer input box (row and column); and NO menu is on the grid.
///   - A MENU is owned by its DRAWN selection marker (<c>❯</c>/<c>&gt;</c> on an option row) plus the option
///     lines - cursor-independent, so menu-answering works with the cursor hidden.
///   - EVERYTHING ELSE is Blocked. When in doubt, block.
/// A grid carrying BOTH a menu marker and a live composer is ambiguous (a stale menu above a fresh composer, or
/// the reverse) and is Blocked rather than guessed.
/// </summary>
public static class WaitingScreenClassifier
{
    /// <summary>Classify the live waiting screen. <paramref name="cursorVisible"/> is the DECTCEM hardware-cursor
    /// visibility (a hidden cursor means the cursor cell is stale). <paramref name="hasGrid"/> is false for an
    /// Embedded session with no parser (unreadable). A <see cref="WaitingScreenKind.Menu"/> result means a
    /// drawn menu is present - the caller still extracts pressable, answerable options and fails closed if it
    /// cannot.</summary>
    public static WaitingScreenKind Classify(
        IReadOnlyList<string>? rows, int cursorRow, int cursorCol, bool cursorVisible, bool isAlternateScreen, bool hasGrid)
    {
        // Unreadable / blank -> never type.
        if (!hasGrid || rows is null || rows.Count == 0) return WaitingScreenKind.Blocked;
        if (rows.All(string.IsNullOrWhiteSpace)) return WaitingScreenKind.Blocked;

        // A menu is owned by its DRAWN marker, independent of the hardware cursor (the Ink picker hides it).
        var hasMenu = WingmanMenuLogic.LiveScreenHasMenuSelection(rows);

        // A composer requires the VISIBLE cursor positively inside a framed input box, on the primary screen.
        var hasComposer = !isAlternateScreen
            && cursorVisible
            && cursorRow >= 0 && cursorRow < rows.Count
            && CursorInComposer(rows, cursorRow, cursorCol);

        // Both present is ambiguous (a stale menu above a live composer, or vice versa) - fail closed.
        if (hasMenu && hasComposer) return WaitingScreenKind.Blocked;
        if (hasMenu) return WaitingScreenKind.Menu;
        if (hasComposer) return WaitingScreenKind.PlainText;
        return WaitingScreenKind.Blocked;
    }

    /// <summary>
    /// POSITIVE composer identification (issue #1777): the cursor's row is the agent's input-prompt line -
    /// after stripping the box edge it begins with a single "&gt;" prompt marker (not "&gt;&gt;", the mode-cycle
    /// arrow) - the cursor column is WITHIN the editable input span (at or after the prompt, and left of the
    /// closing box border), AND the line is FRAMED as an input box (an adjacent box-border row, or the agent's
    /// mode-status footer just below). Note: this alone is NOT enough to type - the classifier also requires the
    /// cursor to be VISIBLE and no menu on the grid. Public so a test can assert the input-span edges directly.
    /// </summary>
    public static bool LooksLikePlainTextPrompt(IReadOnlyList<string> rows, int cursorRow, int cursorCol)
        => rows is not null && cursorRow >= 0 && cursorRow < rows.Count && CursorInComposer(rows, cursorRow, cursorCol);

    private static bool CursorInComposer(IReadOnlyList<string> rows, int cursorRow, int cursorCol)
    {
        var line = rows[cursorRow];
        if (string.IsNullOrWhiteSpace(line)) return false;

        // The first non-border, non-space column is the prompt marker.
        var first = 0;
        while (first < line.Length && System.Array.IndexOf(BorderOrSpace, line[first]) >= 0) first++;
        if (first >= line.Length || line[first] != '>') return false;
        if (first + 1 < line.Length && line[first + 1] == '>') return false; // ">>" is the mode-cycle arrow

        // The editable input begins just after "> " (or after a bare ">"). The cursor must sit within the input
        // span: at or after the input start (so a hidden/stale CursorCol=-1 fails), and strictly left of the
        // closing box border if there is one (so a right-border / off-target cursor fails). Both bounds matter
        // (issue #1777, finding 2).
        var inputStart = (first + 1 < line.Length && line[first + 1] == ' ') ? first + 2 : first + 1;
        if (cursorCol < inputStart) return false;
        var rightBorder = RightBorderColumn(line);
        if (rightBorder >= 0 && cursorCol >= rightBorder) return false;

        // Positively framed as an input box: a box-border row adjacent, or the mode-status footer just below.
        return HasAdjacentBorder(rows, cursorRow) || HasModeStatusBelow(rows, cursorRow);
    }

    /// <summary>The column of the closing box border on a row (the trailing <c>│</c>), or -1 when the row has
    /// no right border. Used as the upper bound of the composer input span.</summary>
    private static int RightBorderColumn(string line)
    {
        var c = line.Length - 1;
        while (c >= 0 && (line[c] == ' ' || line[c] == '\t' || line[c] == '\r')) c--;
        return c >= 0 && System.Array.IndexOf(VerticalBorder, line[c]) >= 0 ? c : -1;
    }

    private static bool HasAdjacentBorder(IReadOnlyList<string> rows, int row)
        => (row - 1 >= 0 && IsBoxBorderRow(rows[row - 1])) || (row + 1 < rows.Count && IsBoxBorderRow(rows[row + 1]));

    /// <summary>A row that is only box-drawing horizontal edge (a top/bottom border of the input box).</summary>
    private static bool IsBoxBorderRow(string? row)
    {
        if (string.IsNullOrWhiteSpace(row)) return false;
        var sawEdge = false;
        foreach (var c in row)
        {
            if (c == ' ' || c == '\t' || c == '\r') continue;
            if (System.Array.IndexOf(BoxEdge, c) < 0) return false; // a non-edge glyph -> content, not a border
            sawEdge = true;
        }
        return sawEdge;
    }

    /// <summary>The agent's mode-status footer (Claude Code renders it directly below the composer box).</summary>
    private static bool HasModeStatusBelow(IReadOnlyList<string> rows, int row)
    {
        var end = Math.Min(rows.Count - 1, row + 3);
        for (var i = row + 1; i <= end; i++)
        {
            var lower = (rows[i] ?? "").ToLowerInvariant();
            foreach (var anchor in ModeStatusAnchors)
                if (lower.Contains(anchor)) return true;
        }
        return false;
    }

    private static readonly string[] ModeStatusAnchors =
    {
        "bypass permissions", "plan mode", "accept edits", "shift+tab to cycle", "? for shortcuts",
    };

    private static readonly char[] BorderOrSpace =
    {
        '│','┃','┆','┇','┊','┋','╎','╏','║',
        '╭','╮','╰','╯','┌','┐','└','┘','╔','╗','╚','╝',
        '─','━','═','┄','┅','┈','┉','|',' ','\t','\r',
    };

    /// <summary>Glyphs that may appear in a pure box-border row (a top/bottom edge of the input box).</summary>
    private static readonly char[] BoxEdge =
    {
        '─','━','═','┄','┅','┈','┉','╭','╮','╰','╯','┌','┐','└','┘','╔','╗','╚','╝','│','║','|',
    };

    /// <summary>Vertical box borders that frame the right edge of an input box.</summary>
    private static readonly char[] VerticalBorder = { '│','┃','║','|' };
}
