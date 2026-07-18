using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The two-anchor fail-closed classifier (issue #1777, round-4). Cursor VISIBILITY is the discriminator: a
/// text composer keeps the hardware cursor visible in its input box, while Claude Code's Ink permission menu
/// HIDES the cursor and draws its own selection marker. So typing requires a VISIBLE cursor positively inside a
/// framed composer with no menu on the grid; a menu is owned by its drawn marker (cursor-independent); and
/// everything else - including a menu with a hidden cursor being typed into - fails closed.
/// </summary>
public sealed class WaitingScreenClassifierTests
{
    // A Claude permission menu drawn with its selection marker. On a real menu the hardware cursor is HIDDEN.
    private static readonly string[] MenuRows =
    {
        "╭──────────────────────────────────────────────╮",
        "│ Bash command                                 │",
        "│ Do you want to proceed?                      │",
        "│ ❯ 1. Yes                                     │",
        "│   2. Yes, and don't ask again this session   │",
        "│   3. No, and tell Claude what to do          │",
        "╰──────────────────────────────────────────────╯",
    };

    // A Claude composer, VISIBLE cursor in the empty input box (row 2), framed by box borders and a footer.
    private static readonly string[] ComposerRows =
    {
        "I finished the last change. What next?",
        "╭──────────────────────────────────────╮",
        "│ >                                     │",
        "╰──────────────────────────────────────╯",
        "  ? for shortcuts",
    };

    [Fact]
    public void Classify_NoGrid_IsBlocked()
        => Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(new[] { "x" }, 0, 0, true, false, hasGrid: false));

    [Fact]
    public void Classify_AllBlankGrid_IsBlocked()
        => Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(new[] { "", "   ", "\t" }, 0, 0, true, false, hasGrid: true));

    [Fact]
    public void Classify_MenuWithHiddenCursor_IsMenu()
        // The real-Claude case: the menu is drawn with its marker, the hardware cursor is HIDDEN and its cell
        // is stale. The menu must still be recognized (via the marker) and answered.
        => Assert.Equal(WaitingScreenKind.Menu, WaitingScreenClassifier.Classify(MenuRows, cursorRow: 0, cursorCol: -1, cursorVisible: false, isAlternateScreen: true, hasGrid: true));

    [Fact]
    public void Classify_VisibleCursorInFramedComposer_IsPlainText()
        => Assert.Equal(WaitingScreenKind.PlainText, WaitingScreenClassifier.Classify(ComposerRows, cursorRow: 2, cursorCol: 4, cursorVisible: true, isAlternateScreen: false, hasGrid: true));

    [Fact]
    public void Classify_ComposerButCursorHidden_IsBlocked()
        // Typing needs a VISIBLE cursor - a hidden cursor cell is stale and cannot be trusted.
        => Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(ComposerRows, cursorRow: 2, cursorCol: 4, cursorVisible: false, isAlternateScreen: false, hasGrid: true));

    [Fact]
    public void Classify_ComposerButAlternateScreen_IsBlocked()
        => Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(ComposerRows, cursorRow: 2, cursorCol: 4, cursorVisible: true, isAlternateScreen: true, hasGrid: true));

    [Fact]
    public void Classify_MenuMarkerAndLiveComposerBothPresent_IsBlocked()
    {
        // Ambiguous: a drawn menu marker AND a live composer on the same grid. When in doubt, block.
        var rows = new[]
        {
            "❯ 1. Yes",
            "  2. No",
            "╭──────────────────────────────────────╮",
            "│ >                                     │",
            "╰──────────────────────────────────────╯",
            "  ? for shortcuts",
        };
        Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(rows, cursorRow: 3, cursorCol: 4, cursorVisible: true, isAlternateScreen: false, hasGrid: true));
    }

    [Fact]
    public void Classify_BorderedSelectorHiddenCursor_IsBlocked()
    {
        // B1: a bordered "> production" selector (a menu selection, hidden cursor) is NOT a composer. It has no
        // numbered option after the marker, so it is not a recognized menu either -> Blocked.
        var rows = new[] { "Choose a deployment:", "╭──────────────╮", "│ > production │", "╰──────────────╯" };
        Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(rows, cursorRow: 2, cursorCol: 4, cursorVisible: false, isAlternateScreen: false, hasGrid: true));
    }

    // A small composer with a known geometry: row 2 = "│ >   │" -> prompt '>' at col 2, input starts col 4,
    // closing border '│' at col 6.
    private static readonly string[] TinyComposer = { "output", "╭─────╮", "│ >   │", "╰─────╯", "  ? for shortcuts" };

    [Fact]
    public void Classify_TinyComposer_CursorInInput_IsPlainText()
        => Assert.Equal(WaitingScreenKind.PlainText, WaitingScreenClassifier.Classify(TinyComposer, cursorRow: 2, cursorCol: 4, cursorVisible: true, isAlternateScreen: false, hasGrid: true));

    [Fact]
    public void Classify_CursorColMinusOne_IsBlocked()
        // finding 2: a stale/hidden CursorCol=-1 is not inside any input span.
        => Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(TinyComposer, cursorRow: 2, cursorCol: -1, cursorVisible: true, isAlternateScreen: false, hasGrid: true));

    [Fact]
    public void Classify_CursorOnRightBorder_IsBlocked()
        // finding 2: the cursor is on the closing box border (col 6), not inside the editable input span.
        => Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(TinyComposer, cursorRow: 2, cursorCol: 6, cursorVisible: true, isAlternateScreen: false, hasGrid: true));

    [Fact]
    public void Classify_CursorBeforeThePrompt_IsBlocked()
        => Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(TinyComposer, cursorRow: 2, cursorCol: 1, cursorVisible: true, isAlternateScreen: false, hasGrid: true));

    [Fact]
    public void Classify_ModeCycleArrow_IsBlocked()
    {
        var rows = new[] { "output", ">> bypass permissions on (shift+tab to cycle)" };
        Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(rows, cursorRow: 1, cursorCol: 5, cursorVisible: true, isAlternateScreen: false, hasGrid: true));
    }

    [Fact]
    public void Classify_AlternateScreenNotAMenu_IsBlocked()
    {
        var rows = new[] { "a full screen viewer", "line of content", "status: reading" };
        Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(rows, cursorRow: 2, cursorCol: 8, cursorVisible: false, isAlternateScreen: true, hasGrid: true));
    }

    [Fact]
    public void LooksLikePlainTextPrompt_FramedByModeStatusFooterAlone_IsTrue()
    {
        var rows = new[] { "some output", "> ", "  bypass permissions on (shift+tab to cycle)" };
        Assert.True(WaitingScreenClassifier.LooksLikePlainTextPrompt(rows, cursorRow: 1, cursorCol: 2));
    }
}
