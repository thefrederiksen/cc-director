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
        processStartUtc: pid => ownerAlive && pid == 4242 ? OwnerStart : (DateTime?)null);

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
}
