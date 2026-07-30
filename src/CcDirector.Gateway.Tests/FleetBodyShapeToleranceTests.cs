using CcDirector.ControlApi;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1051. The Director now asks the Gateway for the reachability envelope so it can tell its command
/// line callers whether the roster is the whole fleet. An OLDER Gateway ignores the unknown envelope query
/// parameter and answers with the plain session array it always did - the hosted Gateway is deployed
/// separately from the desktop app, so a newer Director meeting an older Gateway is an ordinary state, not
/// an exotic one.
///
/// That is a real failure mode and it was nearly shipped as one: the first cut of this change reused
/// <c>ListFleetSessionsWithReachabilityAsync</c>, which FAILS CLOSED and throws when the Gateway cannot
/// vouch for completeness. That posture is correct for its own caller, the worktree reaper, which deletes
/// directories - and catastrophic here, where it would have turned `session list`, every target resolve,
/// cc-status and cc-history into a 502 against a version-skewed Gateway. Two callers, two postures, and
/// these tests pin the reading one.
///
/// Null reachability means UNKNOWN, and the point of the whole issue is that unknown must never be read as
/// complete: absent is not empty.
/// </summary>
public sealed class FleetBodyShapeToleranceTests
{
    [Fact]
    public void TheEnvelopeShape_yieldsRowsAndReachability()
    {
        const string body = """
        {
          "sessions": [ { "sessionId": "aaaa", "name": "one" } ],
          "directors": [ { "directorId": "d2", "machineName": "MACHINE_B", "state": "offline" } ]
        }
        """;

        var (sessions, reachability) = GatewayClient.ParseFleetBodyForDisplay(body);

        Assert.Single(sessions);
        Assert.Equal("aaaa", sessions[0].SessionId);
        Assert.NotNull(reachability);
        Assert.Single(reachability!);
        Assert.Equal("MACHINE_B", reachability![0].MachineName);
        Assert.Equal("offline", reachability[0].State);
    }

    [Fact]
    public void ThePlainArrayShape_fromAnOlderGateway_yieldsRowsAndUNKNOWNreachability()
    {
        // The compatibility case. The rows must still be served - refusing them would break every verb -
        // and reachability must come back NULL so the caller reports completeness as unknown rather than
        // claiming a completeness nobody vouched for.
        const string body = """[ { "sessionId": "aaaa", "name": "one" }, { "sessionId": "bbbb" } ]""";

        var (sessions, reachability) = GatewayClient.ParseFleetBodyForDisplay(body);

        Assert.Equal(2, sessions.Count);
        Assert.Null(reachability);
    }

    [Fact]
    public void AnEnvelopeWithoutReachability_isUNKNOWN_notComplete()
    {
        // A version-skewed Gateway can answer the envelope shape while omitting the reachability array.
        // Absent is not empty: an empty array would be an authoritative "no Directors are unreachable",
        // and this is "it did not say".
        const string body = """{ "sessions": [ { "sessionId": "aaaa" } ] }""";

        var (sessions, reachability) = GatewayClient.ParseFleetBodyForDisplay(body);

        Assert.Single(sessions);
        Assert.Null(reachability);
    }

    [Fact]
    public void AnEmptyReachabilityArray_isPreserved_asAnAuthoritativeNothingUnreachable()
    {
        // The other half of the distinction above, and the reason it cannot be collapsed: present-but-empty
        // is a positive statement that the whole fleet was reachable.
        const string body = """{ "sessions": [], "directors": [] }""";

        var (sessions, reachability) = GatewayClient.ParseFleetBodyForDisplay(body);

        Assert.Empty(sessions);
        Assert.NotNull(reachability);
        Assert.Empty(reachability!);
    }

    [Fact]
    public void AnEnvelopeWithNoSessionList_throws_becauseThatIsMalformedNotDegraded()
    {
        Assert.Throws<InvalidOperationException>(
            () => GatewayClient.ParseFleetBodyForDisplay("""{ "directors": [] }"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyBody_throws_ratherThanReadingAsAnEmptyFleet(string? body)
    {
        // An empty body is a broken answer. Treating it as "no sessions" would be the same class of error
        // this whole issue is about - reporting absence as emptiness.
        Assert.Throws<InvalidOperationException>(() => GatewayClient.ParseFleetBodyForDisplay(body));
    }
}
