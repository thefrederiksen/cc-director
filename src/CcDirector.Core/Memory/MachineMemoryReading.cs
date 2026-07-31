using System.Runtime.InteropServices;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Memory;

/// <summary>
/// What the machine's memory actually looks like at one instant.
///
/// Every number here comes from the operating system in a single call, NOT from adding up
/// processes. That distinction is the whole reason this type exists: on the machine this was
/// built from, the process working sets summed to 46 GB while 53 GB was in use - the missing
/// 7 GB was kernel pool and driver-locked pages, which belong to no process and cannot be
/// discovered by walking a process list. A report built by summing processes silently loses
/// that memory and then reads as wrong.
/// </summary>
/// <param name="PhysicalTotalBytes">Installed physical memory.</param>
/// <param name="PhysicalAvailableBytes">Physical memory available for allocation right now.</param>
/// <param name="CommitTotalBytes">Committed memory: physical plus page file actually promised out.</param>
/// <param name="CommitLimitBytes">
/// The ceiling on commit. THIS is what kills a machine - not physical pressure. When commit
/// reaches this limit, allocations fail and applications die, however much physical is free.
/// </param>
/// <param name="CommitPeakBytes">
/// The highest commit reached since boot. A peak at or above the limit is proof the machine has
/// already run out once, which no instantaneous reading can tell you.
/// </param>
/// <param name="KernelPagedBytes">Kernel paged pool - owned by no process.</param>
/// <param name="KernelNonPagedBytes">Kernel non-paged pool - owned by no process.</param>
/// <param name="ProcessCount">Processes on the machine, for context in a report.</param>
/// <param name="ThreadCount">Threads on the machine, for context in a report.</param>
/// <param name="HandleCount">Handles on the machine, for context in a report.</param>
public sealed record MachineMemoryReading(
    long PhysicalTotalBytes,
    long PhysicalAvailableBytes,
    long CommitTotalBytes,
    long CommitLimitBytes,
    long CommitPeakBytes,
    long KernelPagedBytes,
    long KernelNonPagedBytes,
    int ProcessCount,
    int ThreadCount,
    int HandleCount)
{
    /// <summary>Physical memory in use, derived rather than summed.</summary>
    public long PhysicalUsedBytes => PhysicalTotalBytes - PhysicalAvailableBytes;

    /// <summary>Commit still available before allocations start failing.</summary>
    public long CommitHeadroomBytes => CommitLimitBytes - CommitTotalBytes;

    /// <summary>Commit used as a fraction of the limit, 0 to 1. The primary pressure signal.</summary>
    public double CommitUsedFraction =>
        CommitLimitBytes <= 0 ? 0 : (double)CommitTotalBytes / CommitLimitBytes;

    /// <summary>Physical used as a fraction of installed, 0 to 1.</summary>
    public double PhysicalUsedFraction =>
        PhysicalTotalBytes <= 0 ? 0 : (double)PhysicalUsedBytes / PhysicalTotalBytes;

    /// <summary>
    /// True when commit has already touched its ceiling at some point since boot. Distinct from
    /// current pressure: the machine may look calm now and still have died an hour ago.
    /// </summary>
    public bool HasExhaustedCommitSinceBoot =>
        CommitLimitBytes > 0 && CommitPeakBytes >= CommitLimitBytes;
}

/// <summary>
/// Reads <see cref="MachineMemoryReading"/> from the operating system.
///
/// Windows only, by design - it calls GetPerformanceInfo, which is the only source that reports
/// the commit limit. On other platforms <see cref="TryRead"/> returns false rather than guessing,
/// because a fabricated commit limit would make every downstream verdict wrong in the direction
/// of "everything is fine".
/// </summary>
public static class MachineMemoryProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint cb;
        public nint CommitTotal;
        public nint CommitLimit;
        public nint CommitPeak;
        public nint PhysicalTotal;
        public nint PhysicalAvailable;
        public nint SystemCache;
        public nint KernelTotal;
        public nint KernelPaged;
        public nint KernelNonpaged;
        public nint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(out PerformanceInformation info, uint size);

    /// <summary>True when this platform can be read at all.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Read the machine's memory state. Returns false (and a null reading) when the platform is
    /// not supported or the call fails - never a partial or invented reading.
    /// </summary>
    public static bool TryRead(out MachineMemoryReading? reading)
    {
        reading = null;
        if (!IsSupported)
            return false;

        var size = (uint)Marshal.SizeOf<PerformanceInformation>();
        if (!GetPerformanceInfo(out var info, size))
        {
            FileLog.Write($"[MachineMemoryProbe] GetPerformanceInfo FAILED: win32={Marshal.GetLastWin32Error()}");
            return false;
        }

        // Every size the call returns is in pages, not bytes.
        long page = (long)info.PageSize;
        if (page <= 0)
        {
            FileLog.Write($"[MachineMemoryProbe] TryRead FAILED: implausible page size {page}");
            return false;
        }

        reading = new MachineMemoryReading(
            PhysicalTotalBytes: (long)info.PhysicalTotal * page,
            PhysicalAvailableBytes: (long)info.PhysicalAvailable * page,
            CommitTotalBytes: (long)info.CommitTotal * page,
            CommitLimitBytes: (long)info.CommitLimit * page,
            CommitPeakBytes: (long)info.CommitPeak * page,
            KernelPagedBytes: (long)info.KernelPaged * page,
            KernelNonPagedBytes: (long)info.KernelNonpaged * page,
            ProcessCount: (int)info.ProcessCount,
            ThreadCount: (int)info.ThreadCount,
            HandleCount: (int)info.HandleCount);

        return true;
    }
}
