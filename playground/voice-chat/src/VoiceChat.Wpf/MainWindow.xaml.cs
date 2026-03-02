using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VoiceChat.Wpf.ViewModels;

namespace VoiceChat.Wpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        // Auto-scroll chat when new messages arrive
        _viewModel.Messages.CollectionChanged += (_, _) =>
        {
            ChatScrollViewer.ScrollToEnd();
        };

        // Save dictionary when TextBox loses focus (not on every keystroke)
        DictionaryTextBox.LostFocus += (_, _) =>
        {
            DictionaryTextBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        };

        // Update processing indicator visibility
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsProcessing))
            {
                ProcessingText.Visibility = _viewModel.IsProcessing
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (e.PropertyName == nameof(MainViewModel.IsInitialized))
            {
                TalkButtonSubtext.Text = _viewModel.IsInitialized
                    ? "(press and hold)"
                    : "(initializing...)";
            }
        };

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Dispose();
    }

    private void OnTalkButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsInitialized || _viewModel.IsProcessing) return;

        TalkButtonBorder.Background = FindResource("RecordingBrush") as SolidColorBrush;
        TalkButtonText.Text = "Listening...";
        TalkButtonSubtext.Text = "(release to send)";
        _viewModel.StartRecording();
    }

    private async void OnTalkButtonUp(object sender, MouseButtonEventArgs e)
    {
        await FinishRecording();
    }

    private async void OnTalkButtonLeave(object sender, MouseEventArgs e)
    {
        // Also stop recording if mouse leaves the button area while held
        if (_viewModel.IsRecording)
        {
            await FinishRecording();
        }
    }

    private async Task FinishRecording()
    {
        if (!_viewModel.IsRecording) return;

        TalkButtonBorder.Background = FindResource("ButtonBackground") as SolidColorBrush;
        TalkButtonText.Text = "Hold to Talk";
        TalkButtonSubtext.Text = "(press and hold)";

        await _viewModel.StopRecordingAsync();
    }
}
