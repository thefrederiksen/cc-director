namespace CcDirector.Core.Memory;

/// <summary>How much trouble the machine is in. A stable token for grouping, NOT for a client to branch on.</summary>
public enum MemoryPressureLevel
{
    /// <summary>Nothing to say.</summary>
    Normal,

    /// <summary>Worth showing, not worth interrupting anyone.</summary>
    Elevated,

    /// <summary>Allocations are close to failing. Say so plainly.</summary>
    Critical
}

/// <summary>
/// The rule for what counts as memory pressure, stated once.
///
/// It is keyed on COMMIT, not physical memory, and that is the central lesson from the incident
/// this came from. That machine had 63.78 GB of physical memory and a 105.78 GB commit limit; it
/// sat at 84 percent physical and felt fine, while its commit peak hit 106.28 GB - above the
/// limit. Commit exhaustion is what makes allocations fail and applications die. Physical
/// pressure only makes things slow. A monitor watching physical would have reported "fine" right
/// up to the failure.
///
/// Thresholds are fractions of the machine's own limit rather than absolute byte counts, because
/// a hardcoded gigabyte figure is a measurement of one machine that goes stale the moment it runs
/// anywhere else.
/// </summary>
public static class MemoryPressureRule
{
    /// <summary>Commit used, above which the situation is Elevated.</summary>
    public const double ElevatedCommitFraction = 0.80;

    /// <summary>Commit used, above which the situation is Critical.</summary>
    public const double CriticalCommitFraction = 0.92;

    /// <summary>Physical available, below which the situation is at least Elevated.</summary>
    public const double ElevatedPhysicalFreeFraction = 0.10;

    /// <summary>Physical available, below which the situation is Critical.</summary>
    public const double CriticalPhysicalFreeFraction = 0.04;

    /// <summary>
    /// Consecutive readings a level must hold before it is announced. Build activity moved the
    /// total on the measured machine by 8-10 GB within minutes, so a single sample crossing a
    /// threshold says nothing. Announcing on one sample produces an alert nobody trusts.
    /// </summary>
    public const int ConsecutiveReadingsToConfirm = 3;

    /// <summary>The level a single reading implies, before any debouncing.</summary>
    public static MemoryPressureLevel LevelFor(MachineMemoryReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        double commitUsed = reading.CommitUsedFraction;
        double physicalFree = reading.PhysicalTotalBytes <= 0
            ? 1
            : (double)reading.PhysicalAvailableBytes / reading.PhysicalTotalBytes;

        if (commitUsed >= CriticalCommitFraction || physicalFree <= CriticalPhysicalFreeFraction)
            return MemoryPressureLevel.Critical;

        if (commitUsed >= ElevatedCommitFraction || physicalFree <= ElevatedPhysicalFreeFraction)
            return MemoryPressureLevel.Elevated;

        return MemoryPressureLevel.Normal;
    }
}

/// <summary>
/// Everything the fold may look at, gathered by whoever is rendering and handed in whole, so the
/// fold stays a pure function that can be tested without a machine.
/// </summary>
/// <param name="Machine">The machine reading, or null when the platform cannot be read.</param>
/// <param name="OwnHeap">This process's managed heap, or null when it was not sampled.</param>
/// <param name="Leak">The leak verdict over recent readings, or null when no history exists.</param>
/// <param name="ConfirmedLevel">
/// The level after debouncing - what the watcher has actually seen hold. Passed in rather than
/// derived here because a single Facts object has no history.
/// </param>
/// <param name="ReclaimableBuildServers">
/// How many build server processes are running and could be asked to exit, or null when unknown.
/// Note that this is a COUNT of candidates, never a promise of bytes: a build node that looks
/// idle may be serving another build, and only the graceful shutdown can tell the difference.
/// </param>
public sealed record MemoryPressureFacts(
    MachineMemoryReading? Machine,
    ManagedHeapReading? OwnHeap,
    LeakVerdict? Leak,
    MemoryPressureLevel ConfirmedLevel,
    int? ReclaimableBuildServers);

/// <summary>
/// A finished memory verdict: the words to show, the colors to show them in, and which actions
/// are offered. Every field is an answer, not an input to one. A client renders these verbatim
/// and never re-derives meaning - adding a new situation is one edit in the fold rather than a
/// new branch in every client.
/// </summary>
/// <param name="Level">Stable token naming the situation, for grouping and tests.</param>
/// <param name="Headline">The short line, e.g. "MEMORY CRITICAL".</param>
/// <param name="Detail">The second line, with the numbers that matter.</param>
/// <param name="Tooltip">The whole story in sentences, for a hover or details pane.</param>
/// <param name="Accent">Text and icon color, as hex.</param>
/// <param name="Advice">What a person should actually do, or empty when there is nothing to do.</param>
/// <param name="OfferReclaimBuildServers">Whether to offer the safe build-server reclaim action.</param>
/// <param name="ReclaimActionLabel">Label for that action, empty when it is not offered.</param>
/// <param name="ShowLeakWarning">Whether to show that this process itself looks like it is leaking.</param>
/// <param name="LeakWarning">The leak sentence, empty when there is nothing to warn about.</param>
public sealed record MemoryPressureStatus(
    MemoryPressureLevel Level,
    string Headline,
    string Detail,
    string Tooltip,
    string Accent,
    string Advice,
    bool OfferReclaimBuildServers,
    string ReclaimActionLabel,
    bool ShowLeakWarning,
    string LeakWarning);

/// <summary>
/// Folds a memory reading into the finished thing a person sees. The single place a memory
/// verdict is decided - see the "client is dumb" rule in the project instructions.
/// </summary>
public static class MemoryPressureFold
{
    private const string AccentNormal = "#54A24B";
    private const string AccentElevated = "#E8A33D";
    private const string AccentCritical = "#E45756";
    private const string AccentUnknown = "#8C8C8C";

    /// <summary>Fold the facts into a finished status.</summary>
    public static MemoryPressureStatus Fold(MemoryPressureFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Machine is null)
        {
            return new MemoryPressureStatus(
                MemoryPressureLevel.Normal,
                "MEMORY NOT READABLE",
                "This platform does not report a commit limit.",
                "Memory pressure is judged from the commit limit, which only Windows reports. Rather than guess, nothing is claimed here.",
                AccentUnknown,
                Advice: "",
                OfferReclaimBuildServers: false,
                ReclaimActionLabel: "",
                ShowLeakWarning: false,
                LeakWarning: "");
        }

        var m = facts.Machine;
        var level = facts.ConfirmedLevel;

        string headline = level switch
        {
            MemoryPressureLevel.Critical => "MEMORY CRITICAL",
            MemoryPressureLevel.Elevated => "MEMORY HIGH",
            _ => "MEMORY OK"
        };

        string accent = level switch
        {
            MemoryPressureLevel.Critical => AccentCritical,
            MemoryPressureLevel.Elevated => AccentElevated,
            _ => AccentNormal
        };

        string detail =
            $"{Format(m.CommitTotalBytes)} of {Format(m.CommitLimitBytes)} committed " +
            $"({m.CommitUsedFraction:P0}), {Format(m.PhysicalAvailableBytes)} physical free";

        var tooltip = new System.Text.StringBuilder();
        tooltip.Append($"Committed {Format(m.CommitTotalBytes)} against a limit of {Format(m.CommitLimitBytes)}, ")
               .Append($"leaving {Format(m.CommitHeadroomBytes)} of headroom. ")
               .Append($"Physical memory {Format(m.PhysicalUsedBytes)} used of {Format(m.PhysicalTotalBytes)}. ")
               .Append("Commit is the number that matters: when it reaches the limit, allocations fail however much physical memory is free.");

        if (m.HasExhaustedCommitSinceBoot)
        {
            tooltip.Append($" This machine has already reached its commit limit since boot (peak {Format(m.CommitPeakBytes)}).");
        }

        // The leak warning is about THIS process, and is independent of machine pressure: a
        // process can be leaking steadily on a machine with plenty of room, and that is exactly
        // when it is cheapest to fix.
        bool showLeak = facts.Leak is { Suspicion: LeakSuspicion.Suspected };
        string leakWarning = showLeak ? facts.Leak!.Reason : "";

        bool offerReclaim = facts.ReclaimableBuildServers is > 0
                            && level != MemoryPressureLevel.Normal;
        string reclaimLabel = offerReclaim
            ? $"Shut down {facts.ReclaimableBuildServers} idle build server(s)"
            : "";

        // The advice is built from the SAME decision that gates the action, never from the raw
        // count. Letting them diverge produces advice telling a person to do something the
        // interface is not offering - which reads as broken, and is the exact class of defect
        // this fold exists to prevent.
        string advice = BuildAdvice(level, m, showLeak, offerReclaim ? facts.ReclaimableBuildServers : null);

        return new MemoryPressureStatus(
            level,
            headline,
            detail,
            tooltip.ToString(),
            accent,
            advice,
            offerReclaim,
            reclaimLabel,
            showLeak,
            leakWarning);
    }

    private static string BuildAdvice(
        MemoryPressureLevel level,
        MachineMemoryReading m,
        bool leaking,
        int? buildServers)
    {
        var parts = new List<string>();

        if (buildServers is > 0)
        {
            // Deliberately no byte estimate. Which nodes are genuinely free is not knowable by
            // inspection - a node whose parent build has exited may have been adopted by a later
            // one - so promising a figure here would be inventing it.
            parts.Add($"Ask the {buildServers} build server(s) to exit; only the ones that are genuinely free will go.");
        }

        if (leaking)
            parts.Add("This process is retaining large objects and will keep growing until it is restarted or the retention is fixed.");

        if (level == MemoryPressureLevel.Critical)
            parts.Add($"Only {Format(m.CommitHeadroomBytes)} of commit remains; close something before allocations start failing.");
        else if (level == MemoryPressureLevel.Elevated)
            parts.Add("There is still headroom, but this is the point to reclaim rather than wait.");

        return string.Join(" ", parts);
    }

    private static string Format(long bytes)
    {
        double abs = Math.Abs(bytes);
        if (abs >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
        if (abs >= 1024L * 1024) return $"{bytes / 1024.0 / 1024:F0} MB";
        return $"{bytes / 1024.0:F0} KB";
    }
}
