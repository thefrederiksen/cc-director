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
/// says otherwise except the absence of a call in a nine-hundred-line file. This turns that absence into
/// something a run can check.
///
/// WHAT IT DOES NOT COVER: it scans for calls to two named members. A future route that reached the schedule
/// through some new wrapper would not be seen. It catches the way this could plausibly regress - somebody
/// "tidying up" by routing the button through the automatic generation path - not every conceivable way.
/// </summary>
public sealed class ManualNarrationIsNotGatedArchitectureTests
{
    private const string PolicyIsDue = "CcDirector.Gateway.Wingman.VoiceRetryPolicy::IsDue";
    private const string ServiceIsDue = "CcDirector.Gateway.Wingman.WingmanVoiceService::IsDueForAutomaticRetry";

    [Fact]
    public void TheVoiceEndpoints_neverAskTheAutomaticScheduleWhetherTheyMayRun()
    {
        var askers = Callers(PolicyIsDue).Concat(Callers(ServiceIsDue))
            .Where(m => m.StartsWith("CcDirector.Gateway.Api.", StringComparison.Ordinal))
            .ToList();

        Assert.True(askers.Count == 0,
            "A voice endpoint consults the automatic retry schedule. The Generate button exists to be pressed " +
            "at the moment that schedule has given up, so gating it on the schedule makes it do nothing for " +
            "as long as the screen is telling the person to press it. Keep the button's route on its own " +
            "synthesis path. These endpoint methods ask:" + Environment.NewLine +
            "  " + string.Join(Environment.NewLine + "  ", askers));
    }

    [Fact]
    public void TheDetectorFindsTheCallsItLooksFor()
    {
        // The absence above means nothing unless the names still resolve to something. Both members ARE
        // called inside the Gateway - the policy by the service, the service by the host's sweep - so a
        // rename turns this red instead of quietly turning the guard into a scan for nothing.
        Assert.True(Callers(PolicyIsDue).Count > 0,
            $"No call to {PolicyIsDue} anywhere in the Gateway. The member has been renamed, so the guard " +
            "above is passing by looking for something that no longer exists.");
        Assert.True(Callers(ServiceIsDue).Count > 0,
            $"No call to {ServiceIsDue} anywhere in the Gateway. The member has been renamed, so the guard " +
            "above is passing by looking for something that no longer exists.");
    }

    private static List<string> Callers(string member)
        => CompiledCalls.Of(member, "CcDirector.Gateway.dll", typeof(VoiceRetryPolicy));
}
