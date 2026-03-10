using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

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

    // Parameterless constructor for XAML designer
    public CloseDialog() : this(null!, new List<string>()) { }

    private async void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        FileLog.Write("[CloseDialog] User confirmed shutdown, beginning session termination");

        OkButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        ProgressPanel.IsVisible = true;

        try
        {
            await _sessionManager.KillAllSessionsAsync();
            FileLog.Write("[CloseDialog] All sessions terminated successfully");
            Close(true);
        }
        catch (System.Exception ex)
        {
            FileLog.Write($"[CloseDialog] Session termination FAILED: {ex.Message}");
            Close(true);
        }
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        if (_isShuttingDown) return;
        Close(false);
    }
}
