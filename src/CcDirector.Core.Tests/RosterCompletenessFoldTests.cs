using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Issue #1051 - the other end of #1019's defect. The Gateway drops an unreachable Director's sessions from
/// the roster and STILL ANSWERS 200, so a caller cannot tell "that Director has no sessions" from "I could
/// not reach that Director": absent reads identical to empty. Since the roster is what every command line
/// verb resolves a target against, a session on an unreachable machine is unnameable - the same failure
/// #1019 fixed, one machine over.
///
/// These pin the fold that turns reachability into a verdict a tool prints verbatim. It lives in
/// Gateway.Contracts beside SessionOrdering, not in each tool, because deciding what a reachability state
/// MEANS is ruling rather than rendering, and three tools each deciding for themselves would drift.
/// Testing it from here rather than Gateway.Tests is deliberate: it is a pure fold with no host, so it
/// does not belong behind that suite's machine-wide serialisation lock.
///
/// The distinction that carries the whole design: OFFLINE drops a Director's sessions, so rows really are
/// missing. WOBBLY does not - it is inside the grace window and its last-known-good sessions are still
/// served, so the roster is whole and merely part-stale. Warning on wobbly would put a caveat on the most
/// frequently run command in the tool for a case where nothing is missing, which trains the reader to
/// ignore it - and then it is not there when offline finally happens.
/// </summary>
public sealed class RosterCompletenessFoldTests
{
    private static DirectorReachabilityDto R(
        string state, string machine = "MACHINE_B", string id = "d2",
        double? ageSeconds = null, string? error = null, DateTime? lastSeen = null)
        => new()
        {
            DirectorId = id,
            MachineName = machine,
            State = state,
            LastSeenAgeSeconds = ageSeconds,
            LastSeenUtc = lastSeen,
            Error = error,
        };

    [Fact]
    public void AllOnline_isComplete_withNothingToSay()
    {
        var (complete, reason) = RosterCompleteness.Fold(new[]
        {
            R(DirectorReachabilityDto.StateOnline, "MACHINE_A", "d1"),
            R(DirectorReachabilityDto.StateOnline, "MACHINE_B", "d2"),
        });

        Assert.True(complete);
        Assert.Null(reason);
    }

    [Fact]
    public void AnOfflineDirector_makesTheRosterIncomplete_andIsNamed()
    {
        // The reported shape. The caller must be able to say WHICH machine it could not see, because
        // "give up, the session is gone" and "go and look at MACHINE_B" are opposite next steps.
        var (complete, reason) = RosterCompleteness.Fold(new[]
        {
            R(DirectorReachabilityDto.StateOnline, "MACHINE_A", "d1"),
            R(DirectorReachabilityDto.StateOffline, "MACHINE_B", "d2", ageSeconds: 240, error: "director not connected to the tunnel"),
        });

        Assert.False(complete);
        Assert.NotNull(reason);
        Assert.Contains("MACHINE_B", reason);
        // Inspection 1, finding 4: this used to demand the words "missing from this list", which pinned a
        // sentence that had stopped being true - step A serves those rows. The caution that survives is about
        // AUTHORITY, so that is what is asserted, and the old words are asserted ABSENT so the false claim
        // cannot quietly come back.
        Assert.Contains("may be out of date", reason);
        Assert.DoesNotContain("missing from this list", reason);
        Assert.Contains("director not connected to the tunnel", reason);
        Assert.Contains("4m", reason);                        // the age, so "how stale" is answerable
        Assert.DoesNotContain("MACHINE_A", reason);           // a reachable Director is never blamed
    }

    [Fact]
    public void AWobblyDirector_isNOTreportedIncomplete_becauseItsSessionsAreStillServed()
    {
        // The load-bearing distinction. A wobbly Director's machine is still reachable, so its rows carry
        // the Gateway's usual confidence and there is nothing to warn about. If this ever flips to
        // incomplete, the session list grows a permanent caveat on a healthy fleet and the warning stops
        // meaning anything.
        var (complete, reason) = RosterCompleteness.Fold(new[]
        {
            R(DirectorReachabilityDto.StateWobbly, "MACHINE_B", "d2", ageSeconds: 12, error: "one poll missed"),
        });

        Assert.True(complete);
        Assert.Null(reason);
    }

    [Fact]
    public void SeveralOfflineDirectors_areAllNamed_andCountedPlurally()
    {
        var (complete, reason) = RosterCompleteness.Fold(new[]
        {
            R(DirectorReachabilityDto.StateOffline, "MACHINE_B", "d2"),
            R(DirectorReachabilityDto.StateOffline, "MACHINE_C", "d3"),
        });

        Assert.False(complete);
        Assert.Contains("2 Directors", reason);
        Assert.Contains("MACHINE_B", reason);
        Assert.Contains("MACHINE_C", reason);
    }

    [Fact]
    public void OneOfflineDirector_readsSingular()
    {
        var (_, reason) = RosterCompleteness.Fold(new[] { R(DirectorReachabilityDto.StateOffline) });
        Assert.Contains("1 Director could not be reached", reason);
    }

    [Fact]
    public void ADirectorNeverReached_saysSo_ratherThanClaimingAnAge()
    {
        // No last-seen at all is different from "last seen a while ago", and inventing an age would be
        // a fabricated number. Say "never reached".
        var (complete, reason) = RosterCompleteness.Fold(new[]
        {
            R(DirectorReachabilityDto.StateOffline, ageSeconds: null, lastSeen: null),
        });

        Assert.False(complete);
        Assert.Contains("never reached", reason);
    }

    [Fact]
    public void AnOfflineDirectorWithNoMachineName_fallsBackToItsId_soItIsStillIdentifiable()
    {
        var (_, reason) = RosterCompleteness.Fold(new[]
        {
            R(DirectorReachabilityDto.StateOffline, machine: "", id: "director-7"),
        });

        Assert.Contains("director-7", reason);
    }

    [Fact]
    public void NoReachabilityReported_isTreatedAsComplete()
    {
        // The standalone floor: no Gateway, so no other Director can be hiding anything. An empty list
        // here must NOT read as "might be incomplete", or a single-machine setup warns forever.
        Assert.True(RosterCompleteness.Fold(Array.Empty<DirectorReachabilityDto>()).Complete);
        Assert.True(RosterCompleteness.Fold(null).Complete);
    }

    [Fact]
    public void StateMatching_isCaseInsensitive()
    {
        var (complete, _) = RosterCompleteness.Fold(new[] { R("OFFLINE") });
        Assert.False(complete);
    }

    // The paired values that pin this against the CLIENT'S formatter (reachabilityLastSeen in client-core),
    // which now prints an age for the SAME Director one line away from a sentence written here. Every case
    // below is one where the two used to disagree, or a boundary where they could drift apart again: 89
    // seconds read "89s" here and "1m" there; 1896 seconds read "32m" here (rounded) and "31m" there
    // (truncated); 4000 seconds read "67m" here and "1h" there. The rule is now the client's, in both.
    [Theory]
    [InlineData(30, "30s")]
    [InlineData(59, "59s")]
    [InlineData(89, "1m")]
    [InlineData(240, "4m")]
    [InlineData(1896, "31m")]
    [InlineData(3599, "59m")]
    [InlineData(4000, "1h")]
    [InlineData(7200, "2h")]
    public void Age_readsInTheCoarsestUsefulUnit(double seconds, string expected)
        => Assert.Equal(expected, RosterCompleteness.DescribeAge(seconds));
}
