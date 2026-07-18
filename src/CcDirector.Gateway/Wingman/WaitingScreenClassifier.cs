namespace CcDirector.Gateway.Wingman;

/// <summary>
/// What a session's live waiting screen is, decided PURELY from the resolved grid (issue #1777). This is
/// the fail-closed floor: the spoken words may be typed as an ordinary prompt ONLY when the screen is
/// positively a plain-text composer. Everything the classifier is not sure about is <see cref="Blocked"/> -
/// "not a recognized menu" must never collapse into "confident plain text", which was the original defect.
/// </summary>
public enum WaitingScreenKind
{
    /// <summary>The live grid carries a menu fingerprint. The caller extracts it (brain) off the LIVE grid.</summary>
    Menu,

    /// <summary>The primary screen positively shows the agent's composer/input line with the cursor at it.</summary>
    PlainText,

    /// <summary>Unreadable, blank, an unrecognized alternate-screen app, or anything uncertain: never type.</summary>
    Blocked,
}

/// <summary>
/// The pure (no-brain) waiting-screen classifier. It reads ONLY the live grid rows, the live cursor cell,
/// and the alternate-screen flag - never the scrollback - and decides whether the caller may type. It is
/// deliberately conservative: it emits <see cref="WaitingScreenKind.PlainText"/> only on a POSITIVE signal
/// (a primary-screen composer input line with the cursor sitting on it) and everything else - a blank grid,
/// an alternate-screen app we did not recognize as a menu, a primary screen with no composer - is
/// <see cref="WaitingScreenKind.Blocked"/>. That is what makes it agent-uniform: an unrecognized menu (a
/// styled picker, a non-Claude TUI) fails closed instead of being typed into.
/// </summary>
public static class WaitingScreenClassifier
{
    /// <summary>Classify the live waiting screen. <paramref name="hasGrid"/> is false for an Embedded session
    /// with no server-side parser (unreadable). A <see cref="WaitingScreenKind.Menu"/> result means the grid
    /// carries a menu fingerprint - the caller still has to extract pressable options off the LIVE grid and
    /// must fail closed if it cannot.</summary>
    public static WaitingScreenKind Classify(
        IReadOnlyList<string>? rows, int cursorRow, int cursorCol, bool isAlternateScreen, bool hasGrid)
    {
        // Unreadable: no resolved grid, or nothing on it.
        if (!hasGrid || rows is null || rows.Count == 0) return WaitingScreenKind.Blocked;

        // A blank / all-whitespace grid tells us nothing - never type into it (finding 1).
        if (rows.All(string.IsNullOrWhiteSpace)) return WaitingScreenKind.Blocked;

        // A menu fingerprint on the LIVE grid is authoritative - the caller extracts it (off the live grid).
        if (WingmanMenuLogic.LiveScreenLooksLikeMenu(rows)) return WaitingScreenKind.Menu;

        // On the alternate screen and NOT recognized as a menu: a full-screen app is showing something we
        // cannot parse. Never type into it (finding 1). This is the agent-uniform fail-closed case.
        if (isAlternateScreen) return WaitingScreenKind.Blocked;

        // Primary screen: type ONLY on a positive plain-text signal - the agent's composer input line with
        // the cursor sitting on it. When that is absent we are unsure, so we block.
        return LooksLikePlainTextPrompt(rows, cursorRow, cursorCol)
            ? WaitingScreenKind.PlainText
            : WaitingScreenKind.Blocked;
    }

    /// <summary>
    /// The POSITIVE plain-text signal (finding 1): the grid shows an agent composer/input-prompt line - a
    /// line whose first non-border, non-space character is a single "&gt;" prompt marker (not "&gt;&gt;", the
    /// mode-cycle arrow) - AND the live cursor is sitting on that line. Requiring the cursor ON the composer
    /// line is what distinguishes "waiting for me to type" from a stray "&gt;" somewhere on screen, and it is
    /// the agent-uniform reading of "the composer with the cursor at it".
    ///
    /// Conservative on purpose: a wrapped multi-line entry parks the cursor on a continuation row, not the
    /// "&gt;" row, so this returns false and the caller fails closed (tells the person to look at the
    /// terminal) rather than typing into an ambiguous state. That is the safe direction for this phase.
    /// Public so a test can assert both the positive and the blocked edges directly.
    /// </summary>
    public static bool LooksLikePlainTextPrompt(IReadOnlyList<string> rows, int cursorRow, int cursorCol)
    {
        if (rows is null || rows.Count == 0) return false;
        // The composer sits at the bottom of the frame; only look there.
        var start = Math.Max(0, rows.Count - 15);
        for (var i = rows.Count - 1; i >= start; i--)
        {
            if (!IsComposerPromptLine(rows[i])) continue;
            // The cursor must be on the composer line - the positive "with the cursor at it" signal.
            return cursorRow == i;
        }
        return false;
    }

    /// <summary>Box-drawing glyphs and pipes an agent uses to frame its input box, plus whitespace, stripped
    /// from the line edges before looking for the prompt marker (so "│ &gt; text │" reads as "&gt; text").</summary>
    private static readonly char[] BorderOrSpace =
    {
        '│','┃','┆','┇','┊','┋','╎','╏','║',
        '╭','╮','╰','╯','┌','┐','└','┘','╔','╗','╚','╝',
        '─','━','═','┄','┅','┈','┉',
        '|',
        ' ','\t','\r',
    };

    /// <summary>True when the row is an agent composer input-prompt line: after stripping leading border and
    /// whitespace, the first character is a single "&gt;" prompt marker (a bare "&gt;" empty box, or "&gt; "
    /// followed by text), but NOT "&gt;&gt;" (the mode-cycle arrow Claude Code renders below the box).</summary>
    private static bool IsComposerPromptLine(string? row)
    {
        if (string.IsNullOrWhiteSpace(row)) return false;
        var first = 0;
        while (first < row.Length && System.Array.IndexOf(BorderOrSpace, row[first]) >= 0) first++;
        if (first >= row.Length || row[first] != '>') return false;
        // ">> ..." is the mode-cycle arrow, not the input prompt.
        if (first + 1 < row.Length && row[first + 1] == '>') return false;
        // A bare ">" (empty box) or "> " (a space, then optional text) is the composer prompt.
        return first + 1 == row.Length || row[first + 1] == ' ';
    }
}
