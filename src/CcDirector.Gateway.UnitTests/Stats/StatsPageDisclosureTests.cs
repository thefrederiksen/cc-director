using System.Reflection;
using CcDirector.Gateway.Stats;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Stats;

/// <summary>
/// The guard for ruling R14 of the "Clean up Your Throttle" mission (2026-09-05): every honesty caveat
/// Your Throttle prints must name the TALLY it is talking about.
///
/// WHAT WENT WRONG. The page's headline is a ratio of submitted TURNS. Its caveat said that the message
/// composer and terminal typing on the desktop app "are counted" - and terminal typing was counted in
/// CHARACTERS and never in turns, so the sentence was false for the only unit the reader can see. It read
/// as a reassurance while covering the largest defect in the number: 594 of the owner's 771 typed
/// submissions in the week of 2026-W35 were absent from the ring's denominator, worth 28.3 of the 34
/// points by which the page disagreed with his mentor report.
///
/// WHY A TEST AND NOT A CAREFUL EDIT. A caveat is prose, and prose about a number drifts away from the
/// number silently - nothing recomputes when the code beneath it changes. This pins the two together:
/// the sentence must say "turn", and the behaviour it describes is pinned in the same slice by
/// CcDirector.Core.UnitTests TerminalTypingIsATurnTests. Neither test alone would have caught the defect;
/// the pair is the guard.
/// </summary>
public sealed class StatsPageDisclosureTests
{
    private static string[] Caveats()
    {
        var field = typeof(StatsPageEndpoint).GetField("NotCaptured", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var value = field!.GetValue(null) as string[];
        Assert.NotNull(value);
        return value!;
    }

    [Fact]
    public void TheCaveatAboutTypingSaysThatItCountsAsASUBMITTEDTURN_NotMerelyThatItIsCounted()
    {
        var typing = Assert.Single(Caveats(), c => c.Contains("terminal", StringComparison.OrdinalIgnoreCase));

        // The desktop terminal and the composer are both counted, and the sentence must say in WHAT.
        Assert.Contains("terminal in the desktop app", typing);
        Assert.Contains("message composer", typing);
        Assert.Contains("submitted turn", typing);

        // The bare claim that made it misleading - saying only that these paths "are counted", with no
        // unit - must not come back.
        Assert.DoesNotContain("terminal typing on the desktop app, are counted", typing);
    }

    [Fact]
    public void TheBrowserTerminalIsStillDisclosedAsNotCountedAtAll()
    {
        // The control. Raw keystrokes streamed from a browser's live terminal carry no surface, so they
        // are counted by nothing - and that remains true after the desktop path started counting turns.
        // Two adjacent paths with opposite answers is exactly where a caveat gets flattened into the
        // reassuring half, so the honest half is pinned too.
        var typing = Assert.Single(Caveats(), c => c.Contains("terminal", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("browser's live terminal stream", typing);
        Assert.Contains("not counted at all", typing);
    }

    [Fact]
    public void EveryCaveatNamesTheUnitItIsAbout()
    {
        // A caveat on a page whose headline is a turn ratio has to be checkable against that ratio. Each
        // one says either what it counts (a turn) or that it counts nothing.
        foreach (var caveat in Caveats())
        {
            var namesAUnit =
                caveat.Contains("turn", StringComparison.OrdinalIgnoreCase)
                || caveat.Contains("counted as voice", StringComparison.OrdinalIgnoreCase)
                || caveat.Contains("counted as typed", StringComparison.OrdinalIgnoreCase)
                || caveat.Contains("not counted", StringComparison.OrdinalIgnoreCase);
            Assert.True(namesAUnit, "This caveat does not say what it is counted AS, so a reader cannot "
                + "check it against the ring: " + caveat);
        }
    }
}
