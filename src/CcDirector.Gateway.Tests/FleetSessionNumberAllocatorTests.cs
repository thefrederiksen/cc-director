using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the fleet-wide session-number authority (issue #1292): the Gateway hands out
/// three-digit numbers unique across every Director, prefers the low band, is idempotent per session,
/// reuses freed numbers, adopts numbers it did not hand out, and frees a removed Director's numbers.
/// </summary>
public class FleetSessionNumberAllocatorTests
{
    [Fact]
    public void Allocate_FirstNumber_IsTheLowestCoordinatedNumber()
    {
        var a = new FleetSessionNumberAllocator();

        var n = a.Allocate("s1", "dirA");

        Assert.Equal(FleetSessionNumberAllocator.MinNumber, n);
    }

    [Fact]
    public void Allocate_FillsFromTheLowEnd_AndStaysInTheCoordinatedBand()
    {
        var a = new FleetSessionNumberAllocator();

        for (int i = 0; i < 100; i++)
        {
            var n = a.Allocate($"s{i}", "dirA");
            Assert.NotNull(n);
            // The coordinated band is 100-799; the offline band (800-999) is left clear until it is full.
            Assert.InRange(n!.Value, FleetSessionNumberAllocator.MinNumber, FleetSessionNumberAllocator.CoordinatedMaxNumber);
        }
    }

    [Fact]
    public void Allocate_DifferentSessions_GetDistinctNumbers()
    {
        var a = new FleetSessionNumberAllocator();
        var seen = new HashSet<int>();

        for (int i = 0; i < 200; i++)
        {
            var n = a.Allocate($"s{i}", i % 2 == 0 ? "dirA" : "dirB");
            Assert.NotNull(n);
            Assert.True(seen.Add(n!.Value), $"number {n.Value} was handed to two sessions");
        }
    }

    [Fact]
    public void Allocate_SameSessionTwice_IsIdempotent()
    {
        var a = new FleetSessionNumberAllocator();

        var first = a.Allocate("s1", "dirA");
        var again = a.Allocate("s1", "dirA");

        Assert.Equal(first, again);
        Assert.Equal(1, a.InUseCount);
    }

    [Fact]
    public void Release_FreesTheNumber_ForReuse()
    {
        var a = new FleetSessionNumberAllocator();
        var n = a.Allocate("s1", "dirA");
        Assert.NotNull(n);

        a.Release("s1");

        Assert.Equal(0, a.InUseCount);
        Assert.Null(a.NumberFor("s1"));
        // The freed number is available again (the pool is empty, so the lowest is handed back out).
        var reused = a.Allocate("s2", "dirA");
        Assert.Equal(n, reused);
    }

    [Fact]
    public void Adopt_MarksAnUnhandedNumberInUse_SoItIsNotHandedOut()
    {
        var a = new FleetSessionNumberAllocator();

        // A Director assigned 100 offline (or it survived a restart); the Gateway learns it via adopt.
        a.Adopt("offline1", "dirB", FleetSessionNumberAllocator.MinNumber);

        // A fresh allocation must skip the adopted number.
        var n = a.Allocate("s1", "dirA");
        Assert.NotEqual(FleetSessionNumberAllocator.MinNumber, n);
        Assert.Equal(2, a.InUseCount);
    }

    [Fact]
    public void Adopt_NeverFreesANumber_OnAbsence()
    {
        var a = new FleetSessionNumberAllocator();
        var n = a.Allocate("s1", "dirA");
        Assert.NotNull(n);

        // Re-adopting an already-known session is a no-op; absence from a later view never reclaims.
        a.Adopt("s1", "dirA", n!.Value);
        Assert.Equal(1, a.InUseCount);
        Assert.Equal(n, a.NumberFor("s1"));
    }

    [Fact]
    public void Adopt_IsIdempotent_ForItsOwnHandOut()
    {
        var a = new FleetSessionNumberAllocator();
        var n = a.Allocate("s1", "dirA");

        // The /sessions aggregation observes the session it just handed a number to.
        a.Adopt("s1", "dirA", n!.Value);
        a.Adopt("s1", "dirA", n.Value);

        Assert.Equal(1, a.InUseCount);
    }

    [Fact]
    public void ReleaseForDirector_FreesEveryNumberThatDirectorOwned()
    {
        var a = new FleetSessionNumberAllocator();
        a.Allocate("a1", "dirA");
        a.Allocate("a2", "dirA");
        var keep = a.Allocate("b1", "dirB");
        Assert.Equal(3, a.InUseCount);

        a.ReleaseForDirector("dirA");

        Assert.Equal(1, a.InUseCount);
        Assert.Null(a.NumberFor("a1"));
        Assert.Null(a.NumberFor("a2"));
        Assert.Equal(keep, a.NumberFor("b1"));
    }

    [Fact]
    public void Allocate_SpillsToTheOfflineBand_OnlyWhenTheCoordinatedBandIsFull()
    {
        var a = new FleetSessionNumberAllocator();

        // Fill the entire coordinated band (100-799).
        var coordinatedCount = FleetSessionNumberAllocator.CoordinatedMaxNumber - FleetSessionNumberAllocator.MinNumber + 1;
        for (int i = 0; i < coordinatedCount; i++)
            Assert.NotNull(a.Allocate($"c{i}", "dirA"));

        // The next allocation spills into the offline band.
        var spill = a.Allocate("spill", "dirA");
        Assert.NotNull(spill);
        Assert.InRange(spill!.Value, FleetSessionNumberAllocator.CoordinatedMaxNumber + 1, FleetSessionNumberAllocator.MaxNumber);
    }

    [Fact]
    public void Allocate_WhenPoolExhausted_ReturnsNull()
    {
        var a = new FleetSessionNumberAllocator();
        var total = FleetSessionNumberAllocator.MaxNumber - FleetSessionNumberAllocator.MinNumber + 1;
        for (int i = 0; i < total; i++)
            Assert.NotNull(a.Allocate($"s{i}", "dirA"));

        Assert.Null(a.Allocate("overflow", "dirA"));
    }
}
