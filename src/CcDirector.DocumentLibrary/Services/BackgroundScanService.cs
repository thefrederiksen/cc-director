using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.DocumentLibrary.Models;

namespace CcDirector.DocumentLibrary.Services;

/// <summary>
/// Manages background cc-vault scan/summarize processes with streaming progress.
/// Reads JSON lines from cc-vault stdout for real-time progress updates.
/// </summary>
public sealed class BackgroundScanService : IDisposable
{
    private readonly ConcurrentDictionary<string, ScanState> _activeScans = new();
    private readonly VaultCatalogClient _client = new();
    private bool _disposed;

    /// <summary>Fires on background thread when scan progress changes.</summary>
    public event Action<string, ScanProgress>? ScanProgressChanged;

    /// <summary>Fires on background thread when scan completes successfully.</summary>
    public event Action<string>? ScanCompleted;

    /// <summary>Fires on background thread when scan fails.</summary>
    public event Action<string, string>? ScanFailed;

    /// <summary>True if any library is currently being scanned.</summary>
    public bool HasActiveScans => !_activeScans.IsEmpty;

    /// <summary>Check if a specific library is currently scanning.</summary>
    public bool IsScanning(string label) => _activeScans.ContainsKey(label);

    /// <summary>Get current progress for a library, or null if not scanning.</summary>
    public ScanProgress? GetProgress(string label) =>
        _activeScans.TryGetValue(label, out var state) ? state.Progress : null;

    /// <summary>
    /// Start a background scan + summarize for the given library.
    /// Returns immediately. Progress is reported via events.
    /// </summary>
    public void ScheduleScan(string label)
    {
        FileLog.Write($"[BackgroundScanService] ScheduleScan: {label}");

        if (_activeScans.ContainsKey(label))
        {
            FileLog.Write($"[BackgroundScanService] ScheduleScan: {label} already running, skipping");
            return;
        }

        var cts = new CancellationTokenSource();
        var progress = new ScanProgress { Phase = "scan" };
        var task = Task.Run(() => RunScanAsync(label, progress, cts.Token));
        var state = new ScanState(cts, task, progress);

        if (!_activeScans.TryAdd(label, state))
        {
            FileLog.Write($"[BackgroundScanService] ScheduleScan: {label} race condition, disposing CTS");
            cts.Dispose();
            return;
        }
    }

    /// <summary>Cancel an active scan for the given library.</summary>
    public void CancelScan(string label)
    {
        FileLog.Write($"[BackgroundScanService] CancelScan: {label}");
        if (_activeScans.TryGetValue(label, out var state))
        {
            state.Cts.Cancel();
        }
    }

    private async Task RunScanAsync(string label, ScanProgress progress, CancellationToken ct)
    {
        FileLog.Write($"[BackgroundScanService] RunScanAsync: {label} starting scan phase");
        try
        {
            // Phase 1: Scan
            progress.Phase = "scan";
            await foreach (var evt in _client.ScanLibraryAsync(label, ct))
            {
                UpdateProgressFromEvent(progress, evt);
                ScanProgressChanged?.Invoke(label, progress);
            }

            if (ct.IsCancellationRequested)
            {
                FileLog.Write($"[BackgroundScanService] RunScanAsync: {label} cancelled during scan");
                return;
            }

            FileLog.Write($"[BackgroundScanService] RunScanAsync: {label} scan complete, starting summarize phase");

            // Phase 2: Summarize
            progress.Phase = "summarize";
            progress.Processed = 0;
            progress.Total = 0;
            progress.CurrentFile = null;
            ScanProgressChanged?.Invoke(label, progress);

            await foreach (var evt in _client.SummarizeAsync(label, 50, ct))
            {
                UpdateProgressFromEvent(progress, evt);
                ScanProgressChanged?.Invoke(label, progress);
            }

            if (ct.IsCancellationRequested)
            {
                FileLog.Write($"[BackgroundScanService] RunScanAsync: {label} cancelled during summarize");
                return;
            }

            FileLog.Write($"[BackgroundScanService] RunScanAsync: {label} completed successfully");
            ScanCompleted?.Invoke(label);
        }
        catch (OperationCanceledException)
        {
            FileLog.Write($"[BackgroundScanService] RunScanAsync: {label} cancelled");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BackgroundScanService] RunScanAsync FAILED: {label} - {ex.Message}");
            ScanFailed?.Invoke(label, ex.Message);
        }
        finally
        {
            if (_activeScans.TryRemove(label, out var removed))
            {
                removed.Cts.Dispose();
            }
        }
    }

    private static void UpdateProgressFromEvent(ScanProgress progress, StreamEvent evt)
    {
        if (evt.Event == "progress")
        {
            progress.Processed = evt.Processed;
            progress.Total = evt.Total;
            progress.CurrentFile = evt.File;
        }
        else if (evt.Event == "complete")
        {
            progress.NewCount = evt.New;
            progress.SkippedCount = evt.Skipped;
            progress.ErrorCount = evt.Errors;
            progress.SummarizedCount = evt.Summarized;
            // Set processed = total on completion
            if (evt.Total > 0)
            {
                progress.Processed = evt.Total;
                progress.Total = evt.Total;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FileLog.Write("[BackgroundScanService] Dispose: cancelling all active scans");

        foreach (var kvp in _activeScans)
        {
            kvp.Value.Cts.Cancel();
        }

        // Wait briefly for processes to exit
        var tasks = _activeScans.Values.Select(s => s.RunTask).ToArray();
        if (tasks.Length > 0)
        {
            Task.WaitAll(tasks, TimeSpan.FromSeconds(3));
        }

        foreach (var kvp in _activeScans)
        {
            kvp.Value.Cts.Dispose();
        }

        _activeScans.Clear();
    }

    private sealed record ScanState(CancellationTokenSource Cts, Task RunTask, ScanProgress Progress);
}

/// <summary>Progress state for a library scan/summarize operation.</summary>
public sealed class ScanProgress
{
    public string Phase { get; set; } = "scan";
    public int Processed { get; set; }
    public int Total { get; set; }
    public string? CurrentFile { get; set; }
    public int NewCount { get; set; }
    public int SkippedCount { get; set; }
    public int ErrorCount { get; set; }
    public int SummarizedCount { get; set; }
}
