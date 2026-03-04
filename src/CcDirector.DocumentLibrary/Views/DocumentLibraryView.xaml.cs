using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CcDirector.Core.Utilities;
using CcDirector.DocumentLibrary.Models;
using CcDirector.DocumentLibrary.ViewModels;

namespace CcDirector.DocumentLibrary.Views;

public partial class DocumentLibraryView : UserControl, IDisposable
{
    private readonly DocumentLibraryViewModel _viewModel;
    private bool _isInitialized;

    public DocumentLibraryView()
    {
        FileLog.Write("[DocumentLibraryView] Constructor");
        InitializeComponent();
        _viewModel = new DocumentLibraryViewModel();
        DataContext = _viewModel;
        Loaded += DocumentLibraryView_Loaded;
    }

    public DocumentLibraryViewModel ViewModel => _viewModel;

    private async void DocumentLibraryView_Loaded(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[DocumentLibraryView] Loaded");
        if (_isInitialized) return;
        _isInitialized = true;
        LoadingText.Visibility = Visibility.Visible;
        await _viewModel.InitializeAsync();
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
        _viewModel.Dispose();
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

    private void Library_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not Library lib) return;
        FileLog.Write($"[DocumentLibraryView] Library_Click: {lib.Label}");
        _viewModel.SelectedLibrary = lib;

        // Show department panel
        DeptHeader.Visibility = Visibility.Visible;
        DeptPanel.Visibility = Visibility.Visible;
    }

    private async void BtnScanAndSummarize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string label) return;
        FileLog.Write($"[DocumentLibraryView] BtnScanAndSummarize_Click: {label}");

        // Run scan first
        var scanDialog = new ScanProgressDialog("scan", label);
        scanDialog.Owner = Window.GetWindow(this);
        scanDialog.ShowDialog();

        // Then summarize
        var sumDialog = new ScanProgressDialog("summarize", label);
        sumDialog.Owner = Window.GetWindow(this);
        sumDialog.ShowDialog();

        await _viewModel.RefreshLibrariesAsync();
        await _viewModel.LoadEntriesAsync();
    }

    private void DeptAll_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[DocumentLibraryView] DeptAll_Click");
        DeptList.SelectedItem = null;
        _viewModel.SelectedDepartment = null;
    }

    private void DeptList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeptList.SelectedItem is string dept)
        {
            FileLog.Write($"[DocumentLibraryView] DeptList_SelectionChanged: {dept}");
            _viewModel.SelectedDepartment = dept;
        }
    }

    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string column) return;
        FileLog.Write($"[DocumentLibraryView] SortHeader_Click: {column}");
        _viewModel.SortColumn = column;
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

    private void CatalogList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedEntry is not null)
        {
            FileLog.Write($"[DocumentLibraryView] CatalogList_DoubleClick: {_viewModel.SelectedEntry.FileName}");
            _viewModel.OpenFile(_viewModel.SelectedEntry);
        }
    }
}
