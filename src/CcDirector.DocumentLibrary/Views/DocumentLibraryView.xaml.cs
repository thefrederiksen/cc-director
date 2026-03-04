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
        // No polling needed -- data loaded on demand
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
        // Parent MainWindow handles hiding this panel
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

    private async void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string label) return;
        FileLog.Write($"[DocumentLibraryView] BtnScan_Click: {label}");

        var dialog = new ScanProgressDialog("scan", label);
        dialog.Owner = Window.GetWindow(this);
        dialog.ShowDialog();

        await _viewModel.RefreshLibrariesAsync();
        await _viewModel.LoadEntriesAsync();
    }

    private async void BtnSummarize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string label) return;
        FileLog.Write($"[DocumentLibraryView] BtnSummarize_Click: {label}");

        var dialog = new ScanProgressDialog("summarize", label);
        dialog.Owner = Window.GetWindow(this);
        dialog.ShowDialog();

        await _viewModel.RefreshLibrariesAsync();
        await _viewModel.LoadEntriesAsync();
    }

    private void FilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var ext = button.Tag?.ToString();
        if (string.IsNullOrEmpty(ext)) ext = null;

        FileLog.Write($"[DocumentLibraryView] FilterChip_Click: {ext ?? "All"}");
        _viewModel.ActiveExtFilter = ext;

        // Update chip visuals
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

/// <summary>
/// Converts null/empty string to Collapsed, non-null to Visible.
/// </summary>
public class NullToCollapsedConverter : IValueConverter
{
    public static readonly NullToCollapsedConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrEmpty(s))
            return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
