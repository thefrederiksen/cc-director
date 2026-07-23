using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
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
    public string Timestamp { get; init; } = "";
    public bool HasDetail => Detail.Length > 0;
    public bool HasTimestamp => Timestamp.Length > 0;
    public ISolidColorBrush AccentBrush { get; init; } = Brushes.Gray;
}

/// <summary>
/// The Worktrees page inside the Source Control tab. Enumerates every worktree for the selected
/// repository and renders two groups - safe to reap, and needs attention - plus a copy-to-clipboard
/// report and a one-button reaper for the safe set. The verdict comes wholly from
/// <see cref="WorktreeInventoryService"/>; this view never re-derives it. The reaper re-checks
/// safety immediately before acting and never removes a worktree a live session is using.
/// </summary>
public partial class WorktreesView : UserControl
{
    private static readonly ISolidColorBrush SafeBrush = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly ISolidColorBrush AttentionBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly ISolidColorBrush InUseBrush = new SolidColorBrush(Color.Parse("#3B82F6"));

    private readonly WorktreeInventoryService _service = new();
    private readonly WorktreeReaperService _reaper = new();
    private string? _repoPath;
    private WorktreeInventory? _lastInventory;
    private bool _isLoading;
    private bool _isReaping;

    /// <summary>Raised (on the UI thread) whenever the safe-to-reap count changes, so the host can badge the tab.</summary>
    public event Action<int>? OrphanedCountChanged;

    /// <summary>
    /// Supplies the live sessions on this machine (with their working directories), so a git-safe
    /// worktree a session is running in is shown as "in use" and never reaped. Best-effort: set by
    /// the host, which knows the fleet; when null or it fails, the view falls back to no session data.
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>>? LiveSessionsProvider { get; set; }

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
        InUseList.ItemsSource = null;
        SafeSection.IsVisible = false;
        NeedsSection.IsVisible = false;
        InUseSection.IsVisible = false;
        EmptyText.IsVisible = false;
        ContentScroller.IsVisible = false;
        StatusText.Text = "Loading worktrees...";
        StatusText.IsVisible = true;
        ReapButton.IsVisible = false;
        ConfirmOverlay.IsVisible = false;
        ResultBanner.IsVisible = false;
        SetOrphanedCount(0);
    }

    private void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        ResultBanner.IsVisible = false; // clear any stale reap result on an explicit refresh
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
            // Ask the host which sessions are live on this machine (best-effort) so a git-safe
            // worktree with a session in it is shown as "in use" rather than "safe to reap".
            var liveSessions = await FetchLiveSessionsAsync();
            var inventory = await Task.Run(() => _service.GetInventoryAsync(repoPath, true, liveSessions));

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
        var inUseRows = BuildInUseRows(inventory);
        var needsRows = BuildNeedsRows(inventory);

        SafeList.ItemsSource = safeRows;
        InUseList.ItemsSource = inUseRows;
        NeedsList.ItemsSource = needsRows;

        SafeCountText.Text = safeRows.Count.ToString();
        InUseCountText.Text = inUseRows.Count.ToString();
        NeedsCountText.Text = needsRows.Count.ToString();
        SafeSection.IsVisible = safeRows.Count > 0;
        InUseSection.IsVisible = inUseRows.Count > 0;
        NeedsSection.IsVisible = needsRows.Count > 0;
        EmptyText.IsVisible = safeRows.Count == 0 && inUseRows.Count == 0 && needsRows.Count == 0;

        StatusText.IsVisible = false;
        ContentScroller.IsVisible = true;

        ReapButton.Content = $"Remove {inventory.SafeToReapCount} orphaned worktree{(inventory.SafeToReapCount == 1 ? "" : "s")}";
        ReapButton.IsVisible = inventory.SafeToReapCount > 0;

        SetOrphanedCount(inventory.SafeToReapCount);
    }

    private void SetOrphanedCount(int count)
    {
        OrphanedCount = count;
        OrphanBadgeText.Text = count.ToString();
        OrphanBadge.IsVisible = count > 0;
        OrphanedCountChanged?.Invoke(count);
    }

    // ----- reaper -----

    /// <summary>Show the confirmation overlay listing exactly what will be removed.</summary>
    private void ReapButton_Click(object? sender, RoutedEventArgs e)
    {
        // Worktrees a live session is running in are already classified out of the safe set, so
        // the safe list is exactly what the button removes.
        if (_lastInventory is not { Success: true } inventory || inventory.SafeToReapCount == 0)
            return;

        var toRemove = inventory.SafeToReap.ToList();
        ConfirmTitle.Text = $"Remove {toRemove.Count} orphaned worktree{(toRemove.Count == 1 ? "" : "s")}?";
        ConfirmList.Text = string.Join(Environment.NewLine,
            toRemove.Select(w => $"{(w.Branch ?? "(detached HEAD)")}  ->  {w.Path}"));
        ConfirmOverlay.IsVisible = true;
    }

    private void ConfirmCancelButton_Click(object? sender, RoutedEventArgs e)
    {
        ConfirmOverlay.IsVisible = false;
    }

    private async void ConfirmRemoveButton_Click(object? sender, RoutedEventArgs e)
    {
        ConfirmOverlay.IsVisible = false;
        await RunReapAsync();
    }

    private async Task RunReapAsync()
    {
        var repoPath = _repoPath;
        if (string.IsNullOrWhiteSpace(repoPath) || _isReaping)
            return;

        _isReaping = true;
        ReapButton.IsEnabled = false;
        StatusText.Text = "Removing worktrees...";
        StatusText.IsVisible = true;
        ContentScroller.IsVisible = false;
        ResultBanner.IsVisible = false;

        try
        {
            // Re-fetch live sessions at the moment of acting so a session opened since the scan is
            // still protected (belt-and-suspenders on top of the detector's classification).
            var liveSessions = await FetchLiveSessionsAsync();
            var protectedPaths = ToProtectedSet(liveSessions);
            var result = await Task.Run(() => _reaper.ReapAsync(repoPath, protectedPaths));

            if (_repoPath != repoPath)
                return;

            // Re-scan first so the counts, badge, and listing reflect what actually happened,
            // then lay the result banner on top of the refreshed view.
            _isReaping = false;
            await RefreshAsync();
            ShowReapResult(result);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreesView] RunReapAsync FAILED: {ex.Message}");
            await RefreshAsync();
            ShowBanner($"Reap failed: {ex.Message}");
        }
        finally
        {
            _isReaping = false;
            ReapButton.IsEnabled = true;
        }
    }

    private void ShowReapResult(ReapResult result)
    {
        if (!string.IsNullOrEmpty(result.Error))
        {
            ShowBanner($"Reap failed: {result.Error}");
            return;
        }

        var parts = new List<string> { $"Removed {result.RemovedCount} worktree(s)." };
        if (result.Skipped.Count > 0)
            parts.Add($"Skipped {result.Skipped.Count} in use by a live session.");
        if (result.Leftovers.Count > 0)
        {
            parts.Add($"Could NOT fully delete {result.Leftovers.Count} folder(s) (files locked); they remain and will be retried:");
            parts.AddRange(result.Leftovers);
        }

        // Only surface the banner when there is something noteworthy beyond a clean removal.
        if (result.Skipped.Count > 0 || result.Leftovers.Count > 0)
            ShowBanner(string.Join(Environment.NewLine, parts));
        else
            ResultBanner.IsVisible = false;
    }

    private void ShowBanner(string message)
    {
        ResultBannerText.Text = message;
        ResultBanner.IsVisible = true;
    }

    /// <summary>Fetch live sessions from the host (best-effort). Never throws - falls back to none.</summary>
    private async Task<IReadOnlyList<LiveSessionRef>> FetchLiveSessionsAsync()
    {
        try
        {
            if (LiveSessionsProvider is { } provider)
                return await provider(CancellationToken.None) ?? Array.Empty<LiveSessionRef>();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreesView] FetchLiveSessionsAsync FAILED (proceeding without session data): {ex.Message}");
        }
        return Array.Empty<LiveSessionRef>();
    }

    private static IReadOnlySet<string> ToProtectedSet(IReadOnlyList<LiveSessionRef> liveSessions)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in liveSessions)
            if (!string.IsNullOrWhiteSpace(s.RepoPath))
                set.Add(WorktreeReaperService.NormalizePath(s.RepoPath));
        return set;
    }

    // ----- pure builders (unit-tested without a UI) -----

    internal static IReadOnlyList<WorktreeRowItem> BuildSafeRows(WorktreeInventory inventory) =>
        inventory.SafeToReap.Select(w => new WorktreeRowItem
        {
            Title = TitleFor(w),
            Path = w.Path,
            Detail = "",
            Reason = w.Explanation,
            Timestamp = FormatActivity(w.LastActivityUtc),
            AccentBrush = SafeBrush,
        }).ToList();

    internal static IReadOnlyList<WorktreeRowItem> BuildInUseRows(WorktreeInventory inventory) =>
        inventory.InUseBySession.Select(w => new WorktreeRowItem
        {
            Title = TitleFor(w),
            Path = w.Path,
            Detail = SessionsFor(w),
            Reason = w.Explanation,
            Timestamp = FormatActivity(w.LastActivityUtc),
            AccentBrush = InUseBrush,
        }).ToList();

    internal static IReadOnlyList<WorktreeRowItem> BuildNeedsRows(WorktreeInventory inventory) =>
        inventory.NeedsAttention.Select(w => new WorktreeRowItem
        {
            Title = TitleFor(w),
            Path = w.Path,
            Detail = DetailFor(w),
            Reason = w.Explanation,
            Timestamp = FormatActivity(w.LastActivityUtc),
            AccentBrush = AttentionBrush,
        }).ToList();

    /// <summary>"Open session: A, B" for the in-use group.</summary>
    internal static string SessionsFor(WorktreeInfo w) =>
        w.OpenSessions.Count == 0
            ? ""
            : $"Open session{(w.OpenSessions.Count == 1 ? "" : "s")}: {string.Join(", ", w.OpenSessions)}";

    private static string TitleFor(WorktreeInfo w) =>
        w.IsDetachedHead ? "(detached HEAD)" : (w.Branch ?? "(no branch)");

    /// <summary>"Last activity: 2026-07-23 12:34" in local time, or empty when unknown.</summary>
    internal static string FormatActivity(DateTime? lastActivityUtc)
    {
        if (lastActivityUtc is not { } utc)
            return "";
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        return $"Last activity: {local:yyyy-MM-dd HH:mm}";
    }

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
        sb.AppendLine($"Safe to reap: {inventory.SafeToReap.Count}   In use by a session: {inventory.InUseBySession.Count}   Needs attention: {inventory.NeedsAttention.Count}");
        sb.AppendLine();

        sb.AppendLine("SAFE TO REAP");
        if (inventory.SafeToReap.Count == 0)
            sb.AppendLine("  (none)");
        foreach (var w in inventory.SafeToReap)
        {
            sb.AppendLine($"  {TitleFor(w)}");
            sb.AppendLine($"    path:   {w.Path}");
            AppendActivity(sb, w);
            sb.AppendLine($"    reason: {w.Explanation}");
        }
        sb.AppendLine();

        sb.AppendLine("IN USE BY AN OPEN SESSION");
        if (inventory.InUseBySession.Count == 0)
            sb.AppendLine("  (none)");
        foreach (var w in inventory.InUseBySession)
        {
            var sessions = SessionsFor(w);
            sb.AppendLine($"  {TitleFor(w)}");
            sb.AppendLine($"    path:   {w.Path}");
            AppendActivity(sb, w);
            if (sessions.Length > 0)
                sb.AppendLine($"    {sessions.ToLowerInvariant()}");
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
            AppendActivity(sb, w);
            if (detail.Length > 0)
                sb.AppendLine($"    status: {detail}");
            sb.AppendLine($"    reason: {w.Explanation}");
        }

        return sb.ToString();
    }

    private static void AppendActivity(StringBuilder sb, WorktreeInfo w)
    {
        var activity = FormatActivity(w.LastActivityUtc);
        if (activity.Length > 0)
            sb.AppendLine($"    {activity.ToLowerInvariant()}");
    }
}
