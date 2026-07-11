using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Unit tests for the Director-local three-digit session-number allocator (issue #820).
/// </summary>
public class SessionNumberAllocatorTests
{
    [Fact]
    public void Allocate_FirstNumber_IsInRange()
    {
        var allocator = new SessionNumberAllocator();

        var n = allocator.Allocate();

        Assert.NotNull(n);
        Assert.InRange(n.Value, SessionNumberAllocator.MinNumber, SessionNumberAllocator.MaxNumber);
    }

    [Fact]
    public void Allocate_ManyNumbers_AreAllDistinct()
    {
        var allocator = new SessionNumberAllocator();
        var seen = new HashSet<int>();

        for (int i = 0; i < 50; i++)
        {
            var n = allocator.Allocate();
            Assert.NotNull(n);
            Assert.True(seen.Add(n.Value), $"number {n.Value} was handed out twice");
        }

        Assert.Equal(50, allocator.InUseCount);
    }

    [Fact]
    public void Release_FreesNumber_AndDropsInUseCount()
    {
        var allocator = new SessionNumberAllocator();
        var n = allocator.Allocate();
        Assert.NotNull(n);
        Assert.True(allocator.IsReserved(n.Value));

        allocator.Release(n.Value);

        Assert.False(allocator.IsReserved(n.Value));
        Assert.Equal(0, allocator.InUseCount);
    }

    [Fact]
    public void Allocate_AfterRelease_DoesNotImmediatelyReuseFreedNumber()
    {
        var allocator = new SessionNumberAllocator();
        var first = allocator.Allocate();
        Assert.NotNull(first);

        allocator.Release(first.Value);
        var second = allocator.Allocate();

        Assert.NotNull(second);
        Assert.NotEqual(first.Value, second.Value);
    }

    [Fact]
    public void Release_FreedNumber_IsReusable_OncePushedOutOfTheHoldback()
    {
        var allocator = new SessionNumberAllocator();
        var numbers = new List<int>();
        for (int i = 0; i < 20; i++)
        {
            var n = allocator.Allocate();
            Assert.NotNull(n);
            numbers.Add(n.Value);
        }

        var first = numbers[0];
        allocator.Release(first);
        // Free 16 more so the holdback queue rotates `first` out and it becomes eligible again.
        for (int i = 1; i <= SessionNumberAllocator.PoolCapacity && i <= 16; i++)
            allocator.Release(numbers[i]);

        var reused = false;
        for (int i = 0; i < 25; i++)
        {
            var n = allocator.Allocate();
            Assert.NotNull(n);
            if (n.Value == first)
            {
                reused = true;
                break;
            }
        }

        Assert.True(reused, "a freed number was never offered again after leaving the holdback");
    }

    [Fact]
    public void TryReserve_FreeNumber_Succeeds_ThenSecondReserveFails()
    {
        var allocator = new SessionNumberAllocator();

        Assert.True(allocator.TryReserve(412));
        Assert.True(allocator.IsReserved(412));
        Assert.False(allocator.TryReserve(412));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(1000)]
    [InlineData(0)]
    public void TryReserve_OutOfRange_Refused(int number)
    {
        var allocator = new SessionNumberAllocator();

        Assert.False(allocator.TryReserve(number));
        Assert.False(allocator.IsReserved(number));
    }

    [Fact]
    public void Allocate_DoesNotHandOutAReservedNumber()
    {
        var allocator = new SessionNumberAllocator();
        Assert.True(allocator.TryReserve(SessionNumberAllocator.MinNumber));

        for (int i = 0; i < 20; i++)
        {
            var n = allocator.Allocate();
            Assert.NotNull(n);
            Assert.NotEqual(SessionNumberAllocator.MinNumber, n.Value);
        }
    }

    [Fact]
    public void Allocate_WhenPoolExhausted_ReturnsNull()
    {
        var allocator = new SessionNumberAllocator();

        for (int i = 0; i < SessionNumberAllocator.PoolCapacity; i++)
            Assert.NotNull(allocator.Allocate());

        Assert.Equal(SessionNumberAllocator.PoolCapacity, allocator.InUseCount);
        Assert.Null(allocator.Allocate());
    }

    [Fact]
    public void Release_UnreservedNumber_IsNoOp()
    {
        var allocator = new SessionNumberAllocator();

        allocator.Release(555); // never reserved

        Assert.Equal(0, allocator.InUseCount);
    }

    // ===== Offline allocation (issue #1292): the Gateway-unreachable fallback =====

    [Fact]
    public void AllocateOffline_PicksFromTheHighBand_AndReserves()
    {
        var allocator = new SessionNumberAllocator();

        for (int i = 0; i < 50; i++)
        {
            var n = allocator.AllocateOffline();
            Assert.NotNull(n);
            Assert.InRange(n!.Value, SessionNumberAllocator.OfflineBandStart, SessionNumberAllocator.MaxNumber);
            Assert.True(allocator.IsReserved(n.Value));
        }
    }

    [Fact]
    public void AllocateOffline_ManyNumbers_AreAllDistinct()
    {
        var allocator = new SessionNumberAllocator();
        var seen = new HashSet<int>();

        // The high band holds exactly (Max - OfflineBandStart + 1) numbers; take them all.
        var bandSize = SessionNumberAllocator.MaxNumber - SessionNumberAllocator.OfflineBandStart + 1;
        for (int i = 0; i < bandSize; i++)
        {
            var n = allocator.AllocateOffline();
            Assert.NotNull(n);
            Assert.True(seen.Add(n!.Value), $"offline number {n.Value} was handed out twice");
        }
    }

    [Fact]
    public void AllocateOffline_WhenHighBandFull_SpillsToAnyFreeNumber()
    {
        var allocator = new SessionNumberAllocator();

        // Fill the whole high band.
        for (int n = SessionNumberAllocator.OfflineBandStart; n <= SessionNumberAllocator.MaxNumber; n++)
            Assert.True(allocator.TryReserve(n));

        // The next offline pick must spill below the band rather than fail.
        var spill = allocator.AllocateOffline();
        Assert.NotNull(spill);
        Assert.InRange(spill!.Value, SessionNumberAllocator.MinNumber, SessionNumberAllocator.OfflineBandStart - 1);
    }

    [Fact]
    public void AllocateOffline_WhenPoolExhausted_ReturnsNull()
    {
        var allocator = new SessionNumberAllocator();

        for (int i = 0; i < SessionNumberAllocator.PoolCapacity; i++)
            Assert.NotNull(allocator.AllocateOffline());

        Assert.Equal(SessionNumberAllocator.PoolCapacity, allocator.InUseCount);
        Assert.Null(allocator.AllocateOffline());
    }
}
