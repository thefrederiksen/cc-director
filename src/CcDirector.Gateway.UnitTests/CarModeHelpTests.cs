using CcDirector.Gateway.CarMode;
using CcDirector.Gateway.Speech;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The help content is the ONE source the brain's get_help tool reads, and these tests lock its shape so it can
/// never quietly go empty or stop teaching the two-way addressing model.
///
/// It used to have a second reader - Car Mode's model-free Help button - and it used to end by teaching the
/// phrase that finished a hands-free turn. Car Mode was removed from the product (#1028) and the Assistant has an
/// explicit Send action with no end-phrase watcher, so that sign-off was teaching a command which ends nothing
/// (Gateway audit, finding C6). What the script must NOT contain is now part of what is checked here.
/// </summary>
public sealed class CarModeHelpTests
{
    [Fact]
    public void Script_TeachesBothAddressingModes_AndNoSignOffPhrase()
    {
        var script = CarModeHelp.SpokenScript(SpokenLanguages.English);
        Assert.False(string.IsNullOrWhiteSpace(script));

        // It must teach commanding the manager AND relaying into a session (at least one relay verb) - the two
        // ways of addressing the fleet, which are unchanged and still true.
        Assert.Contains("command", script, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tell", script, System.StringComparison.OrdinalIgnoreCase);

        // And it must teach NO sign-off phrase. Asserted as an ABSENCE because the presence was the defect: the
        // Assistant has a Send action, so a script telling somebody to say a phrase when they are done teaches a
        // command that ends nothing. There is no leftover format slot for one either - a script with no slot
        // cannot go stale against a setting, which this one had already done once before by hardcoding "over and
        // out" while the phrase was configurable.
        Assert.DoesNotContain("over and out", script, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{0}", script);

        // Spoken prose, read aloud: no markdown list markers or headings.
        Assert.DoesNotContain("\n-", script);
        Assert.DoesNotContain("#", script);
    }
}
