using System.Windows;
using System.Windows.Input;
using CommunicationManager.ViewModels;

namespace CommunicationManager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _isInitialized;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _isInitialized = true;
        await _viewModel.InitializeAsync();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
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
