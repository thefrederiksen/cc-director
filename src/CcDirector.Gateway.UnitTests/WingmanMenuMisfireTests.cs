using System.Text.Json;
using CcDirector.Gateway.Speech;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The session-115 menu misfire (issue devthrottle_internal#1195): the pure classifier declared a finished
/// summary - a numbered list of completed work above an empty composer - to be "waiting on a menu", because
/// (a) any two lines shaped like "1. ..." counted as menu options wherever they sat on the grid, and (b) a
/// stale composer glyph supplied the "drawn selection marker". These tests pin the two mechanical fixes
/// (the contiguous option block; the real U+276F composer prompt) with a sanitized twin of the real grid,
/// and the SCREEN-verdict contract that makes the model - not the regex - the authority on "menu".
/// The REAL captured grid lives in the private repo's goldens corpus, reachable via WINGMAN_GOLDENS_DIR.
/// </summary>
public sealed class WingmanMenuMisfireTests
{
    // A sanitized twin of session 115's live grid at misfire time: the agent's finishing summary is a
    // numbered list whose items are separated by wrapped prose (rows apart, as on the real screen), a STALE
    // '❯' glyph from an earlier frame sits at the start of the "1." row, and below it all is the agent's
    // real, empty '❯' composer with the mode-status footer. Nothing here is interactive.
    private static readonly string[] SummaryWithStaleMarker =
    {
        "● All three jobs are done:",
        "❯ 1. Updated the release notes - two commits today: the rebuilt draft plus the",
        "  final proofread pass over every section. Nothing is only on this machine",
        "  anymore, and the branch is deleted.",
        "  more prose recounting what happened, wrapped across several rows,",
        "  as a real reply wraps in a narrow terminal window.",
        "  3. Cleaned up the temp folders - nothing left to review there.",
        "",
        "──────────────────────────────────────────────",
        "❯ ",
        "──────────────────────────────────────────────",
        "  ⏵⏵ bypass permissions on (shift+tab to cycle)",
    };

    [Fact]
    public void SummaryWithStaleMarker_IsNotAMenuSelection()
        // The fix under test: "1." and "3." with paragraphs between them are prose, not an option block,
        // no matter what stale glyph sits next to the "1.".
        => Assert.False(WingmanMenuLogic.LiveScreenHasMenuSelection(SummaryWithStaleMarker));

    [Fact]
    public void SummaryWithStaleMarker_NeverClassifiesAsMenu()
        // The cursor sits in the real (empty) composer on row 9, visible, at the insertion point. Menu-ish
        // structure elsewhere on the grid keeps this Blocked rather than PlainText - but it must NEVER be
        // Menu, because Menu is what refuses voice replies and announces "waiting on a menu".
        => Assert.NotEqual(WaitingScreenKind.Menu, WaitingScreenClassifier.Classify(
            SummaryWithStaleMarker, cursorRow: 9, cursorCol: 2, cursorVisible: true, isAlternateScreen: false, hasGrid: true));

    [Fact]
    public void RealClaudeComposer_Glyph_IsRecognized()
    {
        // Claude Code draws its composer prompt as '❯' (U+276F), not '>'. Before the fix this composer was
        // invisible to the classifier, so the ambiguity rule (menu + composer -> Blocked) could never fire.
        var rows = new[]
        {
            "I finished the change. What next?",
            "──────────────────────────────────",
            "❯ ",
            "──────────────────────────────────",
            "  ⏵⏵ bypass permissions on (shift+tab to cycle)",
        };
        Assert.Equal(WaitingScreenKind.PlainText, WaitingScreenClassifier.Classify(
            rows, cursorRow: 2, cursorCol: 2, cursorVisible: true, isAlternateScreen: false, hasGrid: true));
    }

    [Fact]
    public void CompactSummaryWithStaleMarker_StillTripsTheTripwire()
    {
        // DOCUMENTED LIMIT, not a defect: a compact one-line-per-item summary with a stray marker is
        // indistinguishable from a picker by shape alone. The tripwire fires - and that is acceptable
        // BECAUSE the surfaces that block all confirm with the model first (ConfirmedMenuAsync). If this
        // assertion ever flips, re-check that the model confirmation still guards every blocking surface.
        var rows = new[] { "❯ 1. Committed the fix", "  2. Updated the tests", "  3. Cleaned up" };
        Assert.True(WingmanMenuLogic.LiveScreenHasMenuSelection(rows));
    }

    [Fact]
    public void RealPermissionMenu_StillRecognized()
    {
        var rows = new[]
        {
            "╭──────────────────────────────────────────────╮",
            "│ Do you want to proceed?                      │",
            "│ ❯ 1. Yes                                     │",
            "│   2. Yes, and don't ask again this session   │",
            "│   3. No, and tell Claude what to do          │",
            "╰──────────────────────────────────────────────╯",
        };
        Assert.True(WingmanMenuLogic.LiveScreenHasMenuSelection(rows));
    }

    [Fact]
    public void MenuWithOneWrappedOptionLabel_StillRecognized()
    {
        // One non-option row inside the block is tolerated: a long option label wraps in a narrow terminal.
        var rows = new[]
        {
            "Do you want to proceed?",
            "❯ 1. Yes, apply the change to every file in the",
            "     selected folder and keep going",
            "  2. No",
        };
        Assert.True(WingmanMenuLogic.LiveScreenHasMenuSelection(rows));
    }

    [Fact]
    public void OptionsSeparatedByParagraphs_NotRecognized()
        // Two "options" in different blocks never merge across the prose between them.
        => Assert.False(WingmanMenuLogic.LiveScreenHasMenuSelection(new[]
        {
            "❯ 1. First thing I did",
            "  a paragraph about it",
            "  and another line of prose",
            "  2. Second thing I did",
        }));

    // ===== The SCREEN verdict: the model's JSON judgment riding the narration call =====

    [Fact]
    public void ParseScreenVerdict_Menu_WithQuestionAndOptions()
    {
        var v = WingmanTranslator.ParseScreenVerdict(
            "===DEVTHROTTLE-ANSWER-BEGIN===\nIt wants permission to run a command.\n===DEVTHROTTLE-ANSWER-END===\n"
            + "SCREEN: {\"needs\":\"menu\",\"question\":\"Run the build command?\",\"options\":[\"1. Yes\",\"2. No\"]}");
        Assert.NotNull(v);
        Assert.Equal("menu", v!.Needs);
        Assert.Equal("Run the build command?", v.Question);
        Assert.Equal(2, v.Options.Count);
    }

    [Theory]
    [InlineData("SCREEN: {\"needs\":\"answer\"}", "answer")]
    [InlineData("SCREEN: {\"needs\":\"nothing\"}", "nothing")]
    [InlineData("the spoken part\nSCREEN:{\"needs\":\"MENU\"}", "menu")]   // case-insensitive value
    public void ParseScreenVerdict_KnownValues_Parse(string reply, string expected)
        => Assert.Equal(expected, WingmanTranslator.ParseScreenVerdict(reply)?.Needs);

    [Theory]
    [InlineData("no verdict line at all")]
    [InlineData("SCREEN: {\"needs\":\"maybe\"}")]              // unknown value is UNKNOWN, never a guess
    [InlineData("SCREEN: {\"needs\":")]                        // unbalanced braces
    [InlineData("SCREEN: not json")]
    [InlineData("")]
    public void ParseScreenVerdict_GarbageDegradesToNull(string reply)
        => Assert.Null(WingmanTranslator.ParseScreenVerdict(reply));

    [Fact]
    public void ParseScreenVerdict_LastLabelWins()
        // A model that echoes the contract's example line must not have the echo win over its verdict.
        => Assert.Equal("nothing", WingmanTranslator.ParseScreenVerdict(
            "SCREEN: {\"needs\":\"menu\",\"question\":\"...\",\"options\":[\"1. Yes\",\"2. No\"]}\n"
            + "the real one:\nSCREEN: {\"needs\":\"nothing\"}")?.Needs);

    [Fact]
    public void BuildPrompt_WithLiveScreen_CarriesScreenAndVerdictContract()
    {
        var p = WingmanTranslator.BuildPrompt(SpokenLanguages.English, "instructions", "ctx", "reply", "title",
            liveScreen: "❯ 1. the grid rows");
        Assert.Contains("❯ 1. the grid rows", p);
        Assert.Contains("SCREEN:", p);
        Assert.Contains("\"needs\":\"menu\"", p);
        // The negative rule the misfire earned: a numbered list in prose is not a menu.
        Assert.Contains("NOT a menu", p);
    }

    [Fact]
    public void BuildPrompt_WithoutLiveScreen_AsksForNoVerdict()
    {
        var p = WingmanTranslator.BuildPrompt(SpokenLanguages.English, "instructions", "ctx", "reply", "title");
        Assert.DoesNotContain("SCREEN:", p);
    }

    // ===== The per-screen verdict cache =====

    [Fact]
    public void VerdictCache_ServesOnlyTheJudgedScreen()
    {
        WingmanScreenVerdictCache.Clear();
        var rowsA = new[] { "row one", "row two" };
        var rowsB = new[] { "row one", "row two changed" };
        var key = "Local/cache-test-sid";
        WingmanScreenVerdictCache.Store(key, WingmanScreenVerdictCache.HashRows(rowsA), "menu");
        Assert.True(WingmanScreenVerdictCache.TryGet(key, WingmanScreenVerdictCache.HashRows(rowsA), out var needs));
        Assert.Equal("menu", needs);
        // The screen moved: the stale verdict must NOT be served.
        Assert.False(WingmanScreenVerdictCache.TryGet(key, WingmanScreenVerdictCache.HashRows(rowsB), out _));
    }

    // ===== The private goldens hook (real captured screens live in the internal repo only) =====

    [Fact]
    public void PrivateGoldens_WhenConfigured_ClassifyCorrectly()
    {
        // Point WINGMAN_GOLDENS_DIR at the private goldens corpus (devthrottle_internal
        // docs/wingman/goldens) to also validate the classifier against REAL captured screens. Unset (CI,
        // other machines) this test asserts nothing - the public repo never carries a real session's screen.
        var dir = Environment.GetEnvironmentVariable("WINGMAN_GOLDENS_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
                continue;   // evidence-only capture (no full-fidelity rows) - nothing to classify
            var rows = rowsEl.EnumerateArray().Select(r => r.GetString() ?? "").ToArray();
            var expected = doc.RootElement.GetProperty("expectedVerdict").GetString();
            var isMenu = WingmanMenuLogic.LiveScreenHasMenuSelection(rows);
            if (expected == "menu") Assert.True(isMenu, $"{Path.GetFileName(file)}: expected a menu");
            else Assert.False(isMenu, $"{Path.GetFileName(file)}: expected not-a-menu");
        }
    }
}
