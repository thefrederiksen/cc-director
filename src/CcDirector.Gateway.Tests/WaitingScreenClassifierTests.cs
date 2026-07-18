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
    public void LooksLikePlainTextPrompt_FooterOnly_CursorAtTrailingEdge_IsTrue()
    {
        // Footer-only composer (no right border), empty input: the trailing edge is right after "> " (col 2).
        var rows = new[] { "some output", "> ", "  bypass permissions on (shift+tab to cycle)" };
        Assert.True(WaitingScreenClassifier.LooksLikePlainTextPrompt(rows, cursorRow: 1, cursorCol: 2));
    }

    [Fact]
    public void LooksLikePlainTextPrompt_FooterOnly_CursorPastTheInput_IsFalse()
    {
        // Blocker B: with no right border, the cursor is bounded by the END of the input content. A cursor far
        // past the input (col 10 over an empty "> ") is NOT the trailing insertion point -> not a composer.
        var rows = new[] { "some output", "> ", "  bypass permissions on (shift+tab to cycle)" };
        Assert.False(WaitingScreenClassifier.LooksLikePlainTextPrompt(rows, cursorRow: 1, cursorCol: 10));
    }

    [Fact]
    public void Classify_BorderedSelectorVisibleCursorAtMarker_IsBlocked()
    {
        // Blocker A: "Pick environment:" over a framed "> production" with a VISIBLE cursor at the marker
        // (col 4, the start of "production"). The cursor is NOT trailing typed text, so it is a selector, not a
        // composer -> fail closed.
        var rows = new[] { "Pick environment:", "╭──────────────╮", "│ > production │", "╰──────────────╯" };
        Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(rows, cursorRow: 2, cursorCol: 4, cursorVisible: true, isAlternateScreen: false, hasGrid: true));
    }

    [Fact]
    public void Classify_ComposerWithTypedWord_CursorTrailing_IsPlainText()
    {
        // The same "> production" text, but the cursor TRAILS the typed word (col 14, right after "production")
        // and there is no menu prompt above - this is a real composer the user typed a word into -> PlainText.
        var rows = new[] { "do the thing", "╭──────────────╮", "│ > production │", "╰──────────────╯", "  ? for shortcuts" };
        Assert.Equal(WaitingScreenKind.PlainText, WaitingScreenClassifier.Classify(rows, cursorRow: 2, cursorCol: 14, cursorVisible: true, isAlternateScreen: false, hasGrid: true));
    }

    [Fact]
    public void Classify_ComposerCursorMidLabel_IsBlocked()
    {
        // The cursor sits in the MIDDLE of the label (col 8 of "> production"), not at the trailing edge -> not
        // a confident composer.
        var rows = new[] { "do the thing", "╭──────────────╮", "│ > production │", "╰──────────────╯", "  ? for shortcuts" };
        Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(rows, cursorRow: 2, cursorCol: 8, cursorVisible: true, isAlternateScreen: false, hasGrid: true));
    }

    // ===== HasMenuishStructure (floor rescope, finding 3: any menu structure blocks typing) =====

    [Fact]
    public void Classify_ComposerWithNumberedListPresent_IsBlocked()
    {
        // Even a valid visible-cursor composer is not typed into when a numbered (menu-ish) list is anywhere on
        // the grid. When in doubt, block.
        var rows = new[]
        {
            "Here are the choices:",
            "1. staging",
            "2. production",
            "╭──────────────────────────────────────╮",
            "│ >                                     │",
            "╰──────────────────────────────────────╯",
            "  ? for shortcuts",
        };
        Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(rows, cursorRow: 4, cursorCol: 4, cursorVisible: true, isAlternateScreen: false, hasGrid: true));
    }

    [Theory]
    [InlineData(new[] { "some prose", "1. first", "2. second" }, true)]     // a numbered/lettered option line
    [InlineData(new[] { "prose", "❯ 1. Yes" }, true)]                        // a drawn selection marker
    [InlineData(new[] { "Choose a deployment:", "the rest" }, true)]         // a ':'-terminated pick prompt
    [InlineData(new[] { "Pick environment:", "..." }, true)]
    [InlineData(new[] { "Do you want to proceed?", "..." }, true)]           // '?'-terminated proceed prompt
    [InlineData(new[] { "prose", "> production" }, true)]                    // a bare-marker selector label
    [InlineData(new[] { "prose", "❯ deploy to prod" }, true)]
    [InlineData(new[] { "I'll select the rows for you.", "more" }, false)]   // 'select' mid-sentence, not a prompt
    [InlineData(new[] { "I finished the change.", "> ", "  ? for shortcuts" }, false)] // a plain empty composer
    [InlineData(new[] { "just some prose output", "and more" }, false)]
    public void HasMenuishStructure_DetectsMenuStructure(string[] rows, bool expected)
        => Assert.Equal(expected, WaitingScreenClassifier.HasMenuishStructure(rows));
}
