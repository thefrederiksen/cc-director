using CcDirector.Core.Memory;
using Xunit;

namespace CcDirector.Core.Tests.Memory;

/// <summary>
/// The memory pressure rule, the leak rule, and the fold.
///
/// The numbers in these tests are the real ones measured on SOREN_NORTH on 2026-07-30, when a
/// Director that had been up for a day held 7.53 GB of Large Object Heap out of a 9.74 GB heap
/// and the machine's commit peak passed its own limit. Using the real shape means these tests
/// answer a specific question: would this library have caught it?
/// </summary>
public class MemoryPressureFoldTests
{
    private const long GB = 1024L * 1024 * 1024;

    private static MachineMemoryReading Machine(
        long physicalTotal, long physicalAvailable,
        long commitTotal, long commitLimit, long commitPeak = 0)
        => new(
            PhysicalTotalBytes: physicalTotal,
            PhysicalAvailableBytes: physicalAvailable,
            CommitTotalBytes: commitTotal,
            CommitLimitBytes: commitLimit,
            CommitPeakBytes: commitPeak == 0 ? commitTotal : commitPeak,
            KernelPagedBytes: 0,
            KernelNonPagedBytes: 0,
            ProcessCount: 0, ThreadCount: 0, HandleCount: 0);

    private static ManagedHeapReading Heap(
        long loh, long total, int gen2Collections, DateTimeOffset at, long fragmentation = 0)
        => new(
            TotalHeapBytes: total,
            Gen0Bytes: 0, Gen1Bytes: 0,
            Gen2Bytes: Math.Max(0, total - loh),
            LargeObjectHeapBytes: loh,
            PinnedObjectHeapBytes: 0,
            LargeObjectHeapFragmentationBytes: fragmentation,
            CommittedBytes: total,
            Gen2CollectionCount: gen2Collections,
            PauseTimePercentage: 0,
            TakenAtUtc: at);

    // ---- the pressure rule -------------------------------------------------

    [Fact]
    public void LevelFor_PlentyOfCommitAndPhysical_IsNormal()
    {
        var r = Machine(64 * GB, 30 * GB, commitTotal: 40 * GB, commitLimit: 105 * GB);
        Assert.Equal(MemoryPressureLevel.Normal, MemoryPressureRule.LevelFor(r));
    }

    /// <summary>
    /// The measured machine: 84 percent of physical in use looks alarming, but with commit at
    /// 78 percent there was still real headroom. Judging on physical alone would cry wolf here.
    /// </summary>
    [Fact]
    public void LevelFor_HighPhysicalButCommitHasHeadroom_IsNotCritical()
    {
        var r = Machine(64 * GB, 10 * GB, commitTotal: 82 * GB, commitLimit: 105 * GB);
        Assert.NotEqual(MemoryPressureLevel.Critical, MemoryPressureRule.LevelFor(r));
    }

    /// <summary>
    /// The failure this library exists to catch: commit near its limit. Physical is comfortable,
    /// so a physical-only monitor would report everything fine right up to the allocation failure.
    /// </summary>
    [Fact]
    public void LevelFor_CommitNearItsLimit_IsCriticalEvenWithPhysicalFree()
    {
        var r = Machine(64 * GB, 25 * GB, commitTotal: 100 * GB, commitLimit: 105 * GB);
        Assert.Equal(MemoryPressureLevel.Critical, MemoryPressureRule.LevelFor(r));
    }

    [Fact]
    public void HasExhaustedCommitSinceBoot_PeakAboveLimit_IsTrue()
    {
        // 106.28 GB peak against a 105.78 GB limit - the real reading.
        var r = Machine(64 * GB, 20 * GB,
            commitTotal: 70 * GB,
            commitLimit: (long)(105.78 * GB),
            commitPeak: (long)(106.28 * GB));

        Assert.True(r.HasExhaustedCommitSinceBoot);
        Assert.Equal(MemoryPressureLevel.Normal, MemoryPressureRule.LevelFor(r));
    }

    // ---- the leak rule -----------------------------------------------------

    /// <summary>
    /// Growth with no generation 2 collection in the window proves nothing - the Large Object
    /// Heap is only swept on such a collection, so this may be garbage nobody has collected yet.
    /// </summary>
    [Fact]
    public void Judge_GrowthButNoCollection_IsUndetermined()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var readings = new[]
        {
            Heap(2 * GB, 3 * GB, gen2Collections: 7, t0),
            Heap(5 * GB, 6 * GB, gen2Collections: 7, t0.AddHours(1)),
            Heap(7 * GB, 8 * GB, gen2Collections: 7, t0.AddHours(2)),
        };

        var verdict = LargeObjectHeapLeakRule.Judge(readings);

        Assert.Equal(LeakSuspicion.Undetermined, verdict.Suspicion);
        Assert.Contains("no generation 2 collection", verdict.Reason);
    }

    /// <summary>The real Director shape: growth that survived collections is retention.</summary>
    [Fact]
    public void Judge_GrowthSurvivingCollections_IsSuspected()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var readings = new[]
        {
            Heap((long)(2.0 * GB), (long)(2.9 * GB), gen2Collections: 10, t0),
            Heap((long)(4.5 * GB), (long)(5.9 * GB), gen2Collections: 14, t0.AddHours(6)),
            Heap((long)(7.53 * GB), (long)(9.74 * GB), gen2Collections: 19, t0.AddHours(12)),
        };

        var verdict = LargeObjectHeapLeakRule.Judge(readings);

        Assert.Equal(LeakSuspicion.Suspected, verdict.Suspicion);
        Assert.True(verdict.GrowthBytesPerHour > 0);
        Assert.Equal(9, verdict.Gen2CollectionsInWindow);
        Assert.Contains("retained rather than uncollected", verdict.Reason);
    }

    /// <summary>A big but flat Large Object Heap is not a leak. Reporting it as one trains people to ignore the warning.</summary>
    [Fact]
    public void Judge_LargeButFlat_IsHealthy()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var readings = new[]
        {
            Heap(4 * GB, 5 * GB, gen2Collections: 10, t0),
            Heap(4 * GB, 5 * GB, gen2Collections: 15, t0.AddHours(1)),
            Heap(4 * GB, 5 * GB, gen2Collections: 20, t0.AddHours(2)),
        };

        Assert.Equal(LeakSuspicion.Healthy, LargeObjectHeapLeakRule.Judge(readings).Suspicion);
    }

    [Fact]
    public void Judge_SmallHeapGrowingFast_IsHealthyBecauseItIsBelowTheFloor()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var readings = new[]
        {
            Heap(1 * 1024 * 1024, 8 * 1024 * 1024, gen2Collections: 1, t0),
            Heap(20 * 1024 * 1024, 40 * 1024 * 1024, gen2Collections: 3, t0.AddMinutes(10)),
            Heap(60 * 1024 * 1024, 90 * 1024 * 1024, gen2Collections: 5, t0.AddMinutes(20)),
        };

        Assert.Equal(LeakSuspicion.Healthy, LargeObjectHeapLeakRule.Judge(readings).Suspicion);
    }

    [Fact]
    public void Judge_TooFewReadings_IsUndetermined()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var readings = new[] { Heap(8 * GB, 9 * GB, 5, t0), Heap(9 * GB, 10 * GB, 6, t0.AddHours(1)) };

        Assert.Equal(LeakSuspicion.Undetermined, LargeObjectHeapLeakRule.Judge(readings).Suspicion);
    }

    // ---- the tracker -------------------------------------------------------

    [Fact]
    public void Tracker_OneBadReading_DoesNotConfirmCritical()
    {
        var tracker = new MemoryPressureTracker();
        var calm = Machine(64 * GB, 30 * GB, 40 * GB, 105 * GB);
        var spike = Machine(64 * GB, 2 * GB, 100 * GB, 105 * GB);

        tracker.Record(calm, null);
        bool changed = tracker.Record(spike, null);

        Assert.False(changed);
        Assert.Equal(MemoryPressureLevel.Normal, tracker.ConfirmedLevel);
    }

    [Fact]
    public void Tracker_SustainedPressure_ConfirmsAndReportsTheChangeOnce()
    {
        var tracker = new MemoryPressureTracker();
        var bad = Machine(64 * GB, 2 * GB, 100 * GB, 105 * GB);

        Assert.False(tracker.Record(bad, null));
        Assert.False(tracker.Record(bad, null));
        Assert.True(tracker.Record(bad, null));                       // third confirms
        Assert.Equal(MemoryPressureLevel.Critical, tracker.ConfirmedLevel);
        Assert.False(tracker.Record(bad, null));                      // already announced
    }

    /// <summary>Recovery is announced immediately - a machine that is fine should stop shouting at once.</summary>
    [Fact]
    public void Tracker_RecoveryIsImmediate()
    {
        var tracker = new MemoryPressureTracker();
        var bad = Machine(64 * GB, 2 * GB, 100 * GB, 105 * GB);
        var good = Machine(64 * GB, 30 * GB, 40 * GB, 105 * GB);

        tracker.Record(bad, null);
        tracker.Record(bad, null);
        tracker.Record(bad, null);
        Assert.Equal(MemoryPressureLevel.Critical, tracker.ConfirmedLevel);

        Assert.True(tracker.Record(good, null));
        Assert.Equal(MemoryPressureLevel.Normal, tracker.ConfirmedLevel);
    }

    [Fact]
    public void Tracker_KeepsOnlyItsCapacity()
    {
        var tracker = new MemoryPressureTracker(capacity: 5);
        var calm = Machine(64 * GB, 30 * GB, 40 * GB, 105 * GB);

        for (int i = 0; i < 20; i++) tracker.Record(calm, null);

        Assert.Equal(5, tracker.ReadingCount);
    }

    // ---- the fold ----------------------------------------------------------

    [Fact]
    public void Fold_NoMachineReading_SaysSoRatherThanClaimingHealth()
    {
        var status = MemoryPressureFold.Fold(
            new MemoryPressureFacts(null, null, null, MemoryPressureLevel.Normal, null));

        Assert.Equal("MEMORY NOT READABLE", status.Headline);
        Assert.False(status.OfferReclaimBuildServers);
    }

    [Fact]
    public void Fold_Critical_ShowsHeadroomAndOffersReclaim()
    {
        var facts = new MemoryPressureFacts(
            Machine(64 * GB, 2 * GB, 100 * GB, 105 * GB),
            null, null,
            MemoryPressureLevel.Critical,
            ReclaimableBuildServers: 46);

        var status = MemoryPressureFold.Fold(facts);

        Assert.Equal("MEMORY CRITICAL", status.Headline);
        Assert.True(status.OfferReclaimBuildServers);
        Assert.Contains("46", status.ReclaimActionLabel);
        Assert.Contains("commit remains", status.Advice);
    }

    /// <summary>
    /// The reclaim action is only offered when there is a reason to act. Offering it on a calm
    /// machine invites people to slow their own builds for nothing.
    /// </summary>
    [Fact]
    public void Fold_NormalWithBuildServers_DoesNotOfferReclaim()
    {
        var facts = new MemoryPressureFacts(
            Machine(64 * GB, 30 * GB, 40 * GB, 105 * GB),
            null, null,
            MemoryPressureLevel.Normal,
            ReclaimableBuildServers: 46);

        Assert.False(MemoryPressureFold.Fold(facts).OfferReclaimBuildServers);
    }

    /// <summary>
    /// Advice and action must agree. Caught on a real machine: at Normal the action was correctly
    /// withheld while the advice still said "ask the 30 build servers to exit", telling a person
    /// to press a button that was not there.
    /// </summary>
    [Fact]
    public void Fold_WhenReclaimIsNotOffered_TheAdviceDoesNotSuggestIt()
    {
        var facts = new MemoryPressureFacts(
            Machine(64 * GB, 30 * GB, 40 * GB, 105 * GB),
            null, null,
            MemoryPressureLevel.Normal,
            ReclaimableBuildServers: 30);

        var status = MemoryPressureFold.Fold(facts);

        Assert.False(status.OfferReclaimBuildServers);
        Assert.DoesNotContain("build server", status.Advice, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The RECLAIM clause must never promise a byte figure. Which build nodes are free is not
    /// knowable by inspection - 52 of 81 apparent orphans were serving live builds - so a number
    /// there would be invented. The commit-headroom clause beside it legitimately carries a byte
    /// figure, which is why this asserts on the reclaim sentence alone rather than the whole
    /// advice string.
    /// </summary>
    [Fact]
    public void Fold_ReclaimAdvice_PromisesNoByteFigure()
    {
        var facts = new MemoryPressureFacts(
            Machine(64 * GB, 2 * GB, 100 * GB, 105 * GB),
            null, null, MemoryPressureLevel.Critical, ReclaimableBuildServers: 46);

        var advice = MemoryPressureFold.Fold(facts).Advice;

        var reclaimSentence = advice
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(s => s.Contains("build server", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain("GB", reclaimSentence);
        Assert.DoesNotContain("MB", reclaimSentence);
        Assert.Contains("genuinely free", reclaimSentence);
    }

    /// <summary>A leak is worth reporting even when the machine has room - that is when it is cheapest to fix.</summary>
    [Fact]
    public void Fold_LeakOnACalmMachine_StillWarns()
    {
        var leak = new LeakVerdict(LeakSuspicion.Suspected, "Large Object Heap grew and survived collections.", 5 * GB, 800 * 1024 * 1024, 9);
        var facts = new MemoryPressureFacts(
            Machine(64 * GB, 30 * GB, 40 * GB, 105 * GB),
            null, leak, MemoryPressureLevel.Normal, null);

        var status = MemoryPressureFold.Fold(facts);

        Assert.Equal("MEMORY OK", status.Headline);
        Assert.True(status.ShowLeakWarning);
        Assert.Contains("keep growing", status.Advice);
    }

    [Fact]
    public void Fold_ExhaustedCommitSinceBoot_SaysSoInTheTooltip()
    {
        var facts = new MemoryPressureFacts(
            Machine(64 * GB, 20 * GB, 70 * GB, (long)(105.78 * GB), (long)(106.28 * GB)),
            null, null, MemoryPressureLevel.Normal, null);

        Assert.Contains("already reached its commit limit", MemoryPressureFold.Fold(facts).Tooltip);
    }
}
