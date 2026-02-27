using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

public partial class RestoreSessionsDialog : Window
{
    private readonly List<PersistedSession> _sessions;
    private readonly Func<PersistedSession, bool, MainWindow.SingleRestoreResult> _restoreCallback;
    private readonly ObservableCollection<RestoreItemViewModel> _progressItems = new();
    private bool _isRestoring;

    public int RestoredCount { get; private set; }
    public int FailedCount { get; private set; }
    public List<string> FailedRepos { get; } = new();

    internal RestoreSessionsDialog(
        List<PersistedSession> sessions,
        Func<PersistedSession, bool, MainWindow.SingleRestoreResult> restoreCallback)
    {
        FileLog.Write($"[RestoreSessionsDialog] Constructor: {sessions.Count} session(s)");
        InitializeComponent();

        _sessions = sessions;
        _restoreCallback = restoreCallback;

        // Build display names for session list
        var displayNames = sessions.Select(BuildDisplayName).ToList();
        SessionListControl.ItemsSource = displayNames;
        ProgressListControl.ItemsSource = _progressItems;

        // Build progress view models
        foreach (var name in displayNames)
            _progressItems.Add(new RestoreItemViewModel(name));

        string sessionWord = sessions.Count == 1 ? "session" : "sessions";
        HeaderText.Text = $"{sessions.Count} {sessionWord} from your previous run are ready to be restored:";
    }

    private static string BuildDisplayName(PersistedSession p)
    {
        string repoName = System.IO.Path.GetFileName(p.RepoPath.TrimEnd('\\', '/'));
        if (!string.IsNullOrEmpty(p.CustomName))
            return $"{repoName} ({p.CustomName})";
        return repoName;
    }

    private async void BtnContinue_Click(object sender, RoutedEventArgs e)
    {
        if (_isRestoring) return;
        _isRestoring = true;

        bool startFresh = ChkStartFresh.IsChecked == true;
        FileLog.Write($"[RestoreSessionsDialog] BtnContinue_Click: startFresh={startFresh}");

        PreRestorePanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;

        int total = _sessions.Count;
        for (int i = 0; i < total; i++)
        {
            var session = _sessions[i];
            var item = _progressItems[i];
            string displayName = item.DisplayName;

            ProgressStatusText.Text = $"Starting: {displayName} ({i + 1} of {total})";
            item.SetInProgress();
            ProgressBar.Value = (double)i / total * 100;

            FileLog.Write($"[RestoreSessionsDialog] Starting session {i + 1}/{total}: {displayName}");

            MainWindow.SingleRestoreResult result;
            try
            {
                result = await Task.Run(() => _restoreCallback(session, startFresh));
            }
            catch (Exception ex)
            {
                FileLog.Write($"[RestoreSessionsDialog] Restore threw exception: {ex.Message}");
                result = new MainWindow.SingleRestoreResult(MainWindow.RestoreStatus.CreateFailed, $"{displayName} ({ex.Message})");
            }

            if (result.Status == MainWindow.RestoreStatus.Success)
            {
                item.SetSuccess();
                RestoredCount++;
                FileLog.Write($"[RestoreSessionsDialog] Session {i + 1}/{total} restored OK");
            }
            else
            {
                item.SetFailed();
                FailedCount++;
                FailedRepos.Add(result.FailureReason ?? displayName);
                FileLog.Write($"[RestoreSessionsDialog] Session {i + 1}/{total} FAILED: {result.Status} - {result.FailureReason}");
            }

            // Delay between sessions to prevent Claude Code settings corruption
            if (i < total - 1)
                await Task.Delay(2500);
        }

        ProgressBar.Value = 100;

        string word = total == 1 ? "session" : "sessions";
        if (FailedCount == 0)
            ProgressStatusText.Text = $"All {total} {word} restored successfully.";
        else
            ProgressStatusText.Text = $"Restored {RestoredCount} of {total} {word}. {FailedCount} failed.";

        DoneButtonPanel.Visibility = Visibility.Visible;
        _isRestoring = false;

        FileLog.Write($"[RestoreSessionsDialog] Restore complete: restored={RestoredCount}, failed={FailedCount}");
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_isRestoring) return;
        FileLog.Write("[RestoreSessionsDialog] BtnSkip_Click: User skipped all sessions");
        DialogResult = false;
    }

    private void BtnDone_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[RestoreSessionsDialog] BtnDone_Click");
        DialogResult = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isRestoring)
        {
            FileLog.Write("[RestoreSessionsDialog] OnClosing blocked: restore in progress");
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    internal class RestoreItemViewModel : INotifyPropertyChanged
    {
        private string _statusIndicator = "[  ]";
        private Brush _statusColor = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

        public string DisplayName { get; }
        public string StatusIndicator
        {
            get => _statusIndicator;
            private set { _statusIndicator = value; OnPropertyChanged(); }
        }
        public Brush StatusColor
        {
            get => _statusColor;
            private set { _statusColor = value; OnPropertyChanged(); }
        }

        public RestoreItemViewModel(string displayName) => DisplayName = displayName;

        public void SetInProgress()
        {
            StatusIndicator = "[..]";
            StatusColor = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
        }

        public void SetSuccess()
        {
            StatusIndicator = "[OK]";
            StatusColor = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
        }

        public void SetFailed()
        {
            StatusIndicator = "[X]";
            StatusColor = new SolidColorBrush(Color.FromRgb(0xF4, 0x48, 0x47));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
