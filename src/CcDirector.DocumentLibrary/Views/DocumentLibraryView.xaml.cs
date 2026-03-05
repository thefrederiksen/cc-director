using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CcDirector.Core.Utilities;
using CcDirector.DocumentLibrary.Models;
using CcDirector.DocumentLibrary.Services;
using CcDirector.DocumentLibrary.ViewModels;

namespace CcDirector.DocumentLibrary.Views;

public partial class DocumentLibraryView : UserControl, IDisposable
{
    private readonly DocumentLibraryViewModel _viewModel;
    private readonly BackgroundScanService _scanService = new();
    private bool _isInitialized;

    private static readonly HashSet<string> ReadableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".csv", ".json", ".xml", ".yaml", ".yml", ".log", ".ini", ".cfg", ".html", ".htm",
    };

    public DocumentLibraryView()
    {
        FileLog.Write("[DocumentLibraryView] Constructor");
        InitializeComponent();
        _viewModel = new DocumentLibraryViewModel();
        DataContext = _viewModel;

        _scanService.ScanProgressChanged += OnScanProgressChanged;
        _scanService.ScanCompleted += OnScanCompleted;
        _scanService.ScanFailed += OnScanFailed;

        Loaded += DocumentLibraryView_Loaded;
    }

    public DocumentLibraryViewModel ViewModel => _viewModel;

    /// <summary>Exposed so MainWindow can check for active scans on closing.</summary>
    public BackgroundScanService ScanService => _scanService;

    private async void DocumentLibraryView_Loaded(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[DocumentLibraryView] Loaded");
        if (_isInitialized) return;
        _isInitialized = true;
        LoadingText.Visibility = Visibility.Visible;
        await _viewModel.InitializeAsync();
        BuildTree();
        LoadingText.Visibility = Visibility.Collapsed;
    }

    public void StartPolling()
    {
        FileLog.Write("[DocumentLibraryView] StartPolling");
    }

    public void StopPolling()
    {
        FileLog.Write("[DocumentLibraryView] StopPolling");
    }

    public void Dispose()
    {
        FileLog.Write("[DocumentLibraryView] Dispose");
        _scanService.ScanProgressChanged -= OnScanProgressChanged;
        _scanService.ScanCompleted -= OnScanCompleted;
        _scanService.ScanFailed -= OnScanFailed;
        _scanService.Dispose();
        _viewModel.Dispose();
    }

    // ==================== SEARCH TAB ====================

    private void SearchGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel.SelectedSearchEntry is not null)
        {
            FileLog.Write($"[DocumentLibraryView] SearchGrid_SelectionChanged: {_viewModel.SelectedSearchEntry.FileName}");
            ShowPreviewAsync(_viewModel.SelectedSearchEntry);
        }
        else
        {
            HidePreview();
        }
    }

    private async void ShowPreviewAsync(CatalogEntry entry)
    {
        FileLog.Write($"[DocumentLibraryView] ShowPreviewAsync: {entry.FileName}");

        // Show pane immediately with metadata
        PreviewCol.Width = new GridLength(350);
        PreviewSplitterCol.Width = new GridLength(3);
        PreviewSplitter.Visibility = Visibility.Visible;
        PreviewPane.Visibility = Visibility.Visible;

        PreviewTitle.Text = entry.Title ?? entry.FileName;
        PreviewMeta.Text = $"Folder: {entry.Department ?? "-"} | Tags: {entry.Tags ?? "-"}";
        PreviewStatus.Text = $"Status: {entry.StatusDisplay} | {entry.FileSizeDisplay}";

        // Summary
        if (!string.IsNullOrEmpty(entry.Summary))
        {
            PreviewSummaryHeader.Visibility = Visibility.Visible;
            PreviewSummary.Visibility = Visibility.Visible;
            PreviewSummary.Text = entry.Summary;
        }
        else
        {
            PreviewSummaryHeader.Visibility = Visibility.Collapsed;
            PreviewSummary.Visibility = Visibility.Collapsed;
        }

        // Content preview for readable file types
        if (ReadableExtensions.Contains(entry.FileExt) && File.Exists(entry.FilePath))
        {
            PreviewContentHeader.Visibility = Visibility.Visible;
            PreviewContent.Visibility = Visibility.Visible;
            PreviewContent.Text = "Loading...";

            var content = await BuildPreviewContent(entry);
            PreviewContent.Text = content;
        }
        else
        {
            PreviewContentHeader.Visibility = Visibility.Collapsed;
            PreviewContent.Visibility = Visibility.Collapsed;
        }
    }

    private static async Task<string> BuildPreviewContent(CatalogEntry entry)
    {
        FileLog.Write($"[DocumentLibraryView] BuildPreviewContent: {entry.FilePath}");
        return await Task.Run(() =>
        {
            try
            {
                using var reader = new StreamReader(entry.FilePath);
                var buffer = new char[2000];
                var read = reader.Read(buffer, 0, buffer.Length);
                var text = new string(buffer, 0, read);
                if (reader.Peek() >= 0)
                    text += "\n\n[... truncated]";
                return text;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DocumentLibraryView] BuildPreviewContent FAILED: {ex.Message}");
                return $"Could not read file: {ex.Message}";
            }
        });
    }

    private void HidePreview()
    {
        FileLog.Write("[DocumentLibraryView] HidePreview");
        PreviewCol.Width = new GridLength(0);
        PreviewSplitterCol.Width = new GridLength(0);
        PreviewSplitter.Visibility = Visibility.Collapsed;
        PreviewPane.Visibility = Visibility.Collapsed;
    }

    private void BtnClosePreview_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[DocumentLibraryView] BtnClosePreview_Click");
        SearchGrid.SelectedItem = null;
        HidePreview();
    }

    private void SearchGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedSearchEntry is not null)
        {
            FileLog.Write($"[DocumentLibraryView] SearchGrid_DoubleClick: {_viewModel.SelectedSearchEntry.FileName}");
            _viewModel.OpenFile(_viewModel.SelectedSearchEntry);
        }
    }

    private void SearchFilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var ext = button.Tag?.ToString();
        if (string.IsNullOrEmpty(ext)) ext = null;

        FileLog.Write($"[DocumentLibraryView] SearchFilterChip_Click: {ext ?? "All"}");
        _viewModel.ActiveExtFilter = ext;

        foreach (var child in SearchFilterBar.Children)
        {
            if (child is Button chip)
            {
                var chipExt = chip.Tag?.ToString();
                chip.Style = (chipExt == (ext ?? ""))
                    ? (Style)FindResource("FilterChipActiveStyle")
                    : (Style)FindResource("FilterChipStyle");
            }
        }

        // Re-trigger search with new filter
        if (!string.IsNullOrWhiteSpace(_viewModel.SearchQuery))
            _ = _viewModel.SearchGlobalAsync(_viewModel.SearchQuery);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchPlaceholder is not null)
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    // ==================== LIBRARIES TAB ====================

    private void BuildTree()
    {
        FileLog.Write("[DocumentLibraryView] BuildTree");
        LibraryTree.Items.Clear();

        foreach (var lib in _viewModel.Libraries)
        {
            var libItem = new TreeViewItem
            {
                Tag = lib,
                IsExpanded = false,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CCCCCC")),
            };

            // Library header: "Corporate (831)" or "Corporate (47/831) - Scanning..."
            var headerText = FormatLibraryHeader(lib);
            libItem.Header = CreateTreeHeader(headerText, true);

            // Get all relative directory paths and build a hierarchical tree
            var dirs = _viewModel.GetRelativeDirectories(lib.Id, lib.Path);
            BuildDirectoryTree(libItem, lib, dirs);

            LibraryTree.Items.Add(libItem);
        }
    }

    private string FormatLibraryHeader(Library lib)
    {
        var progress = _scanService.GetProgress(lib.Label);
        if (progress is not null)
        {
            if (progress.Phase == "scan")
            {
                var totalStr = progress.Total > 0
                    ? $" ({progress.Processed}/{progress.Total}) - Scanning..."
                    : " - Scanning...";
                return lib.Label + totalStr;
            }
            else if (progress.Phase == "summarize")
            {
                var totalStr = progress.Total > 0
                    ? $" - Summarizing {progress.Processed}/{progress.Total}..."
                    : " - Summarizing...";
                return lib.Label + totalStr;
            }
        }

        var countText = lib.Stats?.Total > 0 ? $" ({lib.Stats.Total})" : "";
        return lib.Label + countText;
    }

    private static TextBlock CreateTreeHeader(string text, bool bold)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CCCCCC")),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            FontSize = bold ? 12 : 11,
        };
    }

    /// <summary>Build a multi-level folder tree from flat relative paths.</summary>
    private static void BuildDirectoryTree(TreeViewItem parent, Library lib, List<string> relativeDirs)
    {
        // Map: relative path -> TreeViewItem
        var nodeMap = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in relativeDirs)
        {
            var parts = dir.Split('/');
            var currentPath = "";

            for (var i = 0; i < parts.Length; i++)
            {
                var parentPath = currentPath;
                currentPath = i == 0 ? parts[i] : currentPath + "/" + parts[i];

                if (nodeMap.ContainsKey(currentPath))
                    continue;

                var folderItem = new TreeViewItem
                {
                    Header = CreateTreeHeader(parts[i], false),
                    Tag = new FolderTag(lib, currentPath),
                    IsExpanded = false,
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CCCCCC")),
                };

                if (i == 0)
                    parent.Items.Add(folderItem);
                else if (nodeMap.TryGetValue(parentPath, out var parentNode))
                    parentNode.Items.Add(folderItem);

                nodeMap[currentPath] = folderItem;
            }
        }
    }

    private async void LibraryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (LibraryTree.SelectedItem is not TreeViewItem item) return;

        if (item.Tag is Library lib)
        {
            // Clicked a library node -> show all files
            FileLog.Write($"[DocumentLibraryView] TreeSelection: library={lib.Label}");
            _viewModel.SelectedLibrary = lib;
            _viewModel.SelectedRelativeDir = null;
            UpdateBreadcrumb(lib.Label);
            BtnScanAndSummarize.Visibility = Visibility.Visible;
            BtnRemoveLibrary.Visibility = Visibility.Visible;
            UpdateScanProgressVisibility(lib.Label);
            item.IsExpanded = true;
            await _viewModel.LoadEntriesAsync();
        }
        else if (item.Tag is FolderTag folderTag)
        {
            // Clicked a folder -> filter to that directory
            FileLog.Write($"[DocumentLibraryView] TreeSelection: library={folderTag.Library.Label}, folder={folderTag.RelativeDir}");
            _viewModel.SelectedLibrary = folderTag.Library;
            _viewModel.SelectedRelativeDir = folderTag.RelativeDir;
            UpdateBreadcrumb($"{folderTag.Library.Label}  >  {folderTag.RelativeDir.Replace("/", "  >  ")}");
            BtnScanAndSummarize.Visibility = Visibility.Visible;
            BtnRemoveLibrary.Visibility = Visibility.Visible;
            UpdateScanProgressVisibility(folderTag.Library.Label);
            await _viewModel.LoadEntriesByDirectoryAsync(
                folderTag.Library.Id, folderTag.Library.Path, folderTag.RelativeDir);
        }
    }

    private void UpdateBreadcrumb(string text)
    {
        BreadcrumbText.Text = text;
        BreadcrumbText.Foreground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CCCCCC"));
    }

    /// <summary>Show/hide the toolbar progress bar based on whether the selected library is scanning.</summary>
    private void UpdateScanProgressVisibility(string label)
    {
        var progress = _scanService.GetProgress(label);
        if (progress is not null)
        {
            ScanProgressPanel.Visibility = Visibility.Visible;
            UpdateToolbarProgress(progress);
        }
        else
        {
            ScanProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateToolbarProgress(ScanProgress progress)
    {
        var pct = progress.Total > 0
            ? (double)progress.Processed / progress.Total * 100.0
            : 0;
        ScanProgressBar.Value = pct;

        var fileName = progress.CurrentFile is not null
            ? Path.GetFileName(progress.CurrentFile)
            : "";

        if (progress.Phase == "scan")
        {
            ScanProgressText.Text = progress.Total > 0
                ? $"{progress.Processed}/{progress.Total} - {fileName}"
                : "Scanning...";
        }
        else
        {
            ScanProgressText.Text = progress.Total > 0
                ? $"Summarizing {progress.Processed}/{progress.Total} - {fileName}"
                : "Summarizing...";
        }
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[DocumentLibraryView] BtnBack_Click");
        Visibility = Visibility.Collapsed;
    }

    private async void BtnAddLibrary_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[DocumentLibraryView] BtnAddLibrary_Click");
        var dialog = new AddLibraryDialog();
        dialog.Owner = Window.GetWindow(this);

        if (dialog.ShowDialog() == true)
        {
            FileLog.Write($"[DocumentLibraryView] AddLibrary: path={dialog.LibraryPath}, label={dialog.LibraryLabel}");
            BtnAddLibrary.IsEnabled = false;
            BreadcrumbText.Text = $"Adding '{dialog.LibraryLabel}'...";
            try
            {
                var client = new Services.VaultCatalogClient();
                await client.AddLibraryAsync(
                    dialog.LibraryPath, dialog.LibraryLabel,
                    dialog.LibraryCategory, dialog.LibraryOwner);
                await _viewModel.RefreshLibrariesAsync();
                BuildTree();
                BreadcrumbText.Text = dialog.LibraryLabel;
                _scanService.ScheduleScan(dialog.LibraryLabel);
                FileLog.Write($"[DocumentLibraryView] AddLibrary complete, scan scheduled: {dialog.LibraryLabel}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DocumentLibraryView] AddLibrary FAILED: {ex.Message}");
                BreadcrumbText.Text = "Select a library";
                MessageBox.Show(
                    $"Failed to add library: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnAddLibrary.IsEnabled = true;
            }
        }
    }

    private async void BtnRemoveLibrary_Click(object sender, RoutedEventArgs e)
    {
        var lib = _viewModel.SelectedLibrary;
        if (lib is null) return;

        FileLog.Write($"[DocumentLibraryView] BtnRemoveLibrary_Click: {lib.Label}");

        var entryCount = lib.Stats?.Total ?? 0;
        var message = $"Remove library '{lib.Label}' and all {entryCount} catalog entries?\n\nThis cannot be undone.";
        var result = MessageBox.Show(
            message, "Remove Library",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        FileLog.Write($"[DocumentLibraryView] RemoveLibrary confirmed: {lib.Label}");
        BtnRemoveLibrary.IsEnabled = false;
        BtnScanAndSummarize.IsEnabled = false;
        BreadcrumbText.Text = $"Removing '{lib.Label}'...";
        try
        {
            var client = new Services.VaultCatalogClient();
            await client.DeleteLibraryAsync(lib.Label);

            _viewModel.SelectedLibrary = null;
            _viewModel.SelectedRelativeDir = null;
            BtnScanAndSummarize.Visibility = Visibility.Collapsed;
            BtnRemoveLibrary.Visibility = Visibility.Collapsed;
            BreadcrumbText.Text = "Select a library";

            await _viewModel.RefreshLibrariesAsync();
            BuildTree();
            _viewModel.Entries.Clear();

            FileLog.Write($"[DocumentLibraryView] RemoveLibrary complete: {lib.Label}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DocumentLibraryView] RemoveLibrary FAILED: {ex.Message}");
            MessageBox.Show(
                $"Failed to remove library: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnRemoveLibrary.IsEnabled = true;
            BtnScanAndSummarize.IsEnabled = true;
        }
    }

    private void BtnScanAndSummarize_Click(object sender, RoutedEventArgs e)
    {
        var lib = _viewModel.SelectedLibrary;
        if (lib is null) return;

        FileLog.Write($"[DocumentLibraryView] BtnScanAndSummarize_Click: {lib.Label}");

        if (_scanService.IsScanning(lib.Label))
        {
            FileLog.Write($"[DocumentLibraryView] Already scanning {lib.Label}, ignoring click");
            return;
        }

        // Start background scan -- returns immediately
        _scanService.ScheduleScan(lib.Label);

        // Show progress in toolbar
        ScanProgressPanel.Visibility = Visibility.Visible;
        ScanProgressBar.Value = 0;
        ScanProgressText.Text = "Starting scan...";

        // Update tree node to show scanning state
        UpdateTreeNodeHeader(lib.Label);
    }

    // --- Background scan event handlers (called on background thread) ---

    private void OnScanProgressChanged(string label, ScanProgress progress)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // Update tree node header
            UpdateTreeNodeHeader(label);

            // Update toolbar progress if this is the currently selected library
            var selectedLabel = _viewModel.SelectedLibrary?.Label;
            if (selectedLabel == label)
            {
                ScanProgressPanel.Visibility = Visibility.Visible;
                UpdateToolbarProgress(progress);
            }
        });
    }

    private void OnScanCompleted(string label)
    {
        FileLog.Write($"[DocumentLibraryView] OnScanCompleted: {label}");
        Dispatcher.BeginInvoke(async () =>
        {
            // Hide progress bar if this is the selected library
            var selectedLabel = _viewModel.SelectedLibrary?.Label;
            if (selectedLabel == label)
            {
                ScanProgressPanel.Visibility = Visibility.Collapsed;
            }

            // Refresh libraries and rebuild tree with new stats
            await _viewModel.RefreshLibrariesAsync();
            BuildTree();

            // Reload entries if viewing the scanned library
            if (selectedLabel == label)
            {
                await _viewModel.ReloadCurrentViewAsync();
            }
        });
    }

    private void OnScanFailed(string label, string error)
    {
        FileLog.Write($"[DocumentLibraryView] OnScanFailed: {label} - {error}");
        Dispatcher.BeginInvoke(() =>
        {
            // Hide progress bar
            var selectedLabel = _viewModel.SelectedLibrary?.Label;
            if (selectedLabel == label)
            {
                ScanProgressPanel.Visibility = Visibility.Collapsed;
            }

            // Update tree node (removes scanning state)
            UpdateTreeNodeHeader(label);

            MessageBox.Show(
                $"Scan failed for {label}: {error}",
                "Scan Error", MessageBoxButton.OK, MessageBoxImage.Error);
        });
    }

    /// <summary>Find and update the tree node header for a library by label.</summary>
    private void UpdateTreeNodeHeader(string label)
    {
        foreach (TreeViewItem item in LibraryTree.Items)
        {
            if (item.Tag is Library lib && lib.Label == label)
            {
                item.Header = CreateTreeHeader(FormatLibraryHeader(lib), true);
                break;
            }
        }
    }

    private void CatalogGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // Handle sorting ourselves via the ViewModel (server-side sort)
        e.Handled = true;

        var columnMap = new Dictionary<string, string>
        {
            { "FileExt", "file_ext" },
            { "FileName", "file_name" },
            { "Department", "department" },
            { "FileSize", "file_size" },
            { "FileModifiedAt", "file_modified_at" },
            { "Status", "status" },
        };

        var sortMember = e.Column.SortMemberPath;
        if (sortMember is not null && columnMap.TryGetValue(sortMember, out var dbColumn))
        {
            FileLog.Write($"[DocumentLibraryView] CatalogGrid_Sorting: {dbColumn}");

            // Toggle direction if same column
            if (_viewModel.SortColumn == dbColumn)
            {
                // SortColumn setter toggles direction
                _viewModel.SortColumn = dbColumn;
            }
            else
            {
                _viewModel.SortColumn = dbColumn;
            }

            // Update the column header visual
            e.Column.SortDirection = _viewModel.SortAscending
                ? ListSortDirection.Ascending
                : ListSortDirection.Descending;
        }
    }

    private void FilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var ext = button.Tag?.ToString();
        if (string.IsNullOrEmpty(ext)) ext = null;

        FileLog.Write($"[DocumentLibraryView] FilterChip_Click: {ext ?? "All"}");
        _viewModel.ActiveExtFilter = ext;

        foreach (var child in FilterBar.Children)
        {
            if (child is Button chip)
            {
                var chipExt = chip.Tag?.ToString();
                chip.Style = (chipExt == (ext ?? ""))
                    ? (Style)FindResource("FilterChipActiveStyle")
                    : (Style)FindResource("FilterChipStyle");
            }
        }
    }

    private void CatalogGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedEntry is not null)
        {
            FileLog.Write($"[DocumentLibraryView] CatalogGrid_DoubleClick: {_viewModel.SelectedEntry.FileName}");
            _viewModel.OpenFile(_viewModel.SelectedEntry);
        }
    }
}

/// <summary>Tag for folder tree items linking back to their parent library and relative path.</summary>
internal sealed record FolderTag(Library Library, string RelativeDir);
