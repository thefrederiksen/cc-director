using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The fold that decides what a Director's reachability MEANS - its badge, its available action, and the
/// fleet-wide warning line - pinned here rather than in Gateway.Tests because it is a pure fold with no
/// host, so it does not belong behind that suite's machine-wide serialisation lock.
///
/// THE DEFECT THESE EXIST FOR, stated so a later reader cannot mistake what is being pinned. The Fleet Map
/// counted the rows of the envelope's <c>machineErrors</c> list and printed each one with the word
/// "machine". That list is PER DIRECTOR. On a machine running three Directors and fifteen live sessions,
/// one shut-down slot therefore produced "1 machine unreachable on the last sweep: SOREN_NORTH" while every
/// other Director on it was pushing every few seconds - a healthy machine reported as dead, from a count of
/// the wrong noun. Two separate rules fix it and both are pinned below: a machine is unreachable only when
/// EVERY Director on it is, and a Director that announced its own shutdown is not a failure at all.
/// </summary>
public class FleetReachabilityFoldTests
{
    private static DirectorReachabilityDto Dir(string id, string machine, string state, double? ageSeconds = 60,
        string display = "") =>
        new()
        {
            DirectorId = id,
            MachineName = machine,
            DisplayName = display,
            State = state,
            LastSeenAgeSeconds = ageSeconds,
            LastSeenUtc = ageSeconds is null ? null : DateTime.UtcNow.AddSeconds(-(ageSeconds ?? 0)),
        };

    // ===== The banner: which noun, and how many =====

    [Fact]
    public void UnreachableBanner_healthy_fleet_says_nothing()
    {
        var banner = FleetReachabilityFold.UnreachableBanner(new List<DirectorReachabilityDto>
        {
            Dir("a", "SOREN_NORTH", DirectorReachabilityDto.StateOnline),
            Dir("b", "SOREN", DirectorReachabilityDto.StateOnline),
        });
        Assert.Null(banner);
    }

    /// <summary>
    /// THE REGRESSION. One offline slot on a machine whose other Directors are answering is a DIRECTOR-level
    /// fact. The old view printed "1 machine unreachable on the last sweep: SOREN_NORTH" for exactly this
    /// input, so the assertion that the word "machine" is absent is the one that fails on the old behaviour.
    /// </summary>
    [Fact]
    public void UnreachableBanner_one_dead_slot_on_a_live_machine_names_the_director_not_the_machine()
    {
        var banner = FleetReachabilityFold.UnreachableBanner(new List<DirectorReachabilityDto>
        {
            Dir("dir-slot5", "SOREN_NORTH", DirectorReachabilityDto.StateOffline, 1896, display: "Slot 5"),
            Dir("dir-s1", "SOREN_NORTH", DirectorReachabilityDto.StateOnline, 3),
            Dir("dir-s2", "SOREN_NORTH", DirectorReachabilityDto.StateOnline, 1),
        });

        Assert.NotNull(banner);
        Assert.Contains("1 director could not be reached", banner);
        Assert.Contains("Slot 5 on SOREN_NORTH", banner);
        Assert.Contains("last seen 31m ago", banner);  // truncated, exactly as the lane beside it prints it
        // The correction the owner actually needs: the machine is fine.
        Assert.Contains("the rest of that machine is answering normally", banner);
        // The whole defect in one assertion - the machine must NOT be called unreachable.
        Assert.DoesNotContain("machine could not be reached", banner);
        Assert.DoesNotContain("machines could not be reached", banner);
    }

    [Fact]
    public void UnreachableBanner_every_director_on_a_machine_offline_names_the_machine()
    {
        var banner = FleetReachabilityFold.UnreachableBanner(new List<DirectorReachabilityDto>
        {
            Dir("dir-a", "SOREN_NORTH", DirectorReachabilityDto.StateOffline, 300),
            Dir("dir-b", "SOREN_NORTH", DirectorReachabilityDto.StateOffline, 400),
            Dir("dir-c", "SOREN", DirectorReachabilityDto.StateOnline, 2),
        });

        Assert.NotNull(banner);
        // ONE entry for the machine, not one per Director it lost: the count and the noun have to agree.
        Assert.Contains("1 machine could not be reached", banner);
        Assert.Contains("SOREN_NORTH", banner);
        Assert.DoesNotContain("2 machines", banner);
        Assert.DoesNotContain("director could not be reached", banner);
        // The MACHINE was last heard when the LAST of its Directors was - 300 seconds, not the 400 of the
        // other one. Quoting the older Director would overstate how long the machine has been gone.
        Assert.Contains("last seen 5m ago", banner);
        Assert.DoesNotContain("6m ago", banner);
    }

    [Fact]
    public void UnreachableBanner_reports_a_dead_machine_and_a_dead_slot_together()
    {
        var banner = FleetReachabilityFold.UnreachableBanner(new List<DirectorReachabilityDto>
        {
            Dir("dir-a", "MAC_MINI", DirectorReachabilityDto.StateOffline, 900),
            Dir("dir-b", "SOREN_NORTH", DirectorReachabilityDto.StateOffline, 120, display: "Slot 5"),
            Dir("dir-c", "SOREN_NORTH", DirectorReachabilityDto.StateOnline, 2),
        });

        Assert.NotNull(banner);
        Assert.Contains("1 machine could not be reached", banner);
        Assert.Contains("MAC_MINI", banner);
        Assert.Contains("1 director could not be reached", banner);
        Assert.Contains("Slot 5 on SOREN_NORTH", banner);
    }

    /// <summary>
    /// A Director that said goodbye is not a fault, and this is the assertion that keeps the whole day-long
    /// false warning from coming back: a stopped Director on an otherwise-silent machine produces NO banner.
    /// </summary>
    [Fact]
    public void UnreachableBanner_ignores_a_director_that_was_shut_down()
    {
        var banner = FleetReachabilityFold.UnreachableBanner(new List<DirectorReachabilityDto>
        {
            Dir("dir-slot5", "SOREN_NORTH", DirectorReachabilityDto.StateStopped, 1896, display: "Slot 5"),
            Dir("dir-s1", "SOREN_NORTH", DirectorReachabilityDto.StateOnline, 3),
        });
        Assert.Null(banner);

        // ...and even when it is the ONLY Director on that machine. Nobody is there, nobody should be.
        Assert.Null(FleetReachabilityFold.UnreachableBanner(new List<DirectorReachabilityDto>
        {
            Dir("dir-solo", "OLD_LAPTOP", DirectorReachabilityDto.StateStopped, 4000),
        }));
    }

    [Fact]
    public void UnreachableBanner_ignores_a_wobbly_director()
    {
        Assert.Null(FleetReachabilityFold.UnreachableBanner(new List<DirectorReachabilityDto>
        {
            Dir("dir-a", "SOREN", DirectorReachabilityDto.StateWobbly, 45),
        }));
    }

    [Fact]
    public void UnreachableBanner_never_invents_an_age_it_does_not_have()
    {
        var banner = FleetReachabilityFold.UnreachableBanner(new List<DirectorReachabilityDto>
        {
            Dir("dir-a", "NEW_BOX", DirectorReachabilityDto.StateOffline, ageSeconds: null),
        });
        Assert.NotNull(banner);
        Assert.Contains("never reached", banner);
        Assert.DoesNotContain("last seen", banner);
    }

    /// <summary>
    /// A Director with no machine name cannot be grouped with confidence, so it can never be promoted to a
    /// machine verdict - it is reported as the single Director it is.
    /// </summary>
    [Fact]
    public void UnreachableBanner_an_unnamed_machine_is_never_called_a_dead_machine()
    {
        var banner = FleetReachabilityFold.UnreachableBanner(new List<DirectorReachabilityDto>
        {
            Dir("dir-abcdef12", "", DirectorReachabilityDto.StateOffline, 60),
        });
        Assert.NotNull(banner);
        Assert.Contains("1 director could not be reached", banner);
        Assert.DoesNotContain("machine could not be reached", banner);
    }

    [Fact]
    public void UnreachableBanner_empty_or_null_says_nothing()
    {
        Assert.Null(FleetReachabilityFold.UnreachableBanner(null));
        Assert.Null(FleetReachabilityFold.UnreachableBanner(new List<DirectorReachabilityDto>()));
    }

    // ===== The per-Director presentation the client renders verbatim =====

    [Fact]
    public void Describe_online_wears_no_badge_and_offers_the_action()
    {
        var d = Dir("a", "SOREN", DirectorReachabilityDto.StateOnline, 2);
        FleetReachabilityFold.Describe(d);
        Assert.Equal("", d.StateLabel);
        Assert.False(d.DataIsStale);
        Assert.True(d.CanStartSession);
        Assert.Equal("No sessions - free slot", d.EmptySlotText);
    }

    /// <summary>A wobbly Director's tunnel is UP - only its last push is late - so a start still lands.</summary>
    [Fact]
    public void Describe_wobbly_is_stale_but_still_startable()
    {
        var d = Dir("a", "SOREN", DirectorReachabilityDto.StateWobbly, 45);
        FleetReachabilityFold.Describe(d);
        Assert.Equal("Wobbly", d.StateLabel);
        Assert.True(d.DataIsStale);
        Assert.True(d.CanStartSession);
        Assert.Equal("No sessions - free slot", d.EmptySlotText);
    }

    [Fact]
    public void Describe_offline_withholds_the_action_and_blames_the_director_not_the_machine()
    {
        var d = Dir("a", "SOREN", DirectorReachabilityDto.StateOffline, 300);
        FleetReachabilityFold.Describe(d);
        Assert.Equal("Offline", d.StateLabel);
        Assert.True(d.DataIsStale);
        Assert.False(d.CanStartSession);
        // The placeholder said "machine unreachable" under a DIRECTOR sub-header. It is a director.
        Assert.Equal("No sessions - this director cannot be reached", d.EmptySlotText);
        Assert.DoesNotContain("machine", d.EmptySlotText);
    }

    /// <summary>
    /// Stopped is ordinary, but it is still not free capacity: the tunnel a start would travel down went with
    /// the process, so offering the action would be offering something that cannot be honoured.
    /// </summary>
    [Fact]
    public void Describe_stopped_reads_not_running_and_is_not_offered_as_free_capacity()
    {
        var d = Dir("a", "SOREN_NORTH", DirectorReachabilityDto.StateStopped, 1896);
        FleetReachabilityFold.Describe(d);
        Assert.Equal("Not running", d.StateLabel);
        Assert.True(d.DataIsStale);
        Assert.False(d.CanStartSession);
        Assert.Equal("No sessions - this director is not running", d.EmptySlotText);
        Assert.DoesNotContain("free slot", d.EmptySlotText);
    }

    /// <summary>
    /// An unrecognised state degrades to the healthy render rather than to a guess - the client never has to
    /// invent a word for a state a newer Gateway invented.
    /// </summary>
    [Fact]
    public void Describe_an_unknown_state_degrades_to_no_badge()
    {
        var d = Dir("a", "SOREN", "something-a-later-gateway-invented", 5);
        FleetReachabilityFold.Describe(d);
        Assert.Equal("", d.StateLabel);
        Assert.False(d.DataIsStale);
        Assert.Equal("No sessions - free slot", d.EmptySlotText);
    }

    /// <summary>
    /// A stopped Director must not make the roster read INCOMPLETE either: RosterCompleteness counts offline
    /// only, and the two folds have to agree about what a fault is or the command line tools and the map will
    /// say different things about the same fleet.
    /// </summary>
    [Fact]
    public void A_stopped_director_does_not_make_the_roster_incomplete()
    {
        var (complete, reason) = RosterCompleteness.Fold(new List<DirectorReachabilityDto>
        {
            Dir("dir-slot5", "SOREN_NORTH", DirectorReachabilityDto.StateStopped, 1896),
            Dir("dir-s1", "SOREN_NORTH", DirectorReachabilityDto.StateOnline, 3),
        });
        Assert.True(complete);
        Assert.Null(reason);
    }
}
