using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Git;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// One row in the worktree listing, built from a <see cref="WorktreeInfo"/>. Plain display
/// data - the safety verdict is decided on the service side and this only renders it.
/// </summary>
public sealed class WorktreeRowItem
{
    public string Title { get; init; } = "";
    public string Path { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Reason { get; init; } = "";
    public bool HasDetail => Detail.Length > 0;
    public ISolidColorBrush AccentBrush { get; init; } = Brushes.Gray;
}

/// <summary>
/// The read-only Worktrees page inside the Source Control tab. Enumerates every worktree for
/// the selected repository and renders two groups - safe to reap, and needs attention - plus a
/// copy-to-clipboard report. No delete button: the reaper is a later phase. The verdict comes
/// wholly from <see cref="WorktreeInventoryService"/>; this view never re-derives it.
/// </summary>
public partial class WorktreesView : UserControl
{
    private static readonly ISolidColorBrush SafeBrush = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly ISolidColorBrush AttentionBrush = new SolidColorBrush(Color.Parse("#F59E0B"));

    private readonly WorktreeInventoryService _service = new();
    private string? _repoPath;
    private WorktreeInventory? _lastInventory;
    private bool _isLoading;

    /// <summary>Raised (on the UI thread) whenever the safe-to-reap count changes, so the host can badge the tab.</summary>
    public event Action<int>? OrphanedCountChanged;

    /// <summary>The number of worktrees currently safe to reap - the badge count.</summary>
    public int OrphanedCount { get; private set; }

    public WorktreesView()
    {
        InitializeComponent();
    }

    /// <summary>Point the view at a repository and kick off a scan. Immediate "Loading..." feedback.</summary>
    public void Attach(string repoPath)
    {
        FileLog.Write($"[WorktreesView] Attach: {repoPath}");
        _repoPath = repoPath;
        _ = RefreshAsync();
    }

    /// <summary>Clear the view when the session context goes away.</summary>
    public void Detach()
    {
        FileLog.Write("[WorktreesView] Detach");
        _repoPath = null;
        _lastInventory = null;
        SafeList.ItemsSource = null;
        NeedsList.ItemsSource = null;
        SafeSection.IsVisible = false;
        NeedsSection.IsVisible = false;
        EmptyText.IsVisible = false;
        ContentScroller.IsVisible = false;
        StatusText.Text = "Loading worktrees...";
        StatusText.IsVisible = true;
        SetOrphanedCount(0);
    }

    private void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        _ = RefreshAsync();
    }

    private async void CopyReportButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_lastInventory == null)
                return;
            var report = BuildReport(_lastInventory);
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(report);
            FileLog.Write("[WorktreesView] copied worktree report to clipboard");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreesView] CopyReportButton_Click FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Scan the repository off the UI thread, then render the result. Shows a clear message on
    /// failure rather than silently degrading.
    /// </summary>
    private async Task RefreshAsync()
    {
        var repoPath = _repoPath;
        if (string.IsNullOrWhiteSpace(repoPath) || _isLoading)
            return;

        _isLoading = true;
        StatusText.Text = "Loading worktrees...";
        StatusText.IsVisible = true;
        ContentScroller.IsVisible = false;

        try
        {
            var inventory = await Task.Run(() => _service.GetInventoryAsync(repoPath));

            // Ignore stale results if the view was re-pointed while we were scanning.
            if (_repoPath != repoPath)
                return;

            _lastInventory = inventory;
            Render(inventory);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreesView] RefreshAsync FAILED: {ex.Message}");
            StatusText.Text = $"Could not read worktrees: {ex.Message}";
            StatusText.IsVisible = true;
            ContentScroller.IsVisible = false;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void Render(WorktreeInventory inventory)
    {
        if (!inventory.Success)
        {
            StatusText.Text = $"Could not read worktrees: {inventory.Error}";
            StatusText.IsVisible = true;
            ContentScroller.IsVisible = false;
            SetOrphanedCount(0);
            return;
        }

        var safeRows = BuildSafeRows(inventory);
        var needsRows = BuildNeedsRows(inventory);

        SafeList.ItemsSource = safeRows;
        NeedsList.ItemsSource = needsRows;

        SafeCountText.Text = safeRows.Count.ToString();
        NeedsCountText.Text = needsRows.Count.ToString();
        SafeSection.IsVisible = safeRows.Count > 0;
        NeedsSection.IsVisible = needsRows.Count > 0;
        EmptyText.IsVisible = safeRows.Count == 0 && needsRows.Count == 0;

        StatusText.IsVisible = false;
        ContentScroller.IsVisible = true;

        SetOrphanedCount(inventory.SafeToReapCount);
    }

    private void SetOrphanedCount(int count)
    {
        OrphanedCount = count;
        OrphanBadgeText.Text = count.ToString();
        OrphanBadge.IsVisible = count > 0;
        OrphanedCountChanged?.Invoke(count);
    }

    // ----- pure builders (unit-tested without a UI) -----

    internal static IReadOnlyList<WorktreeRowItem> BuildSafeRows(WorktreeInventory inventory) =>
        inventory.SafeToReap.Select(w => new WorktreeRowItem
        {
            Title = TitleFor(w),
            Path = w.Path,
            Detail = "",
            Reason = w.Explanation,
            AccentBrush = SafeBrush,
        }).ToList();

    internal static IReadOnlyList<WorktreeRowItem> BuildNeedsRows(WorktreeInventory inventory) =>
        inventory.NeedsAttention.Select(w => new WorktreeRowItem
        {
            Title = TitleFor(w),
            Path = w.Path,
            Detail = DetailFor(w),
            Reason = w.Explanation,
            AccentBrush = AttentionBrush,
        }).ToList();

    private static string TitleFor(WorktreeInfo w) =>
        w.IsDetachedHead ? "(detached HEAD)" : (w.Branch ?? "(no branch)");

    /// <summary>The one-line status shown under a needs-attention worktree: dirt, or ahead/behind and open pull request.</summary>
    internal static string DetailFor(WorktreeInfo w)
    {
        if (!w.IsClean)
            return $"{w.DirtyFileCount} uncommitted file(s)";

        var parts = new List<string>();
        if (w.AheadOfMain > 0)
            parts.Add($"ahead {w.AheadOfMain}");
        if (w.BehindMain > 0)
            parts.Add($"behind {w.BehindMain}");
        if (w.HasOpenPullRequest)
            parts.Add("open pull request");
        return string.Join(", ", parts);
    }

    /// <summary>Renders the whole inventory as plain text to hand to an agent (ASCII only).</summary>
    internal static string BuildReport(WorktreeInventory inventory)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Worktree report for {inventory.RepositoryPath}");
        sb.AppendLine($"Safe to reap: {inventory.SafeToReap.Count}   Needs attention: {inventory.NeedsAttention.Count}");
        sb.AppendLine();

        sb.AppendLine("SAFE TO REAP");
        if (inventory.SafeToReap.Count == 0)
            sb.AppendLine("  (none)");
        foreach (var w in inventory.SafeToReap)
        {
            sb.AppendLine($"  {TitleFor(w)}");
            sb.AppendLine($"    path:   {w.Path}");
            sb.AppendLine($"    reason: {w.Explanation}");
        }
        sb.AppendLine();

        sb.AppendLine("NEEDS ATTENTION");
        if (inventory.NeedsAttention.Count == 0)
            sb.AppendLine("  (none)");
        foreach (var w in inventory.NeedsAttention)
        {
            var detail = DetailFor(w);
            sb.AppendLine($"  {TitleFor(w)}");
            sb.AppendLine($"    path:   {w.Path}");
            if (detail.Length > 0)
                sb.AppendLine($"    status: {detail}");
            sb.AppendLine($"    reason: {w.Explanation}");
        }

        return sb.ToString();
    }
}
