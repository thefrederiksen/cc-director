using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The fail-closed classifier (issue #1777, finding 1): the spoken words may be typed ONLY on a positive
/// plain-text signal (a primary-screen composer input line with the cursor on it). Everything else - a blank
/// grid, an unrecognized alternate-screen app, a primary screen with no composer, an unreadable session - is
/// Blocked. "Not a recognized menu" must never collapse into "confident plain text".
/// </summary>
public sealed class WaitingScreenClassifierTests
{
    // A real Claude permission menu, drawn on the alternate screen (full-screen picker).
    private static readonly string[] AltScreenMenuRows =
    {
        "╭──────────────────────────────────────────────╮",
        "│ Bash command                                 │",
        "│ Do you want to proceed?                      │",
        "│ ❯ 1. Yes                                     │",
        "│   2. Yes, and don't ask again this session   │",
        "│   3. No, and tell Claude what to do          │",
        "╰──────────────────────────────────────────────╯",
    };

    // A Claude composer at the bottom of the PRIMARY screen, cursor sitting in the empty input box (row 1).
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
        => Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(new[] { "anything" }, 0, 0, false, hasGrid: false));

    [Fact]
    public void Classify_EmptyRows_IsBlocked()
        => Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(System.Array.Empty<string>(), -1, -1, false, hasGrid: true));

    [Fact]
    public void Classify_AllBlankGrid_IsBlocked()
        => Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(new[] { "", "   ", "\t" }, 0, 0, false, hasGrid: true));

    [Fact]
    public void Classify_MenuFingerprint_IsMenu_EvenOnAlternateScreen()
        => Assert.Equal(WaitingScreenKind.Menu, WaitingScreenClassifier.Classify(AltScreenMenuRows, 3, 3, isAlternateScreen: true, hasGrid: true));

    [Fact]
    public void Classify_AlternateScreen_Unrecognized_IsBlocked()
    {
        // A full-screen app (alternate screen) we did NOT recognize as a menu - never type into it. This is
        // the inversion the original bug had: it is NOT a plain-text prompt just because no menu was matched.
        var appRows = new[] { "some full screen TUI", "with content we cannot parse", "and a cursor somewhere" };
        Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(appRows, 2, 5, isAlternateScreen: true, hasGrid: true));
    }

    [Fact]
    public void Classify_PrimaryComposer_CursorOnPromptLine_IsPlainText()
        => Assert.Equal(WaitingScreenKind.PlainText, WaitingScreenClassifier.Classify(ComposerRows, cursorRow: 2, cursorCol: 4, isAlternateScreen: false, hasGrid: true));

    [Fact]
    public void Classify_PrimaryComposer_CursorNotOnPromptLine_IsBlocked()
        => Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(ComposerRows, cursorRow: 0, cursorCol: 4, isAlternateScreen: false, hasGrid: true));

    [Fact]
    public void Classify_PrimaryScreen_NoComposer_IsBlocked()
    {
        // Primary screen, non-blank, not a menu, but no recognizable composer - unsure, so fail closed.
        var rows = new[] { "the agent printed some output", "and then more output", "but no input prompt is visible" };
        Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(rows, cursorRow: 2, cursorCol: 5, isAlternateScreen: false, hasGrid: true));
    }

    [Fact]
    public void Classify_ComposerButOnAlternateScreen_IsBlocked()
    {
        // Even a ">"-looking line on the alternate screen is not typed into: full-screen apps own their input.
        Assert.Equal(WaitingScreenKind.Blocked, WaitingScreenClassifier.Classify(ComposerRows, cursorRow: 2, cursorCol: 4, isAlternateScreen: true, hasGrid: true));
    }

    [Fact]
    public void LooksLikePlainTextPrompt_ModeArrowIsNotAPrompt()
    {
        // ">> bypass permissions on" is the mode-cycle arrow, NOT the input prompt.
        var rows = new[] { "output", ">> bypass permissions on (shift+tab to cycle)" };
        Assert.False(WaitingScreenClassifier.LooksLikePlainTextPrompt(rows, cursorRow: 1, cursorCol: 3));
    }
}
