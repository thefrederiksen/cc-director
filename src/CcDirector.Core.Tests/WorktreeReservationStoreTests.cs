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
