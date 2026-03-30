using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

public partial class CloseDialog : Window
{
    private readonly SessionManager _sessionManager;
    private bool _isShuttingDown;

    public CloseDialog(SessionManager sessionManager, IReadOnlyList<string> workingSessionNames)
    {
        InitializeComponent();
        _sessionManager = sessionManager;

        int count = workingSessionNames.Count;
        MessageText.Text = count == 1
            ? "1 session is actively working. Close anyway?"
            : $"{count} session(s) are actively working. Close anyway?";

        SessionListControl.ItemsSource = workingSessionNames;
    }

    private async void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        FileLog.Write("[CloseDialog] User confirmed shutdown, beginning session termination");

        // Disable buttons and show progress
        OkButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;

        try
        {
            // Kill all sessions
            await _sessionManager.KillAllSessionsAsync();

            FileLog.Write("[CloseDialog] All sessions terminated successfully");
            DialogResult = true;
        }
        catch (System.Exception ex)
        {
            FileLog.Write($"[CloseDialog] Session termination FAILED: {ex.Message}");
            // Still close - App.OnExit will force-kill remaining processes
            DialogResult = true;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_isShuttingDown) return;
        DialogResult = false;
    }
}
