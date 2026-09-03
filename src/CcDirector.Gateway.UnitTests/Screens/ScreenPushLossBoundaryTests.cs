using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Screens;

/// <summary>
/// Inspection 01, finding 5: a screen that cannot be pushed is lost PERMANENTLY, and that loss used to be
/// completely silent.
///
/// WHAT THE FINDING ESTABLISHED. There is no outbox, no sequence and no reconnect replay for screens. If
/// the tunnel is absent or not connected, <see cref="GatewayStreamClient.PushScreen"/> returned without
/// retaining the capture; if the send failed, it was logged as a generic dropped invoke and discarded. The
/// next turn sends the NEXT turn's screen, so the missed turn has no row in the Gateway's history and
/// never will - and the feature's own report said a miss cost "a round trip, never a record".
///
/// WHAT THIS ROUND CHOSE. The hole is named rather than closed: a durable outbox is a mechanism that would
/// owe its own proofs, and ruling 12 allows either durability or an honest boundary. But an honest
/// boundary that is invisible at runtime is only half an answer, so the drop is now a NAMED, COUNTED,
/// LOGGED event. Before this, a Director dropping every screen it captured looked exactly like a Director
/// that had captured none: the drop path returned in silence and wrote nothing at all - which is what the
/// red run for this finding recorded, an empty collection of log lines.
///
/// THE ASSERTIONS ARE PRESENCES. The log line must exist and must name the session, the capture time and
/// the reason; the dropped counter must move by exactly one per drop; and the DELIVERED counter must not
/// move, because "nothing was dropped" is satisfied by a Director that never pushed anything and the pair
/// is what carries meaning. The delivered side is exercised for real against a live hub by
/// <c>GatewayStreamClientTests</c> in the parked suite - this class deliberately covers only the loss
/// path, which is the one that had no coverage at all.
///
/// THE COUNTERS ARE PROCESS-WIDE, so a before-and-after difference is only meaningful while nothing else
/// in the process is pushing a screen. No collection attribute is needed for that here, and the reason is
/// worth stating rather than leaving to be re-derived: xUnit runs the methods of ONE class sequentially,
/// and this is the only class in this assembly that touches these two counters - unlike
/// <see cref="SessionVerbClient.ScreenGridPulls"/>, which several classes read and which therefore has
/// <see cref="ScreenPullCounterCollection"/>. A second class that touches these belongs in a collection
/// with this one.
/// </summary>
public class ScreenPushLossBoundaryTests
{
    private static GatewayStreamClient UnconnectedClient() =>
        new(new GatewayConfig { Url = "http://127.0.0.1:59999", Token = "t", StreamMode = true },
            "dir-loss-boundary", "test", () => new List<SessionDto>());

    private static ScreenPush Push(string sessionId) => new()
    {
        SessionId = sessionId,
        CapturedAtUtc = new DateTime(2026, 9, 2, 16, 0, 0, DateTimeKind.Utc),
        Rows = new List<string> { "a turn ended here" },
        HasGrid = true,
        BufferBytes = 128,
        ActivityState = "WaitingForInput",
        Agent = "ClaudeCode",
    };

    [Fact]
    public void A_screen_that_cannot_be_sent_is_logged_and_counted_as_a_permanent_loss()
    {
        var client = UnconnectedClient();
        var push = Push("55555555-5555-5555-5555-555555555555");

        var droppedBefore = GatewayStreamClient.ScreenPushesDropped;
        var deliveredBefore = GatewayStreamClient.ScreenPushesDelivered;

        using var scope = FileLog.RedirectForTests();
        client.PushScreen(push);
        var lines = scope.DrainAndReadLines();

        // THE LOG. Named facts, not a generic failure: which session, when it was captured, and why it went.
        var drop = Assert.Single(lines, l => l.Contains("DROPPED", StringComparison.Ordinal));
        Assert.Contains(push.SessionId, drop);
        Assert.Contains("2026-09-02T16:00:00", drop);
        Assert.Contains("no tunnel connection", drop);

        // THE COUNTERS. Exactly one drop, and nothing delivered - the pair, because either number alone is
        // satisfied by a Director that never pushed anything.
        Assert.Equal(1, GatewayStreamClient.ScreenPushesDropped - droppedBefore);
        Assert.Equal(0, GatewayStreamClient.ScreenPushesDelivered - deliveredBefore);
    }

    /// <summary>
    /// A push with no session id is a malformed capture rather than a transport failure, and it is counted
    /// and logged too. Without this it would be the one remaining way for a screen to disappear without
    /// anything saying so.
    /// </summary>
    [Fact]
    public void A_push_with_no_session_id_is_also_counted_rather_than_dropped_in_silence()
    {
        var client = UnconnectedClient();
        var droppedBefore = GatewayStreamClient.ScreenPushesDropped;

        using var scope = FileLog.RedirectForTests();
        client.PushScreen(Push(""));
        var lines = scope.DrainAndReadLines();

        Assert.Contains(lines, l => l.Contains("no session id", StringComparison.Ordinal));
        Assert.Equal(1, GatewayStreamClient.ScreenPushesDropped - droppedBefore);
    }
}
