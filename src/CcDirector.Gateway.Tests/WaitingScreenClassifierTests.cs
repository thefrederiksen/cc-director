using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The cursor-anchored fail-closed classifier (issue #1777, round-3): the spoken words may be typed ONLY when
/// the cursor is positively inside the agent's framed composer input box. A bare "&gt;" row is not a composer,
/// menu-like text elsewhere while the cursor is in the composer is stale, and a menu owns the turn only when
/// the cursor is on a menu option. Everything the cursor does not positively resolve fails closed.
/// </summary>
public sealed class WaitingScreenClassifierTests
{
    // A Claude permission menu on screen, cursor parked on the selected option (row 3, the "❯ 1. Yes" line).
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

    // A Claude composer, cursor in the empty input box (row 2), framed by box borders and a mode-status footer.
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
        => Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(new[] { "x" }, 0, 0, hasGrid: false));

    [Fact]
    public void Classify_EmptyRows_IsBlocked()
        => Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(System.Array.Empty<string>(), -1, -1, hasGrid: true));

    [Fact]
    public void Classify_AllBlankGrid_IsBlocked()
        => Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(new[] { "", "   ", "\t" }, 0, 0, hasGrid: true));

    [Fact]
    public void Classify_CursorOutOfRange_IsBlocked()
        => Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(ComposerRows, cursorRow: 99, cursorCol: 4, hasGrid: true));

    [Fact]
    public void Classify_CursorOnMenuOption_IsMenu()
        => Assert.Equal(WaitingScreenKind.Menu, WaitingScreenClassifier.Classify(MenuRows, cursorRow: 3, cursorCol: 5, hasGrid: true));

    [Fact]
    public void Classify_CursorOnMenuQuestionLine_NotAnOption_IsBlocked()
        // The screen is a menu, but the cursor is on the question line, not on an option - fail closed.
        => Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(MenuRows, cursorRow: 2, cursorCol: 5, hasGrid: true));

    [Fact]
    public void Classify_CursorInFramedComposer_IsPlainText()
        => Assert.Equal(WaitingScreenKind.PlainText, WaitingScreenClassifier.Classify(ComposerRows, cursorRow: 2, cursorCol: 4, hasGrid: true));

    [Fact]
    public void Classify_BareSelectorRow_NoInputBoxFrame_IsBlocked()
    {
        // B1: "> production" under "Choose a deployment:" with the cursor on the selector is NOT a composer -
        // there is no input-box frame - so it must fail closed, not be typed into.
        var rows = new[] { "Choose a deployment:", "> production" };
        Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(rows, cursorRow: 1, cursorCol: 5, hasGrid: true));
    }

    [Fact]
    public void Classify_StaleMenuAboveLiveComposer_CursorInComposer_IsPlainText()
    {
        // B2-new: an answered menu is still visible ABOVE the live composer. The cursor is in the composer, so
        // this is plain text - the stale menu is ignored, the words go in the composer.
        var rows = new[]
        {
            "Do you want to proceed?",
            "❯ 1. Yes",
            "  2. No",
            "╭──────────────────────────────────────╮",
            "│ >                                     │",
            "╰──────────────────────────────────────╯",
            "  ? for shortcuts",
        };
        Assert.Equal(WaitingScreenKind.PlainText, WaitingScreenClassifier.Classify(rows, cursorRow: 4, cursorCol: 4, hasGrid: true));
    }

    [Fact]
    public void Classify_ComposerPromptButCursorBeforeThePrompt_IsBlocked()
    {
        // The cursor is to the LEFT of the input start (not inside the editable input) - not a positive signal.
        Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(ComposerRows, cursorRow: 2, cursorCol: 1, hasGrid: true));
    }

    [Fact]
    public void Classify_ModeCycleArrow_IsNotAComposer_IsBlocked()
    {
        var rows = new[] { "output", ">> bypass permissions on (shift+tab to cycle)" };
        Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(rows, cursorRow: 1, cursorCol: 5, hasGrid: true));
    }

    [Fact]
    public void LooksLikePlainTextPrompt_FramedByModeStatusFooterAlone_IsTrue()
    {
        // No box border, but the mode-status footer just below anchors the composer positively.
        var rows = new[] { "some output", "> ", "  bypass permissions on (shift+tab to cycle)" };
        Assert.True(WaitingScreenClassifier.LooksLikePlainTextPrompt(rows, cursorRow: 1, cursorCol: 2));
    }
}
