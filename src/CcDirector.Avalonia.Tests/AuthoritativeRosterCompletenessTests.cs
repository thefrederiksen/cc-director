using CcDirector.Avalonia;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The authoritative reap roster fails closed when the Gateway's fleet view is INCOMPLETE for this
/// machine (inspection): a Director on this machine that is not fully Online (Wobbly or Offline) may
/// have a live session the roster dropped, so a reap must not proceed. Only this machine's Directors
/// matter - a worktree is a local folder.
/// </summary>
public class AuthoritativeRosterCompletenessTests
{
    private static DirectorReachabilityDto R(string id, string machine, string state)
        => new() { DirectorId = id, MachineName = machine, State = state };

    [Fact]
    public void OfflineOrWobbly_SameMachineDirectors_AreReportedDegraded()
    {
        var reachability = new[]
        {
            R("d1", "M1", DirectorReachabilityDto.StateOnline),
            R("d2", "M1", DirectorReachabilityDto.StateOffline),
            R("d3", "M1", DirectorReachabilityDto.StateWobbly),
            R("d4", "OTHER", DirectorReachabilityDto.StateOffline), // another machine - irrelevant to our worktrees
        };

        var degraded = MainWindow.DegradedSameMachineDirectors(reachability, "M1");

        Assert.Equal(2, degraded.Count);
        Assert.Contains(degraded, x => x.Contains("d2"));
        Assert.Contains(degraded, x => x.Contains("d3"));
        Assert.DoesNotContain(degraded, x => x.Contains("d4")); // other machine never blocks this machine's reap
    }

    [Fact]
    public void AllOnline_OnThisMachine_IsNotDegraded()
    {
        var reachability = new[]
        {
            R("d1", "M1", DirectorReachabilityDto.StateOnline),
            R("d2", "OTHER", DirectorReachabilityDto.StateOffline),
        };

        Assert.Empty(MainWindow.DegradedSameMachineDirectors(reachability, "M1"));
    }

    [Fact]
    public void MachineNameMatchIsCaseInsensitive()
    {
        var reachability = new[] { R("d1", "m1", DirectorReachabilityDto.StateOffline) };
        Assert.Single(MainWindow.DegradedSameMachineDirectors(reachability, "M1"));
    }
}
