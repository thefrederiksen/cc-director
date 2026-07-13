using CcDirector.Gateway.CarMode;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Car Mode Help content (Help Mode, issue #1441) is the ONE source both help triggers read - the Help
/// button (GET /carmode/help) and the spoken "help" (the brain's get_help tool). These tests lock its shape
/// so it can never quietly go empty or stop teaching the two-way addressing model the phase settled.
/// </summary>
public sealed class CarModeHelpTests
{
    [Fact]
    public void Script_TeachesBothAddressingModes_AndHowToEndATurn()
    {
        var script = CarModeHelp.Script;
        Assert.False(string.IsNullOrWhiteSpace(script));
        // It must teach commanding the manager AND relaying into a session (at least one relay verb), plus
        // the sign-off phrase - the three things the owner needs to drive Car Mode.
        Assert.Contains("command", script, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tell", script, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("over and out", script, System.StringComparison.OrdinalIgnoreCase);
        // Spoken prose, read aloud: no markdown list markers or headings.
        Assert.DoesNotContain("\n-", script);
        Assert.DoesNotContain("#", script);
    }

    [Fact]
    public void CheatSheet_HasTheTwoModes_EachWithHintAndExamples()
    {
        var sheet = CarModeHelp.CheatSheet;
        Assert.Equal(2, sheet.Modes.Count);
        foreach (var mode in sheet.Modes)
        {
            Assert.False(string.IsNullOrWhiteSpace(mode.Title));
            Assert.False(string.IsNullOrWhiteSpace(mode.Hint));
            Assert.NotEmpty(mode.Examples);
            Assert.All(mode.Examples, e => Assert.False(string.IsNullOrWhiteSpace(e)));
        }
        Assert.False(string.IsNullOrWhiteSpace(sheet.EndTurn));
        Assert.False(string.IsNullOrWhiteSpace(sheet.Help));
    }
}
