using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Snooze;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The "Snooze ended" badge (<see cref="SessionDto.SnoozeExpired"/>) is folded, both ways, in
/// <see cref="GatewayEndpoints.StampFleetRolesAndFold"/>. Its whole job is to tell the owner "this session
/// did not send a fresh message - it came back because its snooze timer ran out, go see why it went quiet."
/// So it must be true for EXACTLY one condition - an armed snooze whose clock has elapsed - and false for
/// every other, including a session that left needs-you by some OTHER route.
///
/// The defect this guards (memory snooze-expired-never-cleared-gateway-bug): the fold set the flag
/// one-way - "if expired, set true" - and never wrote false. A DTO reaching the fold can ALREADY carry the
/// badge, because the FleetRosterCache stores folded clones and re-serves them, so the one-way set latched
/// the badge on forever: a re-snooze that armed a fresh clock, or (with slice 1) a work burst that deleted
/// the entry, left the badge riding along on a session that never returned by expiry at all. Assigning the
/// flag = IsExpired every fold makes it mean exactly one thing.
///
/// These call the fold directly so the assertion is on the fold's own contract, not on any cache or client.
/// The registry is the sole timer owner; the DTO's incoming SnoozeExpired stands in for a cached clone that
/// a previous fold already stamped.
/// </summary>
public sealed class SnoozeExpiredBadgeFoldTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-snoozebadge-" + Guid.NewGuid().ToString("N"));

    private string Path_ => System.IO.Path.Combine(_dir, "snooze.json");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private static SessionDto Session(string sid, bool incomingBadge) => new()
    {
        SessionId = sid,
        Agent = "ClaudeCode",
        RepoPath = "repo",
        ActivityState = "WaitingForInput",
        Status = "Running",
        StatusColor = "red",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
        SnoozeExpired = incomingBadge,
    };

    /// <summary>Run the shared fold over one session, its own universe, with the given registry.</summary>
    private static bool FoldBadge(SnoozeRegistry reg, SessionDto s)
    {
        var list = new List<SessionDto> { s };
        GatewayEndpoints.StampFleetRolesAndFold(list, list, needsYouStampFor: null, snoozeRegistry: reg);
        return s.SnoozeExpired;
    }

    [Fact]
    public void AnElapsedArmedSnooze_StampsTheBadge()
    {
        // The one condition the badge is FOR: an armed clock that ran out. The DTO arrives without a badge
        // and the fold raises it.
        var reg = new SnoozeRegistry(Path_);
        reg.Snooze("s1", DateTime.UtcNow.AddMinutes(-1), "dir-1"); // already due

        Assert.True(FoldBadge(reg, Session("s1", incomingBadge: false)));
    }

    [Fact]
    public void AnElapsedArmedSnooze_KeepsTheBadge_WhenItWasAlreadySet()
    {
        // The badge is continuous while the entry is still expired-and-present: a fold over a cached clone
        // that already carries it must not flicker it off. Guards against a fix that clears unconditionally.
        var reg = new SnoozeRegistry(Path_);
        reg.Snooze("s1", DateTime.UtcNow.AddMinutes(-1), "dir-1"); // still due, not yet swept

        Assert.True(FoldBadge(reg, Session("s1", incomingBadge: true)));
    }

    [Fact]
    public void AReSnoozeAfterExpiry_ClearsTheBadge()
    {
        // THE BUG. The owner let a snooze expire (badge shown), then re-snoozed the session, arming a fresh
        // future clock. The card now reads grey "Snoozed" and must NOT still carry "Snooze ended" - it did
        // not come back by expiry, the owner parked it again. The incoming badge stands for the cached clone
        // the earlier expired fold stamped.
        var reg = new SnoozeRegistry(Path_);
        reg.Snooze("s1", DateTime.UtcNow.AddHours(12), "dir-1"); // re-armed, far in the future

        Assert.False(FoldBadge(reg, Session("s1", incomingBadge: true)));
    }

    [Fact]
    public void AWorkBurstThatDeletedTheEntry_ClearsTheBadge()
    {
        // Slice 1 interplay. Work on a snoozed session DELETES its entry, so IsExpired is false and the
        // session returns as a plain red "needs you" - never with a "Snooze ended" badge, because it did not
        // come back by expiry, work woke it. There is no entry at all here; the incoming badge is the stale
        // one a prior expired fold left on the cached clone.
        var reg = new SnoozeRegistry(Path_); // no entry for s1 - work deleted it

        Assert.False(FoldBadge(reg, Session("s1", incomingBadge: true)));
    }

    [Fact]
    public void ADeferredHold_NeverCarriesTheBadge()
    {
        // A deferred hold has no clock (it starts when the work ends), so it is never expired and never the
        // "came back by timer" case. A stale incoming badge is cleared here too.
        var reg = new SnoozeRegistry(Path_);
        reg.SnoozeDeferred("s1", 720, "dir-1");

        Assert.False(FoldBadge(reg, Session("s1", incomingBadge: true)));
    }

    [Fact]
    public void AnArmedSnoozeStillRunning_CarriesNoBadge()
    {
        // Snoozed and still in the future: parked, not returned. No badge, and a stale incoming one is
        // cleared.
        var reg = new SnoozeRegistry(Path_);
        reg.Snooze("s1", DateTime.UtcNow.AddMinutes(30), "dir-1");

        Assert.False(FoldBadge(reg, Session("s1", incomingBadge: true)));
    }
}
