namespace CcDirector.Gateway.Wingman;

/// <summary>
/// What a session's live waiting screen is, decided from WHERE THE CURSOR IS (issue #1777). This is the
/// fail-closed floor: the spoken words may be typed as an ordinary prompt ONLY when the cursor is positively
/// inside the agent's composer input box. Everything the classifier is not sure about is
/// <see cref="Blocked"/> - "not a recognized menu" must never collapse into "confident plain text".
/// </summary>
public enum WaitingScreenKind
{
    /// <summary>The selection cursor sits on a menu option. The caller extracts the menu (brain) off the LIVE grid.</summary>
    Menu,

    /// <summary>The cursor is positively inside the agent's composer input box - the spoken words go there.</summary>
    PlainText,

    /// <summary>Unreadable, blank, an ambiguous input, or anything the cursor does not positively resolve: never type.</summary>
    Blocked,
}

/// <summary>
/// The pure (no-brain) waiting-screen classifier, ANCHORED TO THE CURSOR (issue #1777, round-3). Text
/// fingerprints alone cannot tell a live composer from a menu selection marker (both can read "&gt; x"), nor a
/// live composer from an already-answered menu still visible above it - the cursor can. So the verdict is:
///   - the cursor is positively inside the agent's composer input box  -> PlainText (type there; any menu-like
///     text elsewhere on the grid is stale and ignored);
///   - the cursor is on a menu option of a live menu                   -> Menu (the caller extracts + presses);
///   - anything else (a bare "&gt;" row with no input-box frame, an ambiguous picker, an unreadable or blank
///     grid, a cursor resolving to neither)                            -> Blocked.
/// A bare row starting with "&gt;" is NOT a composer: the composer is positively identified as a framed input
/// box (box border or the agent's mode-status footer) with the cursor sitting after the prompt inside it. This
/// is what makes it agent-uniform: an unrecognized picker fails closed instead of being typed into.
/// </summary>
public static class WaitingScreenClassifier
{
    /// <summary>Classify the live waiting screen from the grid and the cursor cell. <paramref name="hasGrid"/>
    /// is false for an Embedded session with no server-side parser (unreadable). A <see cref="WaitingScreenKind.Menu"/>
    /// result means the cursor is on an option of a live menu - the caller still has to extract pressable
    /// options off the LIVE grid and must fail closed if it cannot.</summary>
    public static WaitingScreenKind Classify(
        IReadOnlyList<string>? rows, int cursorRow, int cursorCol, bool hasGrid)
    {
        // Unreadable: no resolved grid, or nothing on it.
        if (!hasGrid || rows is null || rows.Count == 0) return WaitingScreenKind.Blocked;

        // A blank / all-whitespace grid tells us nothing - never type into it.
        if (rows.All(string.IsNullOrWhiteSpace)) return WaitingScreenKind.Blocked;

        // No usable cursor anchor -> we cannot say WHERE the interaction is, so fail closed.
        if (cursorRow < 0 || cursorRow >= rows.Count) return WaitingScreenKind.Blocked;

        // 1) The cursor positively inside the agent's composer input box -> the spoken words go THERE. A stale
        //    menu still visible elsewhere on the grid does not matter: the cursor is in the composer.
        if (CursorInComposer(rows, cursorRow, cursorCol)) return WaitingScreenKind.PlainText;

        // 2) A menu owns the turn ONLY when the selection cursor is on a menu option AND the screen is a menu.
        if (WingmanMenuLogic.IsOptionLine(rows[cursorRow]) && WingmanMenuLogic.LiveScreenLooksLikeMenu(rows))
            return WaitingScreenKind.Menu;

        // 3) The cursor resolves to neither a composer nor a menu option - ambiguous interactive UI. Fail closed.
        return WaitingScreenKind.Blocked;
    }

    /// <summary>
    /// POSITIVE composer identification (issue #1777, round-3): the cursor is inside the agent's composer input
    /// box. That means the cursor's row is the agent's input-prompt line - after stripping the box edge it
    /// begins with a single "&gt;" prompt marker (not "&gt;&gt;", the mode-cycle arrow) - the cursor is sitting
    /// AT OR AFTER the prompt (inside the editable input, using <paramref name="cursorCol"/>), AND the line is
    /// FRAMED as an input box: an adjacent box-border row, or the agent's mode-status footer just below it.
    ///
    /// The framing requirement is what closes the "bare &gt; row" hole (B1): a menu selection like
    /// "&gt; production" under "Choose a deployment:" has no input-box frame, so it is NOT a composer and the
    /// classifier falls through to Blocked. Public so a test can assert both the positive and the blocked edges.
    /// </summary>
    public static bool LooksLikePlainTextPrompt(IReadOnlyList<string> rows, int cursorRow, int cursorCol)
    {
        if (rows is null || cursorRow < 0 || cursorRow >= rows.Count) return false;
        return CursorInComposer(rows, cursorRow, cursorCol);
    }

    private static bool CursorInComposer(IReadOnlyList<string> rows, int cursorRow, int cursorCol)
    {
        var line = rows[cursorRow];
        if (string.IsNullOrWhiteSpace(line)) return false;

        // The first non-border, non-space column is the prompt marker.
        var first = 0;
        while (first < line.Length && System.Array.IndexOf(BorderOrSpace, line[first]) >= 0) first++;
        if (first >= line.Length || line[first] != '>') return false;
        if (first + 1 < line.Length && line[first + 1] == '>') return false; // ">>" is the mode-cycle arrow

        // The editable input begins just after "> " (or after a bare ">"). The cursor must sit at or after it -
        // "the cursor sitting after the prompt inside it".
        var inputStart = (first + 1 < line.Length && line[first + 1] == ' ') ? first + 2 : first + 1;
        if (cursorCol < inputStart) return false;

        // Positively framed as an input box: a box-border row adjacent, or the mode-status footer just below.
        return HasAdjacentBorder(rows, cursorRow) || HasModeStatusBelow(rows, cursorRow);
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
            if (System.Array.IndexOf(BoxEdge, c) < 0) return false; // a non-edge glyph -> this is content, not a border
            sawEdge = true;
        }
        return sawEdge;
    }

    /// <summary>The agent's mode-status footer (Claude Code renders it directly below the composer box).
    /// Its presence just below the cursor line is a strong positive "this is the bottom composer" signal.</summary>
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

    /// <summary>Box-drawing glyphs and pipes framing the input box, plus whitespace, stripped from a line's
    /// leading edge before looking for the prompt marker.</summary>
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
}
