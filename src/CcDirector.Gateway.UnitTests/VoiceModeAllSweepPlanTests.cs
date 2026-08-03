using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The decision half of the voice-mode sweep. Voice mode is a STANDING intent - a tenant that turned it on
/// wants every one of its sessions narrating, including the ones that did not exist when the switch was
/// thrown. <see cref="VoiceModeAllSweep.Plan"/> answers which sessions still need switching on.
///
/// Both directions of the guard matter and both are proved here: a tenant that is NOT in voice mode must
/// yield nothing at all (the sweep must never put a fleet on voice that did not ask), and a tenant that IS in
/// voice mode must yield exactly the sessions still off (so a steady fleet produces no traffic at all).
/// </summary>
public sealed class VoiceModeAllSweepPlanTests
{
    private static (string, SessionDto) Row(string directorId, string sid) =>
        (directorId, new SessionDto { SessionId = sid, Name = sid, Status = "WaitingForInput" });

    private static Func<string, bool> On(params string[] sids)
    {
        var set = new HashSet<string>(sids, StringComparer.Ordinal);
        return set.Contains;
    }

    [Fact]
    public void Plan_whenVoiceModeIsOff_yieldsNothing_evenWithSessionsThatAreNotOnVoice()
    {
        var roster = new[] { Row("d1", "a"), Row("d1", "b") };
        var plan = VoiceModeAllSweep.Plan(voiceModeOn: false, roster, On());
        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_whenVoiceModeIsOn_namesOnlyTheSessionsNotYetOnVoice()
    {
        var roster = new[] { Row("d1", "already"), Row("d2", "needsIt") };
        var plan = VoiceModeAllSweep.Plan(voiceModeOn: true, roster, On("already"));
        Assert.Equal(new[] { ("d2", "needsIt") }, plan);
    }

    [Fact]
    public void Plan_whenEverySessionIsAlreadyOnVoice_yieldsNothing_soASteadyFleetIsSilent()
    {
        var roster = new[] { Row("d1", "a"), Row("d1", "b") };
        var plan = VoiceModeAllSweep.Plan(voiceModeOn: true, roster, On("a", "b"));
        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_carriesTheOwningDirector_soTheSweepKnowsWhereToSendTheCommand()
    {
        var roster = new[] { Row("machine-a", "one"), Row("machine-b", "two") };
        var plan = VoiceModeAllSweep.Plan(voiceModeOn: true, roster, On());
        Assert.Equal(new[] { ("machine-a", "one"), ("machine-b", "two") }, plan);
    }

    [Fact]
    public void Plan_switchesADuplicatedRosterEntryOnce_neverTwice()
    {
        var roster = new[] { Row("d1", "same"), Row("d1", "same") };
        var plan = VoiceModeAllSweep.Plan(voiceModeOn: true, roster, On());
        Assert.Single(plan);
    }

    [Fact]
    public void Plan_ignoresRowsWithNoSessionIdOrNoDirector_thereIsNothingToSwitchOn()
    {
        var roster = new[]
        {
            ("d1", new SessionDto { SessionId = "", Name = "blank" }),
            ("", new SessionDto { SessionId = "orphan", Name = "no director" }),
            Row("d1", "real"),
        };
        var plan = VoiceModeAllSweep.Plan(voiceModeOn: true, roster, On());
        Assert.Equal(new[] { ("d1", "real") }, plan);
    }
}
