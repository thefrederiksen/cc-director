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
/// THE BRIDGE IS GONE, AS ITS AUTHOR SPECIFIED. This test used to read the adoption step's bound by
/// reflection because that bound was private and on another worker's branch. The branches are merged and the
/// bound is public, so the reflection has been replaced by the direct reference it was always a stand-in for:
/// there is now ONE constant, read from both sides. A rename can no longer be a runtime surprise - it is a
/// compile error, which is what the reflection was approximating and could only ever do at run time.
/// </summary>
public sealed class StatsStoreDeadlineRelationshipTests
{
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
    /// THE FIXTURE'S OWN PREMISE. The bound must be a positive duration, or the ordering this test asserts
    /// cannot be evaluated against it and the comparison above would be measuring nothing.
    /// </summary>
    [Fact]
    public void TheInnerBound_IsStillWhereThisTestLooksForIt()
    {
        Assert.True(
            GatewayStatsSqliteAdoption.WriteLockWait > TimeSpan.Zero,
            $"{nameof(GatewayStatsSqliteAdoption)}.{nameof(GatewayStatsSqliteAdoption.WriteLockWait)} is not " +
            "a positive duration, so the relationship this test asserts cannot be evaluated against it.");
    }

    private static TimeSpan ReadInnerBound() => GatewayStatsSqliteAdoption.WriteLockWait;
}
