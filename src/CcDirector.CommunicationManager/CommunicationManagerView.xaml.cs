using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CcDirector.Core.Utilities;
using CommunicationManager.Models;
using CommunicationManager.ViewModels;
using CommunicationManager.Views;

namespace CommunicationManager;

public partial class CommunicationManagerView : UserControl, IDisposable
{
    private readonly MainViewModel _viewModel;
    private bool _isInitialized;

    public CommunicationManagerView()
    {
        FileLog.Write("[CommunicationManagerView] Constructor");
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Loaded += CommunicationManagerView_Loaded;
    }

    /// <summary>
    /// The ViewModel exposed for external access (e.g. pending count badge).
    /// </summary>
    public MainViewModel ViewModel => _viewModel;

    private async void CommunicationManagerView_Loaded(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[CommunicationManagerView] Loaded");
        if (_isInitialized) return;
        _isInitialized = true;
        await _viewModel.InitializeAsync();
    }

    /// <summary>
    /// Start polling for new items. Call when the panel becomes visible.
    /// </summary>
    public void StartPolling()
    {
        FileLog.Write("[CommunicationManagerView] StartPolling");
        _viewModel.StartPolling();
    }

    /// <summary>
    /// Stop polling to save resources. Call when the panel is hidden.
    /// </summary>
    public void StopPolling()
    {
        FileLog.Write("[CommunicationManagerView] StopPolling");
        _viewModel.StopPolling();
    }

    /// <summary>
    /// Clean up resources. Call when the host window is closing.
    /// </summary>
    public void Dispose()
    {
        FileLog.Write("[CommunicationManagerView] Dispose");
        _viewModel.Dispose();
    }

    private void PendingTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _viewModel.OnTabChanged("Pending");
        ResetFilterChipVisuals();
        UpdateViewToggle("List");
    }

    private void ApprovedTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _viewModel.OnTabChanged("Approved");
        ResetFilterChipVisuals();
        ResetDateFilterChipVisuals("All Upcoming");
    }

    private void RejectedTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _viewModel.OnTabChanged("Rejected");
        ResetFilterChipVisuals();
        UpdateViewToggle("List");
    }

    private void SentTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _viewModel.OnTabChanged("Sent");
        ResetFilterChipVisuals();
        UpdateViewToggle("List");
    }

    private void FilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var platform = button.Tag?.ToString() ?? "All";

        _viewModel.SetPlatformFilterCommand.Execute(platform);
        UpdateFilterChipVisuals(platform);
    }

    private void DateFilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var filter = button.Tag?.ToString() ?? "All Upcoming";

        FileLog.Write($"[CommunicationManagerView] DateFilterChip_Click: {filter}");
        _viewModel.SetDateFilterCommand.Execute(filter);
        UpdateDateFilterChipVisuals(filter);
    }

    private void ViewToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var view = button.Tag?.ToString() ?? "List";

        FileLog.Write($"[CommunicationManagerView] ViewToggle_Click: {view}");
        _viewModel.SetApprovedViewCommand.Execute(view);
        UpdateViewToggle(view);
    }

    private async void Approve_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem == null) return;

        FileLog.Write("[CommunicationManagerView] Approve_Click: showing schedule dialog");

        var dialog = new ScheduleDialog();
        dialog.Owner = Window.GetWindow(this);

        if (dialog.ShowDialog() == true)
        {
            FileLog.Write($"[CommunicationManagerView] Schedule dialog result: timing={dialog.SelectedTiming}, dateTime={dialog.SelectedDateTime}");
            await _viewModel.ApproveWithScheduleCommand.ExecuteAsync((dialog.SelectedTiming, dialog.SelectedDateTime));
        }
    }

    private async void Reschedule_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem == null) return;

        FileLog.Write("[CommunicationManagerView] Reschedule_Click: showing schedule dialog");

        var dialog = new ScheduleDialog(_viewModel.SelectedItem.ScheduledFor);
        dialog.Owner = Window.GetWindow(this);
        dialog.Title = "Reschedule";

        if (dialog.ShowDialog() == true)
        {
            FileLog.Write($"[CommunicationManagerView] Reschedule dialog result: timing={dialog.SelectedTiming}, dateTime={dialog.SelectedDateTime}");
            await _viewModel.RescheduleCommand.ExecuteAsync((dialog.SelectedTiming, dialog.SelectedDateTime));
        }
    }

    private void Timeline_ItemSelected(object? sender, ContentItem item)
    {
        FileLog.Write($"[CommunicationManagerView] Timeline_ItemSelected: {item.DisplayTitle}");
        _viewModel.SelectedItem = item;
    }

    private void UpdateFilterChipVisuals(string activePlatform)
    {
        foreach (var child in FilterBar.Children)
        {
            if (child is Button chip)
            {
                chip.Style = (chip.Tag?.ToString() == activePlatform)
                    ? (Style)FindResource("FilterChipActiveStyle")
                    : (Style)FindResource("FilterChipStyle");
            }
        }
    }

    private void UpdateDateFilterChipVisuals(string activeFilter)
    {
        foreach (var child in DateFilterBar.Children)
        {
            if (child is Button chip && chip.Tag != null)
            {
                chip.Style = (chip.Tag.ToString() == activeFilter)
                    ? (Style)FindResource("FilterChipActiveStyle")
                    : (Style)FindResource("FilterChipStyle");
            }
        }
    }

    private void ResetDateFilterChipVisuals(string defaultFilter)
    {
        UpdateDateFilterChipVisuals(defaultFilter);
    }

    private void UpdateViewToggle(string activeView)
    {
        if (ListViewBtn == null || TimelineViewBtn == null) return;

        ListViewBtn.Style = activeView == "List"
            ? (Style)FindResource("FilterChipActiveStyle")
            : (Style)FindResource("FilterChipStyle");

        TimelineViewBtn.Style = activeView == "Timeline"
            ? (Style)FindResource("FilterChipActiveStyle")
            : (Style)FindResource("FilterChipStyle");

        // Toggle visibility
        if (ApprovedTimeline != null)
        {
            var isApprovedTab = ApprovedTab?.IsChecked == true;
            ApprovedTimeline.Visibility = isApprovedTab && activeView == "Timeline"
                ? Visibility.Visible
                : Visibility.Collapsed;
            ItemList.Visibility = isApprovedTab && activeView == "Timeline"
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private void ResetFilterChipVisuals()
    {
        UpdateFilterChipVisuals("All");
    }

    private void PreviewToggle_Click(object sender, MouseButtonEventArgs e)
    {
        _viewModel.IsPreviewMode = true;
    }

    private void RawToggle_Click(object sender, MouseButtonEventArgs e)
    {
        _viewModel.IsPreviewMode = false;
    }
}
