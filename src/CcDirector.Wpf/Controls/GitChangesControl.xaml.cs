using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CcDirector.Core.Git;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf.Controls;

public abstract class GitTreeNode
{
    public string DisplayName { get; set; } = "";
}

public class GitFolderNode : GitTreeNode
{
    public string RelativePath { get; set; } = "";
    public ObservableCollection<GitTreeNode> Children { get; } = new();
}

public class GitFileLeafNode : GitTreeNode
{
    public string FolderPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string StatusChar { get; set; } = "";
    public SolidColorBrush StatusBrush { get; set; } = null!;
}

public partial class GitChangesControl : UserControl
{
    private static readonly SolidColorBrush BrushModified = Freeze(new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00)));
    private static readonly SolidColorBrush BrushAdded = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));
    private static readonly SolidColorBrush BrushDeleted = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));
    private static readonly SolidColorBrush BrushRenamed = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)));
    private static readonly SolidColorBrush BrushUntracked = Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));
    private static readonly SolidColorBrush BrushDefault = Freeze(new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)));

    private readonly GitStatusProvider _provider = new();
    private readonly GitSyncStatusProvider _syncProvider = new();
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _syncTimer;
    private DateTime _lastFetchTime = DateTime.MinValue;
    private string? _repoPath;
    private string? _lastRawOutput;

    /// <summary>Raised when the user requests to view a markdown file in the built-in viewer.</summary>
    public event Action<string>? ViewMarkdownRequested;

    public GitChangesControl()
    {
        InitializeComponent();
    }

    public void Attach(string repoPath)
    {
        Detach();
        _repoPath = repoPath;

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();

        _syncTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _syncTimer.Tick += SyncTimer_Tick;
        _syncTimer.Start();

        _ = RefreshAsync();
        _ = RefreshSyncAsync(fetch: true);
    }

    public void Detach()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
        _syncTimer?.Stop();
        _syncTimer = null;
        _repoPath = null;
        _lastRawOutput = null;

        StagedTree.ItemsSource = null;
        ChangesTree.ItemsSource = null;
        StagedSection.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Visible;
        BranchBar.Visibility = Visibility.Collapsed;
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            // Skip polling when the control is not visible to the user
            if (!IsVisible) return;

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitChangesControl] PollTimer_Tick FAILED: {ex.Message}");
        }
    }

    private async void SyncTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            bool shouldFetch = (DateTime.UtcNow - _lastFetchTime).TotalSeconds >= 60;
            await RefreshSyncAsync(fetch: shouldFetch);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitChangesControl] SyncTimer_Tick FAILED: {ex.Message}");
        }
    }

    private async Task RefreshSyncAsync(bool fetch = false)
    {
        if (_repoPath == null || !Directory.Exists(_repoPath)) return;

        if (fetch)
        {
            _lastFetchTime = DateTime.UtcNow;
            await _syncProvider.FetchAsync(_repoPath);
        }

        var status = await _syncProvider.GetSyncStatusAsync(_repoPath);
        if (!status.Success)
        {
            BranchBar.Visibility = Visibility.Collapsed;
            return;
        }

        BranchBar.Visibility = Visibility.Visible;

        // Branch name
        BranchNameText.Text = status.IsDetachedHead ? "(detached HEAD)" : status.BranchName;

        // No upstream hint
        NoUpstreamText.Visibility = !status.IsDetachedHead && !status.HasUpstream
            ? Visibility.Visible : Visibility.Collapsed;

        // Ahead badge
        if (status.AheadCount > 0)
        {
            AheadBadge.Visibility = Visibility.Visible;
            AheadText.Text = $"\u2191{status.AheadCount}";
        }
        else
        {
            AheadBadge.Visibility = Visibility.Collapsed;
        }

        // Behind badge
        if (status.BehindCount > 0)
        {
            BehindBadge.Visibility = Visibility.Visible;
            BehindText.Text = $"\u2193{status.BehindCount}";
        }
        else
        {
            BehindBadge.Visibility = Visibility.Collapsed;
        }

        // Behind main badge
        if (status.BehindMainCount > 0)
        {
            BehindMainBadge.Visibility = Visibility.Visible;
            BehindMainText.Text = $"{status.MainBranchName} \u2193{status.BehindMainCount}";
        }
        else
        {
            BehindMainBadge.Visibility = Visibility.Collapsed;
        }
    }

    private async Task RefreshAsync()
    {
        if (_repoPath == null || !Directory.Exists(_repoPath)) return;

        var result = await _provider.GetStatusAsync(_repoPath);
        if (!result.Success) return;

        // Skip expensive tree rebuild if git output hasn't changed
        var rawOutput = _provider.GetCachedRawOutput(_repoPath);
        if (rawOutput != null && rawOutput == _lastRawOutput)
            return;
        _lastRawOutput = rawOutput;

        var stagedNodes = BuildTree(result.StagedChanges);
        var unstagedNodes = BuildTree(result.UnstagedChanges);

        StagedTree.ItemsSource = stagedNodes;
        ChangesTree.ItemsSource = unstagedNodes;

        if (result.StagedChanges.Count > 0)
        {
            StagedSection.Visibility = Visibility.Visible;
            StagedBadge.Text = result.StagedChanges.Count.ToString();
        }
        else
        {
            StagedSection.Visibility = Visibility.Collapsed;
        }

        ChangesBadge.Text = result.UnstagedChanges.Count.ToString();
        EmptyText.Visibility = result.StagedChanges.Count == 0 && result.UnstagedChanges.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    internal static List<GitTreeNode> BuildTree(IReadOnlyList<GitFileEntry> files)
    {
        var root = new GitFolderNode();
        // Dictionary lookup per folder level for O(1) child resolution instead of linear scan
        var folderLookup = new Dictionary<GitFolderNode, Dictionary<string, GitFolderNode>>();
        folderLookup[root] = new Dictionary<string, GitFolderNode>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var dir = Path.GetDirectoryName(file.FilePath)?.Replace('/', '\\') ?? "";
            var segments = string.IsNullOrEmpty(dir)
                ? Array.Empty<string>()
                : dir.Split('\\');

            var current = root;
            var pathSoFar = "";
            foreach (var segment in segments)
            {
                pathSoFar = pathSoFar.Length == 0 ? segment : pathSoFar + "\\" + segment;
                var childMap = folderLookup[current];
                if (!childMap.TryGetValue(segment, out var existing))
                {
                    existing = new GitFolderNode { DisplayName = segment, RelativePath = pathSoFar };
                    current.Children.Add(existing);
                    childMap[segment] = existing;
                    folderLookup[existing] = new Dictionary<string, GitFolderNode>(StringComparer.Ordinal);
                }
                current = existing;
            }

            current.Children.Add(new GitFileLeafNode
            {
                DisplayName = file.FileName,
                FolderPath = dir,
                RelativePath = file.FilePath,
                StatusChar = file.Status == GitFileStatus.Untracked ? "U" : file.StatusChar,
                StatusBrush = GetStatusBrush(file.Status)
            });
        }

        CompactFolders(root);
        return [.. root.Children];
    }

    internal static void CompactFolders(GitFolderNode folder)
    {
        for (int i = 0; i < folder.Children.Count; i++)
        {
            if (folder.Children[i] is not GitFolderNode child) continue;

            // Merge single-child folder chains: src > controls -> src\controls
            while (child.Children.Count == 1 && child.Children[0] is GitFolderNode grandchild)
            {
                child.DisplayName = child.DisplayName + "\\" + grandchild.DisplayName;
                child.RelativePath = grandchild.RelativePath;
                var items = grandchild.Children.ToList();
                child.Children.Clear();
                foreach (var item in items)
                    child.Children.Add(item);
            }

            CompactFolders(child);
        }
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush GetStatusBrush(GitFileStatus status) => status switch
    {
        GitFileStatus.Modified => BrushModified,
        GitFileStatus.Added => BrushAdded,
        GitFileStatus.Deleted => BrushDeleted,
        GitFileStatus.Renamed => BrushRenamed,
        GitFileStatus.Copied => BrushRenamed,
        GitFileStatus.Untracked => BrushUntracked,
        _ => BrushDefault
    };

    internal void FileNode_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (e.ClickCount == 2 && sender is FrameworkElement fe && fe.DataContext is GitFileLeafNode node)
            {
                // Open .md files in built-in viewer, everything else in VS Code
                if (IsMarkdownFile(node.RelativePath))
                    RaiseViewMarkdown(node.RelativePath);
                else
                    OpenFileInVsCode(node.RelativePath);
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitChangesControl] FileNode_MouseLeftButtonDown FAILED: {ex.Message}");
        }
    }

    internal void FileContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is ContextMenu menu &&
                menu.PlacementTarget is FrameworkElement fe &&
                fe.DataContext is GitFileLeafNode node)
            {
                // Show "View Markdown" only for .md files
                foreach (var item in menu.Items)
                {
                    if (item is MenuItem mi && mi.Header is string header && header == "View Markdown")
                    {
                        mi.Visibility = IsMarkdownFile(node.RelativePath)
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitChangesControl] FileContextMenu_Opened FAILED: {ex.Message}");
        }
    }

    internal void FileNode_ViewMarkdown_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (GetNodeFromMenuItem(sender) is GitFileLeafNode node)
                RaiseViewMarkdown(node.RelativePath);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitChangesControl] FileNode_ViewMarkdown_Click FAILED: {ex.Message}");
        }
    }

    internal void FileNode_OpenInVsCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (GetNodeFromMenuItem(sender) is GitFileLeafNode node)
                OpenFileInVsCode(node.RelativePath);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitChangesControl] FileNode_OpenInVsCode_Click FAILED: {ex.Message}");
        }
    }

    internal void FileNode_CopyFullPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_repoPath != null && GetNodeFromMenuItem(sender) is GitFileLeafNode node)
            {
                var fullPath = Path.Combine(_repoPath, node.RelativePath);
                Clipboard.SetText(fullPath);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitChangesControl] FileNode_CopyFullPath_Click FAILED: {ex.Message}");
        }
    }

    internal void FileNode_CopyRelativePath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (GetNodeFromMenuItem(sender) is GitFileLeafNode node)
                Clipboard.SetText(node.RelativePath);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitChangesControl] FileNode_CopyRelativePath_Click FAILED: {ex.Message}");
        }
    }

    internal async void FileNode_AddToGitignore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_repoPath != null && GetNodeFromMenuItem(sender) is GitFileLeafNode node)
            {
                FileLog.Write($"[GitChangesControl] FileNode_AddToGitignore_Click: {node.RelativePath}");
                var added = await GitIgnoreService.AddEntryAsync(_repoPath, node.RelativePath);
                if (added)
                {
                    GitStatusProvider.InvalidateCache(_repoPath);
                    _lastRawOutput = null;
                    await RefreshAsync();
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitChangesControl] FileNode_AddToGitignore_Click FAILED: {ex.Message}");
        }
    }

    internal async void FolderNode_AddToGitignore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_repoPath != null && GetFolderNodeFromMenuItem(sender) is GitFolderNode node)
            {
                var entry = node.RelativePath + "/";
                FileLog.Write($"[GitChangesControl] FolderNode_AddToGitignore_Click: {entry}");
                var added = await GitIgnoreService.AddEntryAsync(_repoPath, entry);
                if (added)
                {
                    GitStatusProvider.InvalidateCache(_repoPath);
                    _lastRawOutput = null;
                    await RefreshAsync();
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitChangesControl] FolderNode_AddToGitignore_Click FAILED: {ex.Message}");
        }
    }

    private static GitFileLeafNode? GetNodeFromMenuItem(object sender)
    {
        if (sender is MenuItem mi && mi.Parent is ContextMenu cm
            && cm.PlacementTarget is FrameworkElement fe
            && fe.DataContext is GitFileLeafNode node)
        {
            return node;
        }
        return null;
    }

    private static GitFolderNode? GetFolderNodeFromMenuItem(object sender)
    {
        if (sender is MenuItem mi && mi.Parent is ContextMenu cm
            && cm.PlacementTarget is FrameworkElement fe
            && fe.DataContext is GitFolderNode node)
        {
            return node;
        }
        return null;
    }

    private void OpenFileInVsCode(string relativePath)
    {
        if (_repoPath == null || string.IsNullOrEmpty(relativePath)) return;
        var fullPath = Path.Combine(_repoPath, relativePath);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "code",
                Arguments = $"--goto \"{fullPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitChangesControl] OpenFileInVsCode FAILED for {relativePath}: {ex.Message}");
        }
    }

    private void RaiseViewMarkdown(string relativePath)
    {
        if (_repoPath == null || string.IsNullOrEmpty(relativePath)) return;
        var fullPath = Path.Combine(_repoPath, relativePath);
        FileLog.Write($"[GitChangesControl] RaiseViewMarkdown: {fullPath}");
        ViewMarkdownRequested?.Invoke(fullPath);
    }

    private static bool IsMarkdownFile(string path) => Helpers.FileExtensions.IsMarkdown(path);
}
