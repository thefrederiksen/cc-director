namespace CcDirector.Core.Memory;

/// <summary>
/// This process's own managed heap, broken out by generation.
///
/// The Large Object Heap is called out as a first-class number because it is the signature of a
/// whole class of defect. On 2026-07-30 a Director that had been up for a day held 9.74 GB of
/// managed heap, of which 7.53 GB was Large Object Heap - five byte arrays holding captured
/// microphone audio that nothing ever released. A single total heap number said only "big"; the
/// Large Object Heap share said "something is retaining huge buffers", which is a different and
/// far more actionable statement.
///
/// The Large Object Heap deserves this attention because of how it behaves: it is swept only on
/// a generation 2 collection, and it is not compacted by default. So it grows, and it does not
/// come back on its own.
/// </summary>
/// <param name="TotalHeapBytes">Everything the collector is managing.</param>
/// <param name="Gen0Bytes">Generation 0.</param>
/// <param name="Gen1Bytes">Generation 1.</param>
/// <param name="Gen2Bytes">Generation 2.</param>
/// <param name="LargeObjectHeapBytes">Objects of 85,000 bytes or more.</param>
/// <param name="PinnedObjectHeapBytes">The pinned object heap.</param>
/// <param name="LargeObjectHeapFragmentationBytes">
/// Free space inside the Large Object Heap. The part of the Large Object Heap that is NOT
/// fragmentation is live retained data - which is what separates a leak from mere untidiness.
/// </param>
/// <param name="CommittedBytes">What the collector has committed from the operating system.</param>
/// <param name="Gen2CollectionCount">Generation 2 collections since process start.</param>
/// <param name="PauseTimePercentage">Share of process time spent paused for collection.</param>
/// <param name="TakenAtUtc">When this was read, so a trend can be measured across readings.</param>
public sealed record ManagedHeapReading(
    long TotalHeapBytes,
    long Gen0Bytes,
    long Gen1Bytes,
    long Gen2Bytes,
    long LargeObjectHeapBytes,
    long PinnedObjectHeapBytes,
    long LargeObjectHeapFragmentationBytes,
    long CommittedBytes,
    int Gen2CollectionCount,
    double PauseTimePercentage,
    DateTimeOffset TakenAtUtc)
{
    /// <summary>
    /// How much of the heap sits on the Large Object Heap, 0 to 1. Above roughly half is unusual
    /// for an interactive application and is the first thing worth explaining.
    /// </summary>
    public double LargeObjectHeapFraction =>
        TotalHeapBytes <= 0 ? 0 : (double)LargeObjectHeapBytes / TotalHeapBytes;

    /// <summary>
    /// Large Object Heap bytes that are actually live, rather than free space left by
    /// non-compaction. Retention, not untidiness, is what indicates a leak.
    /// </summary>
    public long LargeObjectHeapLiveBytes =>
        Math.Max(0, LargeObjectHeapBytes - LargeObjectHeapFragmentationBytes);
}

/// <summary>
/// Reads this process's own managed heap. Cheap and allocation-light: it asks the collector for
/// what it already knows and never forces a collection, so it is safe to call on a timer.
/// </summary>
public static class ManagedHeapProbe
{
    // Generation indexes as the runtime reports them in GCMemoryInfo.GenerationInfo.
    private const int Gen0 = 0;
    private const int Gen1 = 1;
    private const int Gen2 = 2;
    private const int LargeObjectHeap = 3;
    private const int PinnedObjectHeap = 4;

    /// <summary>Read the current managed heap state of this process.</summary>
    public static ManagedHeapReading Read(DateTimeOffset now)
    {
        var info = GC.GetGCMemoryInfo();

        // GenerationInfo is a ref struct span, so it cannot be captured by a local function -
        // copy the few numbers out first.
        var generations = info.GenerationInfo;
        Span<long> sizes = stackalloc long[5];
        Span<long> fragmentation = stackalloc long[5];
        for (int i = 0; i < sizes.Length && i < generations.Length; i++)
        {
            sizes[i] = generations[i].SizeAfterBytes;
            fragmentation[i] = generations[i].FragmentationAfterBytes;
        }

        return new ManagedHeapReading(
            TotalHeapBytes: info.HeapSizeBytes,
            Gen0Bytes: sizes[Gen0],
            Gen1Bytes: sizes[Gen1],
            Gen2Bytes: sizes[Gen2],
            LargeObjectHeapBytes: sizes[LargeObjectHeap],
            PinnedObjectHeapBytes: sizes[PinnedObjectHeap],
            LargeObjectHeapFragmentationBytes: fragmentation[LargeObjectHeap],
            CommittedBytes: info.TotalCommittedBytes,
            Gen2CollectionCount: GC.CollectionCount(2),
            PauseTimePercentage: info.PauseTimePercentage,
            TakenAtUtc: now);
    }
}
