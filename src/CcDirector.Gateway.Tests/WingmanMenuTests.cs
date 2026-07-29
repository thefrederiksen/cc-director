using CcDirector.Gateway.Speech;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Menu handling (issue #531): the cheap "is this a menu" gate, local mapping of a spoken answer to
/// an option, tolerant parsing of the brain's menu JSON, and the speakable reading of a menu.
/// </summary>
public sealed class WingmanMenuTests
{
    private static WingmanMenu PermissionMenu() => new()
    {
        IsMenu = true,
        Question = "Do you want to proceed?",
        SelectionMode = "single",
        Submit = "",
        Options = new()
        {
            new WingmanMenuOption { Key = "1. Yes", Send = "1\r" },
            new WingmanMenuOption { Key = "2. Yes, and don't ask again", Send = "2\r", Note = "A standing grant for this command", Recommended = true },
            new WingmanMenuOption { Key = "3. No", Send = "3\r" },
        },
    };

    // ===== LooksLikeMenu (the cheap gate) =====

    [Fact]
    public void LooksLikeMenu_NumberedOptions_IsTrue()
    {
        var term = "Do you want to proceed?\n❯ 1. Yes\n  2. Yes, and don't ask again\n  3. No\n";
        Assert.True(WingmanMenuLogic.LooksLikeMenu(term));
    }

    [Fact]
    public void LooksLikeMenu_PlainProse_IsFalse()
    {
        var term = "I finished editing the file and ran the tests. Everything passes. What would you like next?";
        Assert.False(WingmanMenuLogic.LooksLikeMenu(term));
    }

    [Fact]
    public void LooksLikeMenu_ClaudeCodePermissionPrompt_IsTrue()
    {
        // A boxed permission prompt whose option lines start with box-drawing - caught by the
        // "❯ 1" cursor / "don't ask again" fingerprints even when the per-line regex is fooled.
        var term =
            "╭──────────────────────────────────────────╮\n" +
            "│ Bash command                              │\n" +
            "│ Do you want to proceed?                   │\n" +
            "│ ❯ 1. Yes                                  │\n" +
            "│   2. Yes, and don't ask again this session│\n" +
            "│   3. No, and tell Claude what to do       │\n" +
            "╰──────────────────────────────────────────╯\n";
        Assert.True(WingmanMenuLogic.LooksLikeMenu(term));
    }

    [Fact]
    public void LooksLikeMenu_NumberedListInScrollback_IsFalse()
    {
        // A numbered list the agent printed earlier, now buried under a long tail of normal output,
        // is NOT an active menu - the gate must ignore it (only the last ~40 lines count).
        var top = "Here are the choices:\n1. Yes\n2. No\n3. Maybe\n";
        var filler = string.Concat(System.Linq.Enumerable.Repeat("...working on the next thing...\n", 60));
        Assert.False(WingmanMenuLogic.LooksLikeMenu(top + filler));
    }

    [Fact]
    public void LooksLikeMenu_EmptyOrNull_IsFalse()
    {
        Assert.False(WingmanMenuLogic.LooksLikeMenu(null));
        Assert.False(WingmanMenuLogic.LooksLikeMenu(""));
    }

    // ===== LiveScreenLooksLikeMenu (issue #1777: the live grid rules the verdict) =====

    /// <summary>
    /// The exact defect (issue #1777): a full-screen Claude picker draws on the ALTERNATE screen, where the
    /// scrollback is empty by design. The old scrollback-only gate saw "" and returned false, so voice-turn
    /// typed the spoken words into the picker. The live-grid gate reads the resolved on-screen rows and sees
    /// the menu - this pins the fix: empty scrollback is NOT a menu, but the SAME menu on the live grid IS.
    /// </summary>
    private static readonly string[] AltScreenClaudeMenuRows =
    {
        "> run the tests",
        "",
        "╭──────────────────────────────────────────────╮",
        "│ Bash command                                 │",
        "│ dotnet test                                  │",
        "│                                              │",
        "│ Do you want to proceed?                      │",
        "│ ❯ 1. Yes                                     │",
        "│   2. Yes, and don't ask again this session   │",
        "│   3. No, and tell Claude what to do          │",
        "╰──────────────────────────────────────────────╯",
    };

    [Fact]
    public void LiveScreenLooksLikeMenu_AltScreenClaudeMenu_IsTrue_EvenThoughScrollbackIsEmpty()
    {
        // The scrollback (what the old gate read) is EMPTY on the alternate screen - so the old path misses.
        Assert.False(WingmanMenuLogic.LooksLikeMenu(""));
        // The live grid holds the menu - the new gate catches it. This is the whole fix.
        Assert.True(WingmanMenuLogic.LiveScreenLooksLikeMenu(AltScreenClaudeMenuRows));
    }

    [Fact]
    public void LiveScreenLooksLikeMenu_PlainPrompt_IsFalse()
    {
        var rows = new[]
        {
            "I finished editing the file and ran the tests. Everything passes.",
            "",
            "> ",
        };
        Assert.False(WingmanMenuLogic.LiveScreenLooksLikeMenu(rows));
    }

    [Fact]
    public void LiveScreenLooksLikeMenu_EmptyOrNull_IsFalse()
    {
        Assert.False(WingmanMenuLogic.LiveScreenLooksLikeMenu(null));
        Assert.False(WingmanMenuLogic.LiveScreenLooksLikeMenu(System.Array.Empty<string>()));
    }

    // ===== IsOptionLine (issue #1777, round-3: is the CURSOR on a menu option?) =====

    [Theory]
    [InlineData("❯ 1. Yes", true)]
    [InlineData("  2. No", true)]
    [InlineData("│ 3. Cancel │", true)]
    [InlineData("a) Apply", true)]
    [InlineData("Do you want to proceed?", false)]
    [InlineData("> production", false)]        // a bare selector, not a numbered/lettered option line
    [InlineData("│ >  │", false)]              // a composer input line
    [InlineData("", false)]
    public void IsOptionLine_ClassifiesRows(string row, bool expected)
        => Assert.Equal(expected, WingmanMenuLogic.IsOptionLine(row));

    // ===== IsSelectedOptionLine / LiveScreenHasMenuSelection (round-4: menu owned by its DRAWN marker) =====

    [Theory]
    [InlineData("❯ 1. Yes", true)]
    [InlineData("│ ❯ 1. Yes │", true)]
    [InlineData("> 1. Yes", true)]
    [InlineData("  2. No", false)]          // an option, but not the SELECTED one (no marker)
    [InlineData("> production", false)]     // a bare selector, no numbered option after the marker
    [InlineData("Do you want to proceed?", false)]
    public void IsSelectedOptionLine_DetectsTheDrawnMarker(string row, bool expected)
        => Assert.Equal(expected, WingmanMenuLogic.IsSelectedOptionLine(row));

    [Fact]
    public void LiveScreenHasMenuSelection_MarkerPlusOptions_IsTrue()
    {
        var rows = new[] { "Do you want to proceed?", "❯ 1. Yes", "  2. No" };
        Assert.True(WingmanMenuLogic.LiveScreenHasMenuSelection(rows));
    }

    [Fact]
    public void LiveScreenHasMenuSelection_BareSelectorNoNumberedOptions_IsFalse()
        => Assert.False(WingmanMenuLogic.LiveScreenHasMenuSelection(new[] { "Choose a deployment:", "> production" }));

    [Fact]
    public void LiveScreenHasMenuSelection_OptionsButNoDrawnMarker_IsFalse()
        // A styled/reverse-video picker with no textual ❯/> marker is not recognized (fail closed, deferred).
        => Assert.False(WingmanMenuLogic.LiveScreenHasMenuSelection(new[] { "  1. Yes", "  2. No" }));

    // ===== MenuHasAnswerableOptions (finding 4: reject empty/invented labels) =====

    [Fact]
    public void MenuHasAnswerableOptions_RealLabelsOnScreen_IsTrue()
    {
        var rows = new[] { "Do you want to proceed?", "❯ 1. Yes", "  2. Yes, and don't ask again", "  3. No" };
        Assert.True(WingmanMenuLogic.MenuHasAnswerableOptions(PermissionMenu(), rows));
    }

    [Fact]
    public void MenuHasAnswerableOptions_BareNumberLabels_IsFalse()
    {
        var menu = new WingmanMenu
        {
            IsMenu = true,
            Options = new() { new() { Key = "1.", Send = "1\r" }, new() { Key = "2.", Send = "2\r" } },
        };
        Assert.False(WingmanMenuLogic.MenuHasAnswerableOptions(menu, new[] { "❯ 1.", "  2." }));
    }

    [Fact]
    public void MenuHasAnswerableOptions_LabelNotOnScreen_IsFalse()
    {
        // The model invented an option that is not actually on the live grid.
        var menu = new WingmanMenu
        {
            IsMenu = true,
            Options = new() { new() { Key = "1. Yes", Send = "1\r" }, new() { Key = "2. Delete everything", Send = "2\r" } },
        };
        Assert.False(WingmanMenuLogic.MenuHasAnswerableOptions(menu, new[] { "Proceed?", "❯ 1. Yes", "  2. No" }));
    }

    // ===== MatchOption (local spoken-answer mapping) =====

    [Theory]
    [InlineData("two", 1)]
    [InlineData("number 3", 2)]
    [InlineData("option 1", 0)]
    public void MatchOption_ByNumber(string said, int expected)
        => Assert.Equal(expected, WingmanMenuLogic.MatchOption(PermissionMenu(), said));

    [Fact]
    public void MatchOption_Recommended_PicksTheRecommendedOption()
        => Assert.Equal(1, WingmanMenuLogic.MatchOption(PermissionMenu(), "go with the recommended one"));

    [Theory]
    [InlineData("the first one", 0)]
    [InlineData("third", 2)]
    [InlineData("the last one", 2)]
    public void MatchOption_ByOrdinal(string said, int expected)
        => Assert.Equal(expected, WingmanMenuLogic.MatchOption(PermissionMenu(), said));

    [Fact]
    public void MatchOption_ByLabel_MatchesTheLongestContainedLabel()
        => Assert.Equal(1, WingmanMenuLogic.MatchOption(PermissionMenu(), "yes and don't ask again please"));

    [Fact]
    public void MatchOption_NoConfidentMatch_ReturnsMinusOne()
        => Assert.Equal(-1, WingmanMenuLogic.MatchOption(PermissionMenu(), "hmm I'm not sure what to do here"));

    // ===== ParseMenu (tolerant of the model's JSON) =====

    [Fact]
    public void ParseMenu_ValidJson_BuildsTheMenu()
    {
        var json = "{\"isMenu\":true,\"question\":\"Proceed?\",\"selectionMode\":\"single\",\"submit\":\"\"," +
                   "\"options\":[{\"key\":\"1. Yes\",\"send\":\"1\\r\",\"recommended\":true},{\"key\":\"2. No\",\"send\":\"2\\r\"}]}";
        var menu = WingmanTranslator.ParseMenu(json);
        Assert.True(menu.IsMenu);
        Assert.Equal(2, menu.Options.Count);
        Assert.Equal("1\r", menu.Options[0].Send);
        Assert.True(menu.Options[0].Recommended);
    }

    [Fact]
    public void ParseMenu_DropsOptionsWithNoSend()
    {
        var json = "{\"isMenu\":true,\"options\":[{\"key\":\"1. Yes\",\"send\":\"1\\r\"},{\"key\":\"bad\",\"send\":\"\"}]}";
        var menu = WingmanTranslator.ParseMenu(json);
        Assert.Single(menu.Options);
    }

    [Fact]
    public void ParseMenu_Garbage_DegradesToNotAMenu()
    {
        Assert.False(WingmanTranslator.ParseMenu("the model rambled with no json").IsMenu);
        Assert.False(WingmanTranslator.ParseMenu("").IsMenu);
    }

    // ===== BuildMenuSpoken (ear-friendly reading) =====

    [Fact]
    public void BuildMenuSpoken_ReadsQuestionOptionsAndHowToAnswer()
    {
        var s = WingmanTranslator.BuildMenuSpoken(SpokenLanguages.English, PermissionMenu());
        Assert.Contains("Do you want to proceed?", s);
        Assert.Contains("Option 1: Yes", s);              // the leading "1." marker is stripped for speech
        // The recommendation is now a WHOLE SENTENCE rather than a "(recommended)" tag welded onto the
        // end of the option line (issue #1009): where a recommendation goes, and how it agrees, is a
        // per-language decision, and a tag glued on in code has already made it for English.
        Assert.Contains("That is the recommended option.", s);
        Assert.Contains("Say the number", s);
    }
}
