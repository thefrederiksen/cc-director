namespace CcDirector.Core.Memory;

/// <summary>What a run of heap readings says about a possible leak.</summary>
public enum LeakSuspicion
{
    /// <summary>Not enough readings, or no generation 2 collection yet, to say anything.</summary>
    Undetermined,

    /// <summary>The heap is behaving.</summary>
    Healthy,

    /// <summary>Large Object Heap growth has survived a collection. Worth looking at.</summary>
    Suspected
}

/// <summary>The verdict, with the numbers that produced it so a report can show its working.</summary>
/// <param name="Suspicion">The finding.</param>
/// <param name="Reason">One sentence, in plain words, safe to show a person.</param>
/// <param name="GrowthBytes">Large Object Heap growth across the window, negative if it shrank.</param>
/// <param name="GrowthBytesPerHour">Growth rate, the number that predicts when this becomes a problem.</param>
/// <param name="Gen2CollectionsInWindow">
/// Collections that happened during the window. Zero means growth proves nothing yet.
/// </param>
public sealed record LeakVerdict(
    LeakSuspicion Suspicion,
    string Reason,
    long GrowthBytes,
    double GrowthBytesPerHour,
    int Gen2CollectionsInWindow);

/// <summary>
/// Decides whether a run of heap readings looks like a Large Object Heap leak.
///
/// The rule that matters is the third condition below: growth only counts if a generation 2
/// collection happened during the window. The Large Object Heap is swept ONLY on a generation 2
/// collection, so a heap that grew without one has merely accumulated garbage that nobody has
/// tried to collect yet - that is not evidence of anything. Growth that SURVIVED a sweep is
/// retention, and retention is what a leak is made of.
///
/// Written as a pure function over readings so it can be tested against a scripted history
/// without a machine, a timer, or a leak.
/// </summary>
public static class LargeObjectHeapLeakRule
{
    /// <summary>
    /// Below this, ignore everything. A young process legitimately grows a small Large Object
    /// Heap, and calling that a leak would train people to ignore the warning.
    /// </summary>
    public const long MinimumInterestingBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Above this share of the heap, the Large Object Heap is the story rather than a detail.
    /// The Director that prompted this work sat at 0.77.
    /// </summary>
    public const double DominantFraction = 0.5;

    /// <summary>Growth across the window that counts as real rather than noise.</summary>
    public const double MinimumGrowthFraction = 0.10;

    /// <summary>Readings needed before any verdict but Undetermined.</summary>
    public const int MinimumReadings = 3;

    /// <summary>
    /// Judge a window of readings, oldest first. Returns Undetermined rather than guessing when
    /// the window is too short or nothing has been collected yet.
    /// </summary>
    public static LeakVerdict Judge(IReadOnlyList<ManagedHeapReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);

        if (readings.Count < MinimumReadings)
        {
            return new LeakVerdict(
                LeakSuspicion.Undetermined,
                $"Not enough readings yet - {readings.Count} of {MinimumReadings} needed.",
                0, 0, 0);
        }

        var first = readings[0];
        var last = readings[^1];

        long growth = last.LargeObjectHeapBytes - first.LargeObjectHeapBytes;
        int collections = Math.Max(0, last.Gen2CollectionCount - first.Gen2CollectionCount);

        double hours = (last.TakenAtUtc - first.TakenAtUtc).TotalHours;
        double perHour = hours > 0 ? growth / hours : 0;

        if (last.LargeObjectHeapBytes < MinimumInterestingBytes)
        {
            return new LeakVerdict(
                LeakSuspicion.Healthy,
                $"Large Object Heap is {Format(last.LargeObjectHeapBytes)}, below the {Format(MinimumInterestingBytes)} floor worth watching.",
                growth, perHour, collections);
        }

        // A sweep has to have happened, or growth means nothing. This is the clause that stops
        // the rule shouting about garbage that simply has not been collected yet.
        if (collections == 0)
        {
            return new LeakVerdict(
                LeakSuspicion.Undetermined,
                $"Large Object Heap is {Format(last.LargeObjectHeapBytes)} and grew {Format(growth)}, but no generation 2 collection has run in this window - the growth has not been tested yet.",
                growth, perHour, collections);
        }

        bool dominant = last.LargeObjectHeapFraction >= DominantFraction;
        bool grewMaterially = first.LargeObjectHeapBytes > 0
            && growth >= first.LargeObjectHeapBytes * MinimumGrowthFraction;

        if (dominant && grewMaterially)
        {
            return new LeakVerdict(
                LeakSuspicion.Suspected,
                $"Large Object Heap is {Format(last.LargeObjectHeapBytes)} - {last.LargeObjectHeapFraction:P0} of the heap - and grew {Format(growth)} " +
                $"({Format((long)perHour)} per hour) through {collections} generation 2 collection(s), so it is retained rather than uncollected.",
                growth, perHour, collections);
        }

        if (dominant)
        {
            return new LeakVerdict(
                LeakSuspicion.Healthy,
                $"Large Object Heap is large at {Format(last.LargeObjectHeapBytes)} ({last.LargeObjectHeapFraction:P0} of the heap) but is not growing.",
                growth, perHour, collections);
        }

        return new LeakVerdict(
            LeakSuspicion.Healthy,
            $"Large Object Heap is {Format(last.LargeObjectHeapBytes)}, {last.LargeObjectHeapFraction:P0} of the heap, and steady.",
            growth, perHour, collections);
    }

    private static string Format(long bytes)
    {
        double abs = Math.Abs(bytes);
        if (abs >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
        if (abs >= 1024L * 1024) return $"{bytes / 1024.0 / 1024:F0} MB";
        return $"{bytes / 1024.0:F0} KB";
    }
}
