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
        // TYPING is allowed only on a composer with NO menu-ish structure ANYWHERE ELSE on the grid (issue
        // #1777): a numbered/lettered option line, a drawn selection marker, a bare-marker selector label, or a
        // pick/choose/select/proceed prompt all block typing. The confident composer line itself is excluded
        // from this scan (it is a "> <typed text>" line and would otherwise trip the bare-marker rule); a stale
        // selector on any OTHER row still blocks. When in doubt, block.
        if (hasComposer && !HasMenuishStructure(rows, excludeRow: cursorRow)) return WaitingScreenKind.PlainText;
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

    /// <summary>
    /// Any menu-ish structure on the grid (issue #1777): a numbered/lettered option line, a drawn selection
    /// marker on an option, a bare <c>❯</c>/<c>&gt;</c> selector label (a marker followed by a non-numbered
    /// label - "&gt; production"), or a pick/choose/select/proceed prompt (a line ending in ':' or '?' naming
    /// one of those). Typing is refused when any is present. <paramref name="excludeRow"/> is the confident
    /// composer's own row, skipped so a legitimate "&gt; typed text" composer line does not trip the
    /// bare-marker rule; a stale selector on any OTHER row still blocks. Deliberately conservative (an ordinary
    /// numbered list also blocks) - the floor types only onto an unambiguous composer. Public so a test can
    /// assert it directly.
    /// </summary>
    public static bool HasMenuishStructure(IReadOnlyList<string>? rows, int excludeRow = -1)
    {
        if (rows is null) return false;
        for (var i = 0; i < rows.Count; i++)
        {
            if (i == excludeRow) continue;
            var row = rows[i];
            if (string.IsNullOrWhiteSpace(row)) continue;
            if (WingmanMenuLogic.IsOptionLine(row)) return true;
            if (WingmanMenuLogic.IsSelectedOptionLine(row)) return true;
            if (IsBareMarkerLabel(row)) return true;
            if (IsPickPrompt(row)) return true;
        }
        return false;
    }

    /// <summary>A bare selector label: after the box edge, a single <c>&gt;</c>/<c>❯</c> marker then a space
    /// then a NON-numbered label ("&gt; production", "❯ deploy to prod") - a drawn selection, not a composer.
    /// A bare "&gt;" with no label (an empty composer) and a numbered option ("&gt; 1. Yes", caught elsewhere)
    /// are not this.</summary>
    private static bool IsBareMarkerLabel(string row)
    {
        var t = row.TrimStart(BorderOrSpace);
        if (t.Length == 0) return false;
        if (t[0] != '>' && t[0] != '❯') return false;
        if (t.Length >= 2 && t[1] == '>') return false;                 // ">>" mode arrow
        var rest = t[1..].TrimStart();
        if (rest.Length == 0) return false;                             // bare marker, empty input -> composer
        if (WingmanMenuLogic.IsOptionLine(rest)) return false;          // "> 1. Yes" is a numbered option, handled elsewhere
        return true;                                                    // a marker with a non-numbered label
    }

    /// <summary>A menu prompt line: it ends in ':' or '?' and names a pick/choose/select/proceed action
    /// ("Pick environment:", "Choose a deployment:", "Select an option:", "Proceed?"). The terminator keeps an
    /// ordinary sentence that merely uses the word ("I'll select the rows.") from tripping it.</summary>
    private static bool IsPickPrompt(string row)
    {
        var trimmed = row.TrimEnd();
        if (trimmed.Length == 0) return false;
        var last = trimmed[^1];
        if (last != ':' && last != '?') return false;
        var lower = trimmed.ToLowerInvariant();
        return lower.Contains("pick") || lower.Contains("choose") || lower.Contains("select")
            || lower.Contains("proceed") || lower.Contains("which option") || lower.Contains("which one");
    }

    private static bool CursorInComposer(IReadOnlyList<string> rows, int cursorRow, int cursorCol)
    {
        var line = rows[cursorRow];
        if (string.IsNullOrWhiteSpace(line)) return false;

        // The first non-border, non-space column is the prompt marker. Claude Code draws its composer prompt
        // as '❯' (U+276F), not the ASCII '>'; accepting only '>' meant a REAL Claude Code composer was never
        // positively recognized, so the classifier's own ambiguity rule (menu-ish structure + live composer ->
        // Blocked, never Menu) could not fire - one of the two defects behind the session-115 menu misfire.
        var first = 0;
        while (first < line.Length && System.Array.IndexOf(BorderOrSpace, line[first]) >= 0) first++;
        if (first >= line.Length || (line[first] != '>' && line[first] != '❯')) return false;
        if (first + 1 < line.Length && (line[first + 1] == '>' || line[first + 1] == '❯')) return false; // ">>" is the mode-cycle arrow

        // THE COMPOSER INVARIANT (issue #1777, final tighten): a real text composer has the visible cursor at
        // the INSERTION POINT - it TRAILS the input, right after the last non-space character the user typed, or
        // right after "> " when the input is empty. A "> production" SELECTION line instead has its marker at
        // the start and a drawn label after it, with the cursor at the marker, NOT trailing typed text. So the
        // cursor must sit EXACTLY at the trailing edge of the input content; anything else (a hidden/stale -1, a
        // cursor at the marker or mid-label, a cursor past the input or on the border) is not a composer. This
        // is one bound that closes BOTH the "> label selector" hole and the "no right border, cursor unbounded"
        // hole - the column is bounded on both sides, always.
        var inputStart = (first + 1 < line.Length && line[first + 1] == ' ') ? first + 2 : first + 1;
        var rightBorder = RightBorderColumn(line);
        var scanEnd = rightBorder >= 0 ? rightBorder : line.Length;   // the input span is [inputStart, scanEnd)
        var lastContent = inputStart - 1;
        for (var c = Math.Min(scanEnd, line.Length) - 1; c >= inputStart; c--)
        {
            if (line[c] != ' ' && line[c] != '\t' && line[c] != '\r') { lastContent = c; break; }
        }
        var emptyInput = lastContent < inputStart;
        var insertionPoint = lastContent + 1;   // right after the last typed char, or == inputStart when empty
        if (cursorCol == insertionPoint) { /* the trailing insertion point - a confident composer */ }
        // Account for TRAILING-TRIMMED rows (issue #1777): ScreenGridResponse rows are trailing-trimmed, so a
        // footer-only EMPTY "> " composer arrives as ">" - the trimmed space is gone, so the visible cursor sits
        // one column PAST the marker, at the true input column. Accept that only for an empty composer with no
        // right border. A "> label" selector keeps its non-space label (never empty here), so this never
        // loosens the selector rejection - a cursor not at the trailing edge of a labelled line still fails.
        else if (emptyInput && rightBorder < 0 && cursorCol == insertionPoint + 1) { /* trimmed "> " space */ }
        else return false;

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
