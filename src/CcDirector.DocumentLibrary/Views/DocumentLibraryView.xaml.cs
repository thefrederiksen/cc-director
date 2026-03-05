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
            try
            {
                var client = new Services.VaultCatalogClient();
                await client.AddLibraryAsync(
                    dialog.LibraryPath, dialog.LibraryLabel,
                    dialog.LibraryCategory, dialog.LibraryOwner);
                await _viewModel.RefreshLibrariesAsync();
                BuildTree();
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DocumentLibraryView] AddLibrary FAILED: {ex.Message}");
                MessageBox.Show(
                    $"Failed to add library: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchPlaceholder is not null)
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
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
