using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Git;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>One repository row, built from a <see cref="RepositoryStatus"/> - plain display data.</summary>
public sealed class RepoRowItem
{
    public string Name { get; init; } = "";
    public string SubPath { get; init; } = "";
    public string Provider { get; init; } = "";
    public bool HasProvider => Provider.Length > 0;
    public string Where { get; init; } = "";
    public string Sync { get; init; } = "";
    public string Worktrees { get; init; } = "";

    public ISolidColorBrush WhereFg { get; init; } = Brushes.Gray;
    public ISolidColorBrush WhereBg { get; init; } = Brushes.Transparent;
    public ISolidColorBrush WhereBr { get; init; } = Brushes.Gray;
    public ISolidColorBrush WorktreeFg { get; init; } = Brushes.Gray;
    public ISolidColorBrush WorktreeBg { get; init; } = Brushes.Transparent;
    public ISolidColorBrush WorktreeBr { get; init; } = Brushes.Gray;

    public string Path { get; init; } = "";
}

/// <summary>
/// The Repository list: every repository on disk under the configured root directories, with its
/// where/dirty state, sync, and worktree summary. Renders the verdict computed by
/// <see cref="RepositoryStatusService"/>; passes live sessions through so a worktree a session is in
/// is counted as "in use", not "safe".
/// </summary>
public partial class RepositoryListView : UserControl
{
    private static readonly ISolidColorBrush Green = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly ISolidColorBrush GreenBg = new SolidColorBrush(Color.Parse("#1B3A2A"));
    private static readonly ISolidColorBrush GreenBr = new SolidColorBrush(Color.Parse("#1E5138"));
    private static readonly ISolidColorBrush Amber = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly ISolidColorBrush AmberBg = new SolidColorBrush(Color.Parse("#3A2A1B"));
    private static readonly ISolidColorBrush AmberBr = new SolidColorBrush(Color.Parse("#5A4326"));
    private static readonly ISolidColorBrush Muted = new SolidColorBrush(Color.Parse("#8A8A8A"));
    private static readonly ISolidColorBrush MutedBg = new SolidColorBrush(Color.Parse("#2D2D30"));
    private static readonly ISolidColorBrush MutedBr = new SolidColorBrush(Color.Parse("#3C3C3C"));

    private RepositoryMonitor? _monitor;

    /// <summary>Raised when the user clicks Refresh; the host triggers the monitor to rescan.</summary>
    public event Action? RefreshRequested;

    public RepositoryListView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Subscribe to the background monitor and render its current model. The view never scans - it
    /// displays what the service already knows and updates as results stream in.
    /// </summary>
    public void Attach(RepositoryMonitor monitor)
    {
        FileLog.Write("[RepositoryListView] Attach");
        Detach();
        _monitor = monitor;
        monitor.Upserted += OnModelChanged;
        monitor.Removed += OnModelChanged;
        monitor.ProgressChanged += OnProgressChanged;
        RenderFromMonitor();
    }

    public void Detach()
    {
        if (_monitor is { } m)
        {
            m.Upserted -= OnModelChanged;
            m.Removed -= OnModelChanged;
            m.ProgressChanged -= OnProgressChanged;
            _monitor = null;
        }
    }

    private void OnModelChanged(RepositoryStatus _) => Dispatcher.UIThread.Post(RenderFromMonitor);
    private void OnProgressChanged() => Dispatcher.UIThread.Post(RenderFromMonitor);

    private void RefreshButton_Click(object? sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    private void RenderFromMonitor()
    {
        if (_monitor is null)
            return;

        var statuses = _monitor.Snapshot();
        RepoList.ItemsSource = BuildRows(statuses);

        bool scanning = _monitor.IsScanning;
        if (statuses.Count == 0)
        {
            StatusText.Text = scanning
                ? "Scanning repositories..."
                : "No repositories found under the configured root directories.";
            StatusText.IsVisible = true;
            ContentScroller.IsVisible = false;
        }
        else
        {
            StatusText.IsVisible = false;
            ContentScroller.IsVisible = true;
        }

        SummaryText.Text = scanning
            ? $"Scanning... {_monitor.ScanDone} of {_monitor.ScanTotal}"
            : BuildSummary(statuses);
    }

    // ----- pure builders (unit-tested without a UI) -----

    internal static IReadOnlyList<RepoRowItem> BuildRows(IReadOnlyList<RepositoryStatus> statuses) =>
        statuses
            .OrderByDescending(s => s.WorktreesSafeToReap)
            .ThenByDescending(s => s.UncommittedCount)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToRow)
            .ToList();

    private static RepoRowItem ToRow(RepositoryStatus s)
    {
        bool dirty = !s.IsClean;
        bool hasReap = s.WorktreesSafeToReap > 0;
        return new RepoRowItem
        {
            Name = s.Name,
            SubPath = SubPathFor(s),
            Provider = ProviderLabel(s.Provider),
            Where = WhereText(s),
            Sync = SyncText(s),
            Worktrees = WorktreeText(s),
            WhereFg = dirty ? Amber : Green,
            WhereBg = dirty ? AmberBg : GreenBg,
            WhereBr = dirty ? AmberBr : GreenBr,
            WorktreeFg = hasReap ? Green : Muted,
            WorktreeBg = hasReap ? GreenBg : MutedBg,
            WorktreeBr = hasReap ? GreenBr : MutedBr,
            Path = s.Path,
        };
    }

    internal static string ProviderLabel(RepoProvider p) => p switch
    {
        RepoProvider.GitHub => "GitHub",
        RepoProvider.AzureDevOps => "Azure DevOps",
        RepoProvider.Other => "Git",
        _ => "local",
    };

    private static string SubPathFor(RepositoryStatus s) =>
        s.Org is { Length: > 0 } ? $"{s.Org} · {s.Path}" : s.Path;

    internal static string WhereText(RepositoryStatus s) =>
        s.IsClean ? "clean" : $"{s.UncommittedCount} uncommitted";

    internal static string SyncText(RepositoryStatus s)
    {
        if (s.IsDetachedHead)
            return "detached HEAD";

        var parts = new List<string>();
        if (s.AheadCount > 0) parts.Add($"ahead {s.AheadCount}");
        if (s.BehindCount > 0) parts.Add($"behind {s.BehindCount}");
        if (s.BehindMainCount > 0) parts.Add($"behind main {s.BehindMainCount}");
        return parts.Count == 0 ? "up to date" : string.Join(", ", parts);
    }

    internal static string WorktreeText(RepositoryStatus s)
    {
        if (s.WorktreeCount == 0)
            return "no worktrees";

        var parts = new List<string> { $"{s.WorktreeCount} worktree{(s.WorktreeCount == 1 ? "" : "s")}" };
        if (s.WorktreesSafeToReap > 0) parts.Add($"{s.WorktreesSafeToReap} safe");
        if (s.WorktreesInUse > 0) parts.Add($"{s.WorktreesInUse} in use");
        if (s.WorktreesNeedAttention > 0) parts.Add($"{s.WorktreesNeedAttention} attention");
        return string.Join(" · ", parts);
    }

    internal static string BuildSummary(IReadOnlyList<RepositoryStatus> statuses)
    {
        int repos = statuses.Count;
        int dirty = statuses.Count(s => !s.IsClean);
        int reap = statuses.Sum(s => s.WorktreesSafeToReap);
        return $"{repos} on disk · {dirty} with uncommitted work · {reap} worktrees to reap";
    }
}
