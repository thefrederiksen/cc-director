using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #2188: a live session must not report as "not found" because its Director missed one snapshot push.
///
/// The failure these pin: on 2026-07-26 a Director's re-push tick was missed twice in a row (a clean ten
/// second cadence became a thirty second gap). The Gateway's pushed cache aged past its twenty second
/// staleness cut, and for about ten seconds EVERY per-session route - attaching an image was the one a
/// person happened to press - answered 404 "session not found across any director" for fourteen sessions
/// that were alive the whole time. The very next prompt, once a push landed, returned 200.
///
/// Two properties are pinned here, and they are different properties:
///  1. TOLERANCE - one missed push cycle must still resolve the session (the hole closes).
///  2. HONESTY - past the tolerance, the refusal must distinguish "the Director is stale" (retryable) from
///     "no Director has ever pushed this id" (permanent). Collapsing those two was the original defect, and
///     a tolerance alone would not have fixed it.
/// </summary>
public sealed class SessionLocateStalePushTests
{
    private DateTime _now = new(2026, 7, 26, 13, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan StaleAfter =
        TimeSpan.FromSeconds(GatewayConfig.DefaultStreamStaleAfterSeconds);

    private PushedSessionStore NewStoreWithSession(string directorId, string sessionId)
    {
        var store = new PushedSessionStore(() => _now);
        store.RegisterConnection(TenantId.Local, directorId, "conn-1");
        var applied = store.ApplySnapshot(TenantId.Local, directorId, "conn-1", 0, new[]
        {
            new SessionDto { SessionId = sessionId, ActivityState = "Working" },
        });
        Assert.True(applied);
        return store;
    }

    // ---------------------------------------------------------------- the tolerance (property 1) ----

    [Fact]
    public void LocateGrace_IsOneWholePushCycle_NotAnArbitraryNumber()
    {
        // The cadence is staleAfterSeconds / 2, so the grace must be exactly one cycle: enough to absorb a
        // single missed tick, and no more. A number picked by feel would either fail to close the observed
        // hole or keep serving a Director that is genuinely gone.
        Assert.Equal(TimeSpan.FromSeconds(GatewayConfig.DefaultStreamStaleAfterSeconds / 2.0),
            GatewayEndpoints.LocateGrace);
    }

    [Fact]
    public void TryLocate_PushOneCycleLate_StillResolvesTheSession()
    {
        // Arrange: a session whose Director last pushed 26 seconds ago - past the 20 second staleness cut,
        // inside the 20 + 10 tolerance. This is the exact shape of the observed 26.9 second gap.
        var store = NewStoreWithSession("dir-A", "sess-1");
        _now = _now.AddSeconds(26);

        // Act
        var located = store.TryLocate(TenantId.Local, "sess-1", StaleAfter + GatewayEndpoints.LocateGrace);

        // Assert: the regression. Before the fix this was null and the route answered 404 for a live session.
        Assert.NotNull(located);
        Assert.Equal("dir-A", located!.Value.DirectorId);
        Assert.Equal("sess-1", located.Value.Session.SessionId);
    }

    [Fact]
    public void TryLocate_WithoutTheGrace_WouldHaveRefusedTheSameSession()
    {
        // The other arm: the SAME state, read with only the roster's freshness cut, is refused. Without this
        // the test above could pass for a reason unrelated to the grace window (a store that never expires,
        // a clock that never advances), and would keep passing if the grace were removed again.
        var store = NewStoreWithSession("dir-A", "sess-1");
        _now = _now.AddSeconds(26);

        Assert.Null(store.TryLocate(TenantId.Local, "sess-1", StaleAfter));
    }

    [Fact]
    public void TryLocate_PushWellPastTheGrace_IsStillRefused()
    {
        // The tolerance is bounded. A Director silent for two minutes is a real outage and must not be served
        // as if it were reachable - this is a defined grace window, not a fallback that hides an outage.
        var store = NewStoreWithSession("dir-A", "sess-1");
        _now = _now.AddMinutes(2);

        Assert.Null(store.TryLocate(TenantId.Local, "sess-1", StaleAfter + GatewayEndpoints.LocateGrace));
    }

    // ---------------------------------------------------------------- the honesty (property 2) ----

    [Fact]
    public void TryLocateIgnoringFreshness_SessionPresentButPushStale_NamesTheOwnerAndTheAge()
    {
        // Arrange: 90 seconds of silence - genuinely stale, well past any tolerance.
        var store = NewStoreWithSession("dir-A", "sess-1");
        _now = _now.AddSeconds(90);

        // Act
        var known = store.TryLocateIgnoringFreshness(TenantId.Local, "sess-1");

        // Assert: the session is KNOWN. This is what lets the route answer a retryable 503 that names the
        // delay, instead of telling the user their session does not exist.
        Assert.NotNull(known);
        Assert.Equal("dir-A", known!.Value.DirectorId);
        Assert.Equal(90, (int)Math.Round(known.Value.PushAge.TotalSeconds));
    }

    [Fact]
    public void TryLocateIgnoringFreshness_UnknownSessionId_IsNotFound()
    {
        // The permanent case must stay distinguishable, or the fix would turn every genuine 404 into a
        // "try again" the user could retry forever.
        var store = NewStoreWithSession("dir-A", "sess-1");

        Assert.Null(store.TryLocateIgnoringFreshness(TenantId.Local, "sess-does-not-exist"));
    }

    [Fact]
    public void TryLocateIgnoringFreshness_SessionInAnotherTenant_IsNotFound()
    {
        // The reason for a refusal must never leak across a tenant boundary: a caller in tenant B asking about
        // tenant A's session id gets "not found", never "that machine has not reported in for 90 seconds"
        // (which would confirm the session exists and name the owning Director).
        var store = NewStoreWithSession("dir-A", "sess-1");
        _now = _now.AddSeconds(90);

        Assert.Null(store.TryLocateIgnoringFreshness(new TenantId("t#other"), "sess-1"));
    }

    [Fact]
    public void TryLocateIgnoringFreshness_DirectorThatNeverPushed_ReportsMaximallyStale_NotZero()
    {
        // A Director registered but with no snapshot has no meaningful age. Reporting zero would read as
        // "pushed just now" and produce the sentence "has not reported in for 0 seconds", which is worse than
        // saying nothing.
        var store = new PushedSessionStore(() => _now);
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-1");

        // No snapshot applied, so the session id is unknown - the correct answer is still "not found".
        Assert.Null(store.TryLocateIgnoringFreshness(TenantId.Local, "sess-1"));
    }
}
