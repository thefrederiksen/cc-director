using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Architecture;

/// <summary>
/// THE GENERATE BUTTON IS NEVER GATED BY THE AUTOMATIC RETRY SCHEDULE.
///
/// The schedule (<see cref="VoiceRetryPolicy"/>) exists so the Gateway stops trying by itself after a few
/// attempts and offers the person a button instead. The button is therefore the ONE path that must work
/// precisely when the schedule says stop - it is the escape from the schedule, not a participant in it. If
/// the route behind it ever consulted the policy, the screen would offer a button that answered nothing for
/// ten minutes, and the sentence beside it ("the Gateway has stopped; you can ask for it yourself") would be
/// a lie in the other direction.
///
/// A reviewer read the two together and concluded exactly that had happened - reasonably, because nothing
/// said otherwise except the absence of a call in a nine-hundred-line file. This turns that absence into
/// something a run can check.
///
/// AN EXACT ALLOWLIST, NOT A NAMESPACE FILTER. The first version of this asked whether anything under the
/// endpoints namespace consulted the schedule, and a second review pointed out the obvious way past it: an
/// endpoint calling a NEW wrapper that consults the schedule on its behalf puts the offending call in the
/// wrapper's namespace, and the guard sees nothing. Listing the callers the schedule is allowed to have
/// inverts that - any new caller anywhere, wrapper included, turns this red and has to be looked at. It is
/// the same shape as the SQLite allowlist next door, and for the same reason.
///
/// WHAT IT STILL DOES NOT COVER, said plainly: it reads call instructions naming these members. Gating
/// logic duplicated inline (comparing an attempt count by hand rather than asking the policy), a member
/// renamed on both sides at once, or a call made by reflection would all pass. It catches the way this
/// plausibly regresses, not everything conceivable.
/// </summary>
public sealed class ManualNarrationIsNotGatedArchitectureTests
{
    private const string PolicyIsDue = "CcDirector.Gateway.Wingman.VoiceRetryPolicy::IsDue";
    private const string ServiceIsDue = "CcDirector.Gateway.Wingman.WingmanVoiceService::IsDueForAutomaticRetry";

    /// <summary>
    /// Everything the Gateway is allowed to ask "may an automatic narration run now?". Two callers, both of
    /// them the automatic path itself: the sweep asks before spending a slot, and the generation asks again
    /// once it knows which turn it is holding. Nothing a person presses is on this list, and nothing that
    /// serves a person's press may be added to it without deciding, deliberately, that the button should
    /// stop working at the moment the screen starts advertising it.
    /// </summary>
    private static readonly HashSet<string> MayAskTheSchedule = new(StringComparer.Ordinal)
    {
        "CcDirector.Gateway.Wingman.WingmanVoiceService::IsDueForAutomaticRetry",
        "CcDirector.Gateway.Wingman.WingmanVoiceService::GenerateOnceAsync",
        "CcDirector.Gateway.GatewayHost::SweepVoiceSessionsAsync",
    };

    [Fact]
    public void OnlyTheAutomaticPathAsksTheScheduleWhetherItMayRun()
    {
        var askers = Callers(PolicyIsDue).Concat(Callers(ServiceIsDue))
            .Distinct()
            .Where(m => !MayAskTheSchedule.Contains(m))
            .ToList();

        Assert.True(askers.Count == 0,
            "Something outside the automatic narration path consults the retry schedule. The Generate " +
            "button exists to be pressed at the moment that schedule has given up, so anything that serves " +
            "a person's press must not ask it - gating the button on the schedule makes it do nothing for " +
            "as long as the screen is telling the person to press it. If this really is part of the " +
            "automatic path, add it to MayAskTheSchedule with the reason. These ask:" + Environment.NewLine +
            "  " + string.Join(Environment.NewLine + "  ", askers));
    }

    [Fact]
    public void EveryNameOnTheAllowlistStillAsks()
    {
        // The allowlist cannot rot, and the detector cannot quietly become a scan for nothing. An entry
        // whose method no longer asks is a permission nobody is checking; a member renamed on the policy
        // side would empty every result and turn the guard above into a test that passes by finding
        // nothing at all.
        var asking = Callers(PolicyIsDue).Concat(Callers(ServiceIsDue))
            .ToHashSet(StringComparer.Ordinal);

        var stale = MayAskTheSchedule.Where(m => !asking.Contains(m)).ToList();

        Assert.True(stale.Count == 0,
            "These names are allowed to consult the retry schedule and no longer do. Either the automatic " +
            "path has been rewritten (delete them), or the members have been renamed and the guard above is " +
            "now scanning for something that does not exist:" + Environment.NewLine +
            "  " + string.Join(Environment.NewLine + "  ", stale));
    }

    private static List<string> Callers(string member)
        => CompiledCalls.Of(member, "CcDirector.Gateway.dll", typeof(VoiceRetryPolicy));
}
