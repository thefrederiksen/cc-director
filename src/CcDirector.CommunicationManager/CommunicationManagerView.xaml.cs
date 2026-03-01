using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CcDirector.Core.Utilities;
using CommunicationManager.ViewModels;

namespace CommunicationManager;

public partial class CommunicationManagerView : UserControl
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
        ItemList.ItemsSource = _viewModel.PendingItems;
        _viewModel.OnTabChanged("Pending");
    }

    private void ApprovedTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        ItemList.ItemsSource = _viewModel.ApprovedItems;
        _viewModel.OnTabChanged("Approved");
    }

    private void RejectedTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        ItemList.ItemsSource = _viewModel.RejectedItems;
        _viewModel.OnTabChanged("Rejected");
    }

    private void SentTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        ItemList.ItemsSource = _viewModel.SentItems;
        _viewModel.OnTabChanged("Sent");
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
