using System.Reflection;
using CcDirector.Gateway.Stats.Data;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE INNER BOUND MUST SIT INSIDE THE OUTER ONE - asserted as a RELATIONSHIP, never as a pair of literals.
///
/// TWO BOUNDS, TWO OWNERS, ONE ORDERING. The adoption step bounds how long it waits for another writer before
/// reporting the store BUSY, with a named reason that tells the operator exactly what is happening. The
/// startup boundary bounds how long the Gateway waits for the whole open before finishing boot without
/// statistics. If the OUTER bound expires first, the inner one is useless: the caller stops waiting before
/// the named answer arrives and reports a generic one instead, which is precisely the operator misdirection
/// this work exists to remove. A local writer lock must arrive as a local writer lock.
///
/// IT HAS ALREADY BEEN WRONG ONCE, which is why this is a test and not a comment. Review 9 measured adoption
/// taking 35.065 seconds to return its named busy result against a twenty-second startup deadline - so the
/// outer bound expired first and the operator was told "database or network problem" over a local lock.
/// Nothing failed to build and no test noticed, because the relationship lived in two prose comments that
/// each named the other's number.
///
/// WHY LITERALS WOULD NOT DO. Both numbers are measurements and both will move. A test asserting "inner is 5
/// and outer is 8" passes today and says nothing about the property that matters; the day somebody tunes one
/// of them it either fails for no reason or - far worse - is updated to match without anyone re-checking the
/// ordering it was supposed to protect. So this reads BOTH values at run time and asserts only their
/// relationship.
///
/// THE REFLECTION IS A BRIDGE AND IT IS TEMPORARY. The adoption step's bound is private and lives on another
/// worker's branch, so it cannot be referenced directly yet; worker 2 flagged the same problem from its side,
/// having had to MIRROR this deadline as a literal in its own file. A mirrored constant is a second place to
/// forget. At merge these become ONE constant referenced from both sides and this reflection goes away - and
/// until then, reading the real field is still strictly better than copying its value, because a rename
/// breaks this test loudly instead of leaving two numbers silently drifting apart.
/// </summary>
public sealed class StatsStoreDeadlineRelationshipTests
{
    /// <summary>The adoption step's write-lock wait, in seconds.</summary>
    private const string InnerBoundField = "WriteLockWaitSeconds";

    private readonly ITestOutputHelper _out;

    public StatsStoreDeadlineRelationshipTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void TheAdoptionWriteWait_ExpiresWellBefore_TheStartupDeadline()
    {
        var inner = ReadInnerBound();
        var outer = GatewayStatsStore.OpenDeadline;

        _out.WriteLine($"inner (adoption write wait) = {inner.TotalSeconds:0.###}s");
        _out.WriteLine($"outer (startup deadline)    = {outer.TotalSeconds:0.###}s");

        Assert.True(
            inner < outer,
            $"The adoption step waits {inner.TotalSeconds:0.###}s for a writer but the startup boundary gives " +
            $"up after {outer.TotalSeconds:0.###}s, so the caller stops waiting FIRST and the operator is told " +
            "a generic failure instead of the named busy result. The inner bound is useless in that order.");

        // Margin, not merely ordering. Bounds that differ by a hair are ordered on paper and racing in
        // practice: the inner call has to return, be observed, and be turned into a result before the outer
        // clock expires, and every one of those steps costs time this comparison cannot see.
        Assert.True(
            outer - inner >= TimeSpan.FromSeconds(2),
            $"The two bounds are ordered but only {(outer - inner).TotalSeconds:0.###}s apart, which is not " +
            "enough room for the inner result to be produced and observed before the outer clock expires.");
    }

    /// <summary>
    /// THE FIXTURE'S OWN PREMISE. If the field cannot be found, this test would otherwise have nothing to
    /// compare and could quietly pass by comparing a default against a real number. A rename must break it
    /// LOUDLY, because a rename is exactly the moment somebody needs to re-check the ordering.
    /// </summary>
    [Fact]
    public void TheInnerBound_IsStillWhereThisTestLooksForIt()
    {
        var field = typeof(GatewayStatsSqliteAdoption)
            .GetField(InnerBoundField, BindingFlags.NonPublic | BindingFlags.Static);

        Assert.True(
            field is not null,
            $"{nameof(GatewayStatsSqliteAdoption)}.{InnerBoundField} no longer exists. The startup deadline " +
            "is derived from it, so it cannot simply be renamed: find the bound that replaced it, point this " +
            "test at it, and re-check that it still expires before GatewayStatsStore.OpenDeadline.");

        Assert.True(
            field!.GetValue(null) is int seconds && seconds > 0,
            $"{InnerBoundField} is not a positive whole number of seconds, so the relationship this test " +
            "asserts cannot be evaluated against it.");
    }

    private static TimeSpan ReadInnerBound()
    {
        var field = typeof(GatewayStatsSqliteAdoption)
            .GetField(InnerBoundField, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"{nameof(GatewayStatsSqliteAdoption)}.{InnerBoundField} was not found - see " +
                nameof(TheInnerBound_IsStillWhereThisTestLooksForIt) + ".");

        return TimeSpan.FromSeconds((int)field.GetValue(null)!);
    }
}
