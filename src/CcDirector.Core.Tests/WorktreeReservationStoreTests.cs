using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The machine-local session reservation the worktree reaper trusts (inspection): a session reserves
/// its working directory while alive; a reservation whose owning Director is gone is stale and pruned.
/// </summary>
public sealed class WorktreeReservationStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccd-resv-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime OwnerStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private WorktreeReservationStore Store(bool ownerAlive = true) => new(
        dir: _dir,
        ownerPid: 4242,
        ownerStartUtc: OwnerStart,
        probeOwner: pid => ownerAlive && pid == 4242
            ? (OwnerState.Alive, OwnerStart)
            : (OwnerState.Gone, (DateTime?)null));

    [Fact]
    public void Reserve_MakesThePathLive_ReleaseRemovesIt()
    {
        var wt = Path.Combine(_dir, "wt");
        var store = Store();

        store.Reserve(wt, "sess-1");
        Assert.Contains(WorktreeReaperService.NormalizePath(wt), store.LiveReservedPaths());

        store.Release("sess-1");
        Assert.DoesNotContain(WorktreeReaperService.NormalizePath(wt), store.LiveReservedPaths());
    }

    [Fact]
    public void Reservation_WhoseOwningDirectorIsGone_IsPrunedAndNotReturned()
    {
        var wt = Path.Combine(_dir, "wt");

        // Write the reservation with a live owner...
        Store(ownerAlive: true).Reserve(wt, "sess-1");
        // ...then read it through a store whose owner-liveness lookup reports the Director gone.
        var live = Store(ownerAlive: false).LiveReservedPaths();

        Assert.Empty(live);
    }

    // FAIL CLOSED (inspection round 5): an owner process that cannot be INSPECTED (access denied,
    // transient error) must NOT be treated as gone - dropping protection on uncertainty is the unsafe
    // direction. The reservation is kept and still protects the worktree.
    [Fact]
    public void Reservation_WhoseOwnerCannotBeInspected_IsKept_NotPruned()
    {
        var wt = Path.Combine(_dir, "wt");
        Store(ownerAlive: true).Reserve(wt, "sess-1");

        var uninspectable = new WorktreeReservationStore(
            dir: _dir, ownerPid: 4242, ownerStartUtc: OwnerStart,
            probeOwner: _ => (OwnerState.Unknown, (DateTime?)null));

        Assert.Contains(WorktreeReaperService.NormalizePath(wt), uninspectable.LiveReservedPaths());
    }

    // FAIL CLOSED (inspection round 5): a reused pid whose start time no longer matches means the
    // original owner is gone - the reservation is stale and pruned.
    [Fact]
    public void Reservation_WhoseOwnerPidWasReused_IsPruned()
    {
        var wt = Path.Combine(_dir, "wt");
        Store(ownerAlive: true).Reserve(wt, "sess-1");

        var reused = new WorktreeReservationStore(
            dir: _dir, ownerPid: 4242, ownerStartUtc: OwnerStart,
            probeOwner: _ => (OwnerState.Alive, OwnerStart.AddHours(6))); // alive, but different start

        Assert.Empty(reused.LiveReservedPaths());
    }

    // FAIL CLOSED (inspection round 6): a reservation file that exists but cannot be READ or PARSED
    // (a lock, a corrupt record) must abort the read - we cannot know what worktree it protects, so
    // treating it as absent would let the reaper delete under a live session.
    [Fact]
    public void LiveReservedPaths_WhenAReservationFileCannotBeParsed_ThrowsFailClosed()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "corrupt.json"), "{ this is not valid json");

        Assert.ThrowsAny<Exception>(() => Store().LiveReservedPaths());
    }

    // SESSION-PROCESS LIVENESS (inspection round 6): a reservation owned by a live SESSION process
    // stays alive even though the DEFAULT owner (this Director) is reported gone - so a session, or a
    // detached child, that outlives a force-killed Director keeps its worktree protected.
    [Fact]
    public void Reserve_OwnedByALiveSessionProcess_SurvivesEvenWhenTheDirectorIsGone()
    {
        var wt = Path.Combine(_dir, "wt");
        const int directorPid = 4242, sessionPid = 5150;
        var sessionStart = OwnerStart.AddMinutes(1);

        var store = new WorktreeReservationStore(
            dir: _dir, ownerPid: directorPid, ownerStartUtc: OwnerStart,
            probeOwner: pid => pid == sessionPid
                ? (OwnerState.Alive, sessionStart)   // the session process is alive
                : (OwnerState.Gone, (DateTime?)null)); // the Director is gone

        // Reserve owned by the SESSION process, not the Director.
        store.Reserve(wt, "sess-1", sessionPid, sessionStart);

        Assert.Contains(WorktreeReaperService.NormalizePath(wt), store.LiveReservedPaths());
    }

    // The machine-wide critical section is mutually exclusive across store instances on the same dir -
    // this is what serializes a reservation write against the reaper's remove.
    [Fact]
    public void EnterCriticalSection_IsMutuallyExclusive_AcrossInstances()
    {
        var a = Store();
        var b = Store();

        using (a.EnterCriticalSection(TimeSpan.FromSeconds(1)))
        {
            Assert.Throws<TimeoutException>(() => b.EnterCriticalSection(TimeSpan.FromMilliseconds(150)));
        }
        // Released - now it can be acquired.
        using (b.EnterCriticalSection(TimeSpan.FromSeconds(1))) { }
    }
}
