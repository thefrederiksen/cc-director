using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CcDirector.Core.Sessions;
using CcDirector.Wpf.Controls;

namespace CcDirector.Wpf;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<SessionViewModel> _sessions = new();
    private SessionManager _sessionManager = null!;
    private TerminalControl? _terminalControl;
    private Session? _activeSession;

    public MainWindow()
    {
        InitializeComponent();
        SessionList.ItemsSource = _sessions;
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        _sessionManager = app.SessionManager;
    }

    private void BtnNewSession_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        var repos = app.Repositories;

        if (repos.Count == 0)
        {
            // No repos configured - ask for a path
            var dialog = new NewSessionDialog();
            dialog.Owner = this;
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                CreateSession(dialog.SelectedPath);
            }
            return;
        }

        // Show repo picker
        var pickerDialog = new NewSessionDialog(repos);
        pickerDialog.Owner = this;
        if (pickerDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(pickerDialog.SelectedPath))
        {
            CreateSession(pickerDialog.SelectedPath);
        }
    }

    private void CreateSession(string repoPath)
    {
        try
        {
            var session = _sessionManager.CreateSession(repoPath);
            var vm = new SessionViewModel(session);
            _sessions.Add(vm);
            SessionList.SelectedItem = vm;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to create session:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnKillSession_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionViewModel vm)
            return;

        try
        {
            await _sessionManager.KillSessionAsync(vm.Session.Id);
            vm.Refresh();

            // Detach terminal
            if (_activeSession?.Id == vm.Session.Id)
            {
                DetachTerminal();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to kill session:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionViewModel vm)
        {
            DetachTerminal();
            return;
        }

        AttachTerminal(vm.Session);
    }

    private void AttachTerminal(Session session)
    {
        _activeSession = session;
        PlaceholderText.Visibility = Visibility.Collapsed;

        if (_terminalControl != null)
        {
            _terminalControl.Detach();
            TerminalArea.Child = null;
        }

        _terminalControl = new TerminalControl();
        TerminalArea.Child = _terminalControl;
        _terminalControl.Attach(session);
        _terminalControl.Focus();
    }

    private void DetachTerminal()
    {
        _activeSession = null;
        if (_terminalControl != null)
        {
            _terminalControl.Detach();
            TerminalArea.Child = null;
            _terminalControl = null;
        }
        PlaceholderText.Visibility = Visibility.Visible;
    }
}

public class SessionViewModel
{
    public Session Session { get; }

    public SessionViewModel(Session session)
    {
        Session = session;
    }

    public string DisplayName => System.IO.Path.GetFileName(Session.RepoPath.TrimEnd('\\', '/'));
    public string StatusText => $"{Session.Status} (PID {Session.ProcessId})";

    public void Refresh()
    {
        // In a full MVVM implementation we'd use INotifyPropertyChanged.
        // For now the UI refreshes on selection change.
    }
}
