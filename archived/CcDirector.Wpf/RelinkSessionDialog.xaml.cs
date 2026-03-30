using System.IO;
using System.Windows;
using System.Windows.Controls;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf;

/// <summary>
/// View model for displaying Claude sessions in the re-link dialog.
/// </summary>
public class RelinkSessionViewModel
{
    private readonly ClaudeSessionMetadata _metadata;

    public RelinkSessionViewModel(ClaudeSessionMetadata metadata)
    {
        _metadata = metadata;
    }

    public ClaudeSessionMetadata Metadata => _metadata;
    public string SessionId => _metadata.SessionId;

    public string SessionIdShort => _metadata.SessionId.Length > 8
        ? _metadata.SessionId[..8] + "..."
        : _metadata.SessionId;

    public string MessageCountDisplay => $"{_metadata.MessageCount} msgs";

    public string TimeAgo
    {
        get
        {
            if (_metadata.Modified == DateTime.MinValue)
                return string.Empty;

            var span = DateTime.UtcNow - _metadata.Modified.ToUniversalTime();

            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
            if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)}mo ago";
            return $"{(int)(span.TotalDays / 365)}y ago";
        }
    }

    public string DisplaySummary
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_metadata.Summary))
                return TruncateWithEllipsis(_metadata.Summary, 100);

            return $"{_metadata.MessageCount} messages";
        }
    }

    public string FirstPromptDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_metadata.FirstPrompt))
                return string.Empty;
            return "First: " + TruncateWithEllipsis(_metadata.FirstPrompt, 80);
        }
    }

    private static string TruncateWithEllipsis(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        text = text.Replace("\r", " ").Replace("\n", " ");
        while (text.Contains("  "))
            text = text.Replace("  ", " ");

        if (text.Length <= maxLength)
            return text.Trim();

        return text.Substring(0, maxLength - 3).Trim() + "...";
    }
}

/// <summary>
/// Dialog for manually re-linking a Director session to a different Claude session.
/// Shows all Claude sessions for the repo and allows the user to select one.
/// </summary>
public partial class RelinkSessionDialog : Window
{
    private readonly string _repoPath;
    private List<RelinkSessionViewModel>? _allSessions;
    private bool _sessionsLoaded;

    /// <summary>The selected Claude session ID.</summary>
    public string? SelectedSessionId { get; private set; }

    public RelinkSessionDialog(string repoPath)
    {
        InitializeComponent();
        _repoPath = repoPath;

        // Show repo path in header
        RepoPathText.Text = repoPath;

        // Load sessions async after dialog is shown
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await LoadSessionsAsync();
        };
    }

    private async Task LoadSessionsAsync()
    {
        FileLog.Write($"[RelinkSessionDialog] LoadSessionsAsync: loading for {_repoPath}");

        try
        {
            var sessions = await Task.Run(() => ClaudeSessionReader.ReadAllSessionMetadata(_repoPath));

            FileLog.Write($"[RelinkSessionDialog] LoadSessionsAsync: found {sessions.Count} sessions");

            // Sort by modified date (most recent first)
            _allSessions = sessions
                .OrderByDescending(s => s.Modified)
                .Select(s => new RelinkSessionViewModel(s))
                .ToList();

            _sessionsLoaded = true;

            // Update UI
            LoadingText.Visibility = Visibility.Collapsed;

            if (_allSessions.Count > 0)
            {
                SessionList.ItemsSource = _allSessions;
                SessionList.Visibility = Visibility.Visible;
            }
            else
            {
                NoSessionsText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RelinkSessionDialog] LoadSessionsAsync FAILED: {ex.Message}");
            LoadingText.Visibility = Visibility.Collapsed;
            NoSessionsText.Text = "Error loading sessions";
            NoSessionsText.Visibility = Visibility.Visible;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_sessionsLoaded || _allSessions == null)
            return;

        var filter = SearchBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(filter))
        {
            SessionList.ItemsSource = _allSessions;
        }
        else
        {
            SessionList.ItemsSource = _allSessions
                .Where(s => s.SessionId.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.DisplaySummary.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.FirstPromptDisplay.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is RelinkSessionViewModel vm)
        {
            SelectedSessionId = vm.SessionId;
            BtnLink.IsEnabled = true;
            FileLog.Write($"[RelinkSessionDialog] Session selected: {vm.SessionId}");
        }
        else
        {
            SelectedSessionId = null;
            BtnLink.IsEnabled = false;
        }
    }

    private void BtnLink_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(SelectedSessionId))
        {
            FileLog.Write("[RelinkSessionDialog] BtnLink_Click: No session selected");
            return;
        }

        FileLog.Write($"[RelinkSessionDialog] BtnLink_Click: Linking to {SelectedSessionId}");
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[RelinkSessionDialog] BtnCancel_Click");
        DialogResult = false;
    }
}
