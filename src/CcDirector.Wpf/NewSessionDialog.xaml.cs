using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using Microsoft.Win32;

namespace CcDirector.Wpf;

/// <summary>
/// View model for displaying Claude sessions in the Resume Session tab.
/// Wraps ClaudeSessionMetadata with display-friendly properties.
/// Still used by RelinkSessionDialog for browsing raw Claude sessions.
/// </summary>
public class ClaudeSessionViewModel
{
    private readonly ClaudeSessionMetadata _metadata;
    private readonly string? _customName;
    private readonly string? _customColor;

    public ClaudeSessionViewModel(ClaudeSessionMetadata metadata, string? customName = null, string? customColor = null)
    {
        _metadata = metadata;
        _customName = customName;
        _customColor = customColor;
    }

    /// <summary>The underlying metadata.</summary>
    public ClaudeSessionMetadata Metadata => _metadata;

    /// <summary>The Claude session ID for resuming.</summary>
    public string SessionId => _metadata.SessionId;

    /// <summary>Display name prefers custom name over repo name.</summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(_customName) ? _customName : ProjectName;

    /// <summary>Show repo name in parentheses when custom name is set.</summary>
    public string ProjectNameSuffix => HasCustomName ? $"({ProjectName})" : string.Empty;

    /// <summary>Whether this session has a custom name.</summary>
    public bool HasCustomName => !string.IsNullOrWhiteSpace(_customName);

    /// <summary>Whether this session has a custom color.</summary>
    public bool HasCustomColor => !string.IsNullOrWhiteSpace(_customColor);

    /// <summary>The custom color brush for the color indicator.</summary>
    public SolidColorBrush? CustomColorBrush
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_customColor)) return null;
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(_customColor);
                return new SolidColorBrush(color);
            }
            catch { return null; }
        }
    }

    /// <summary>Extract project name from path.</summary>
    public string ProjectName
    {
        get
        {
            if (string.IsNullOrEmpty(_metadata.ProjectPath))
                return "Unknown Project";
            return Path.GetFileName(_metadata.ProjectPath.TrimEnd('\\', '/'));
        }
    }

    /// <summary>The full project path.</summary>
    public string ProjectPath => _metadata.ProjectPath ?? string.Empty;

    /// <summary>Message count display (e.g., "42 msgs").</summary>
    public string MessageCountDisplay => $"{_metadata.MessageCount} msgs";

    /// <summary>Time ago display (e.g., "2h ago", "3d ago").</summary>
    public string TimeAgo
    {
        get
        {
            if (_metadata.Modified == DateTime.MinValue)
                return string.Empty;

            var span = DateTime.UtcNow - _metadata.Modified.ToUniversalTime();

            if (span.TotalMinutes < 1)
                return "just now";
            if (span.TotalMinutes < 60)
                return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24)
                return $"{(int)span.TotalHours}h ago";
            if (span.TotalDays < 30)
                return $"{(int)span.TotalDays}d ago";
            if (span.TotalDays < 365)
                return $"{(int)(span.TotalDays / 30)}mo ago";
            return $"{(int)(span.TotalDays / 365)}y ago";
        }
    }

    /// <summary>Summary or first prompt for display.</summary>
    public string DisplaySummary
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_metadata.Summary))
                return TruncateWithEllipsis(_metadata.Summary, 120);

            if (!string.IsNullOrWhiteSpace(_metadata.FirstPrompt))
                return TruncateWithEllipsis(_metadata.FirstPrompt, 120);

            return $"{_metadata.MessageCount} messages";
        }
    }

    internal static string TruncateWithEllipsis(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // Remove newlines for single-line display
        text = text.Replace("\r", " ").Replace("\n", " ");

        // Collapse multiple spaces
        while (text.Contains("  "))
            text = text.Replace("  ", " ");

        if (text.Length <= maxLength)
            return text.Trim();

        return text.Substring(0, maxLength - 3).Trim() + "...";
    }
}

/// <summary>
/// View model for displaying session history entries in the Resume Session tab.
/// Wraps a SessionHistoryEntry with optional Claude metadata enrichment.
/// </summary>
public class SessionHistoryViewModel
{
    private readonly SessionHistoryEntry _entry;
    private readonly ClaudeSessionMetadata? _claudeMetadata;

    public SessionHistoryViewModel(SessionHistoryEntry entry, ClaudeSessionMetadata? claudeMetadata)
    {
        _entry = entry;
        _claudeMetadata = claudeMetadata;
    }

    /// <summary>The Claude session ID for resuming (null if not yet linked).</summary>
    public string? ClaudeSessionId => _entry.ClaudeSessionId;

    /// <summary>Display name prefers custom name over repo folder name.</summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(_entry.CustomName)
        ? _entry.CustomName
        : ProjectName;

    /// <summary>Extract project name from repo path.</summary>
    public string ProjectName
    {
        get
        {
            if (string.IsNullOrEmpty(_entry.RepoPath))
                return "Unknown Project";
            return Path.GetFileName(_entry.RepoPath.TrimEnd('\\', '/'));
        }
    }

    /// <summary>Show repo name in parentheses when custom name is set.</summary>
    public string ProjectNameSuffix => HasCustomName ? $"({ProjectName})" : string.Empty;

    /// <summary>Whether this session has a custom name.</summary>
    public bool HasCustomName => !string.IsNullOrWhiteSpace(_entry.CustomName);

    /// <summary>Whether this session has a custom color.</summary>
    public bool HasCustomColor => !string.IsNullOrWhiteSpace(_entry.CustomColor);

    /// <summary>The custom color brush for the color indicator.</summary>
    public SolidColorBrush? CustomColorBrush
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_entry.CustomColor)) return null;
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(_entry.CustomColor);
                return new SolidColorBrush(color);
            }
            catch { return null; }
        }
    }

    /// <summary>The full project path.</summary>
    public string ProjectPath => _entry.RepoPath;

    /// <summary>Message count display from Claude metadata, or empty.</summary>
    public string MessageCountDisplay => _claudeMetadata != null
        ? $"{_claudeMetadata.MessageCount} msgs"
        : string.Empty;

    /// <summary>Time ago based on our LastUsedAt (when user last used this in CC Director).</summary>
    public string TimeAgo
    {
        get
        {
            if (_entry.LastUsedAt == default)
                return string.Empty;

            var span = DateTimeOffset.UtcNow - _entry.LastUsedAt;

            if (span.TotalMinutes < 1)
                return "just now";
            if (span.TotalMinutes < 60)
                return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24)
                return $"{(int)span.TotalHours}h ago";
            if (span.TotalDays < 30)
                return $"{(int)span.TotalDays}d ago";
            if (span.TotalDays < 365)
                return $"{(int)(span.TotalDays / 30)}mo ago";
            return $"{(int)(span.TotalDays / 365)}y ago";
        }
    }

    /// <summary>Summary from Claude metadata, or cached first prompt snippet.</summary>
    public string DisplaySummary
    {
        get
        {
            if (_claudeMetadata != null)
            {
                if (!string.IsNullOrWhiteSpace(_claudeMetadata.Summary))
                    return ClaudeSessionViewModel.TruncateWithEllipsis(_claudeMetadata.Summary, 120);

                if (!string.IsNullOrWhiteSpace(_claudeMetadata.FirstPrompt))
                    return ClaudeSessionViewModel.TruncateWithEllipsis(_claudeMetadata.FirstPrompt, 120);
            }

            if (!string.IsNullOrWhiteSpace(_entry.FirstPromptSnippet))
                return ClaudeSessionViewModel.TruncateWithEllipsis(_entry.FirstPromptSnippet, 120);

            return string.Empty;
        }
    }
}

/// <summary>
/// View model for displaying handover documents in the Handovers tab.
/// Parses timestamped filenames like 20260304_0700_fix-session-routing.md
/// and YAML frontmatter for structured metadata.
/// </summary>
public class HandoverViewModel
{
    public string FilePath { get; }
    public string Title { get; }
    public string DateDisplay { get; }
    public DateTime FileDate { get; }

    /// <summary>Primary repository path (first valid path from frontmatter repositories list).</summary>
    public string? RepoPath { get; }

    /// <summary>All repository paths from frontmatter.</summary>
    public List<string> RepoPaths { get; } = new();

    /// <summary>Session name from YAML frontmatter (session_name field).</summary>
    public string? SessionName { get; }

    /// <summary>Display-friendly repo name (folder name from RepoPath).</summary>
    public string RepoDisplay => string.IsNullOrEmpty(RepoPath)
        ? "Unknown"
        : Path.GetFileName(RepoPath.TrimEnd('\\', '/'));

    public HandoverViewModel(string filePath)
    {
        FilePath = filePath;
        var name = Path.GetFileNameWithoutExtension(filePath);

        // Parse filename: YYYYMMDD_HHMM_title-slug
        if (name.Length >= 13 && name[8] == '_'
            && DateTime.TryParseExact(name.Substring(0, 8) + name.Substring(9, 4),
                "yyyyMMddHHmm", null, System.Globalization.DateTimeStyles.None, out var parsed))
        {
            FileDate = parsed;
            DateDisplay = parsed.ToString("yyyy-MM-dd HH:mm");

            var slug = name.Length > 14 ? name.Substring(14) : string.Empty;
            Title = string.IsNullOrEmpty(slug)
                ? "Handover"
                : char.ToUpper(slug[0]) + slug.Substring(1).Replace("-", " ");
        }
        else
        {
            FileDate = File.GetLastWriteTime(filePath);
            DateDisplay = FileDate.ToString("yyyy-MM-dd HH:mm");
            Title = name;
        }

        var frontmatter = ExtractFrontmatter(filePath);
        RepoPaths = frontmatter.RepoPaths;
        RepoPath = RepoPaths.FirstOrDefault();
        SessionName = frontmatter.SessionName;
    }

    private record HandoverFrontmatter(List<string> RepoPaths, string? SessionName);

    /// <summary>
    /// Extracts structured metadata from YAML frontmatter (repo paths, session name).
    /// Falls back to legacy **Repository:** format for old handovers.
    /// </summary>
    private static HandoverFrontmatter ExtractFrontmatter(string filePath)
    {
        var paths = new List<string>();
        string? sessionName = null;
        try
        {
            using var reader = new StreamReader(filePath);
            var firstLine = reader.ReadLine();
            if (firstLine == "---")
            {
                // YAML frontmatter: read until closing ---
                bool inRepositories = false;
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line == "---") break;

                    if (line.StartsWith("session_name:"))
                    {
                        inRepositories = false;
                        sessionName = line.Substring("session_name:".Length).Trim();
                        if (string.IsNullOrEmpty(sessionName))
                            sessionName = null;
                        continue;
                    }

                    if (line.StartsWith("repositories:"))
                    {
                        inRepositories = true;
                        continue;
                    }

                    // A non-indented key ends the repositories block
                    if (inRepositories && line.Length > 0 && !char.IsWhiteSpace(line[0]))
                        inRepositories = false;

                    if (inRepositories && line.TrimStart().StartsWith("- path:"))
                    {
                        var path = line.Substring(line.IndexOf("- path:") + 7).Trim();
                        if (Directory.Exists(path))
                            paths.Add(path);
                    }
                }
            }
            else
            {
                // Legacy format: scan first 10 lines for **Repository:** line
                var line = firstLine;
                for (int i = 0; i < 10 && line != null; i++)
                {
                    if (line.StartsWith("**Repository:**"))
                    {
                        var raw = line.Substring("**Repository:**".Length).Trim();
                        // Handle comma-separated paths with optional annotations like "(primary)"
                        foreach (var segment in raw.Split(','))
                        {
                            // Strip annotations in parentheses: "D:\path (primary)" -> "D:\path"
                            var cleaned = System.Text.RegularExpressions.Regex.Replace(
                                segment.Trim(), @"\s*\(.*?\)\s*$", "").Trim();
                            if (Directory.Exists(cleaned))
                                paths.Add(cleaned);
                        }
                        break;
                    }
                    line = reader.ReadLine();
                }
            }
        }
        catch { /* Non-critical: preview still works without repo path */ }

        return new HandoverFrontmatter(paths, sessionName);
    }
}

public partial class NewSessionDialog : Window
{
    private static readonly SolidColorBrush ResumeButtonBrush = new(
        (Color)ColorConverter.ConvertFromString("#22C55E"));
    private static readonly SolidColorBrush NewSessionButtonBrush = new(
        (Color)ColorConverter.ConvertFromString("#007ACC"));
    private static readonly SolidColorBrush DisabledButtonBrush = new(
        (Color)ColorConverter.ConvertFromString("#4A4A4A"));
    private static readonly SolidColorBrush DisabledTextBrush = new(
        (Color)ColorConverter.ConvertFromString("#AAAAAA"));
    private static readonly SolidColorBrush EnabledTextBrush = new(Colors.White);

    static NewSessionDialog()
    {
        ResumeButtonBrush.Freeze();
        NewSessionButtonBrush.Freeze();
        DisabledButtonBrush.Freeze();
        DisabledTextBrush.Freeze();
        EnabledTextBrush.Freeze();
    }

    private readonly RepositoryRegistry? _registry;
    private readonly SessionHistoryStore? _historyStore;
    private List<SessionHistoryViewModel>? _allSessions;
    private List<RepositoryConfig>? _allRepos;
    private List<HandoverViewModel>? _allHandovers;
    private bool _sessionsLoaded;
    private bool _handoversLoaded;

    /// <summary>The selected path (for new session or resume).</summary>
    public string? SelectedPath { get; private set; }

    /// <summary>The Claude session ID to resume (null for new session).</summary>
    public string? SelectedResumeSessionId { get; private set; }

    /// <summary>Path to the selected handover file (when starting from Handovers tab).</summary>
    public string? SelectedHandoverPath { get; private set; }

    /// <summary>Whether to bypass permission prompts (adds --dangerously-skip-permissions flag).</summary>
    public bool BypassPermissions => BypassPermissionsCheckBox.IsChecked == true;

    /// <summary>Whether to enable remote control mode (uses 'remote-control' subcommand).</summary>
    public bool EnableRemoteControl => RemoteControlCheckBox.IsChecked == true;

    public NewSessionDialog(RepositoryRegistry? registry = null, SessionHistoryStore? historyStore = null)
    {
        FileLog.Write("[NewSessionDialog] Constructor: initializing");
        InitializeComponent();
        _registry = registry;
        _historyStore = historyStore;

        // Set dialog size to 80% of screen
        Width = SystemParameters.PrimaryScreenWidth * 0.8;
        Height = SystemParameters.PrimaryScreenHeight * 0.7;
        MinWidth = 900;
        MinHeight = 600;

        // Load repositories immediately (typically fast)
        if (_registry != null && _registry.Repositories.Count > 0)
        {
            _allRepos = _registry.Repositories.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            RepoList.ItemsSource = _allRepos;
            FileLog.Write($"[NewSessionDialog] Loaded {_allRepos.Count} repositories");
        }
        else
        {
            _allRepos = new List<RepositoryConfig>();
        }

        // Load session history async after dialog is shown
        Loaded += async (_, _) =>
        {
            RepoSearchBox.Focus();
            await LoadSessionHistoryAsync();
        };

        FileLog.Write("[NewSessionDialog] Constructor: complete");
    }

    private async Task LoadSessionHistoryAsync()
    {
        FileLog.Write("[NewSessionDialog] LoadSessionHistoryAsync: starting");

        try
        {
            // Load both data sources in parallel
            var historyTask = Task.Run(() => _historyStore?.LoadAll() ?? new List<SessionHistoryEntry>());
            var claudeMetadataTask = Task.Run(() =>
            {
                var map = new Dictionary<string, ClaudeSessionMetadata>(StringComparer.Ordinal);
                foreach (var cm in ClaudeSessionReader.ScanAllProjects())
                    map.TryAdd(cm.SessionId, cm);
                return map;
            });

            await Task.WhenAll(historyTask, claudeMetadataTask);

            var historyEntries = historyTask.Result;
            var claudeMetadata = claudeMetadataTask.Result;

            FileLog.Write($"[NewSessionDialog] LoadSessionHistoryAsync: found {historyEntries.Count} history entries, {claudeMetadata.Count} Claude sessions");

            // Build view models: history entries enriched with Claude metadata
            _allSessions = historyEntries.Select(entry =>
            {
                ClaudeSessionMetadata? meta = null;
                if (!string.IsNullOrEmpty(entry.ClaudeSessionId))
                    claudeMetadata.TryGetValue(entry.ClaudeSessionId, out meta);

                return new SessionHistoryViewModel(entry, meta);
            }).ToList();

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
                NoSessionsText.Text = "No session history yet. Start a new session to begin.";
                NoSessionsText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[NewSessionDialog] LoadSessionHistoryAsync FAILED: {ex.Message}");
            LoadingText.Visibility = Visibility.Collapsed;
            NoSessionsText.Text = "Error loading sessions";
            NoSessionsText.Visibility = Visibility.Visible;
        }
    }

    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs)
            return;

        // Lazy-load handovers when tab is first selected
        if (MainTabs.SelectedIndex == 2 && !_handoversLoaded)
            await LoadHandoversAsync();

        UpdateActionButton();
    }

    private void UpdateActionButton()
    {
        // Show/hide copy button (only visible on Handovers tab with selection)
        BtnCopyHandover.Visibility = MainTabs.SelectedIndex == 2 && HandoverList.SelectedItem != null
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (MainTabs.SelectedIndex == 0) // New Session tab
        {
            BtnAction.Content = "Start Session";
            var isEnabled = !string.IsNullOrWhiteSpace(PathInput.Text);
            BtnAction.IsEnabled = isEnabled;
            BtnAction.Background = isEnabled ? NewSessionButtonBrush : DisabledButtonBrush;
            BtnAction.Foreground = isEnabled ? EnabledTextBrush : DisabledTextBrush;
        }
        else if (MainTabs.SelectedIndex == 1) // Resume Session tab
        {
            BtnAction.Content = "Resume Selected";
            var isEnabled = SessionList.SelectedItem != null;
            BtnAction.IsEnabled = isEnabled;
            BtnAction.Background = isEnabled ? ResumeButtonBrush : DisabledButtonBrush;
            BtnAction.Foreground = isEnabled ? EnabledTextBrush : DisabledTextBrush;
        }
        else // Handovers tab
        {
            BtnAction.Content = "Start Session";
            var hvm = HandoverList.SelectedItem as HandoverViewModel;
            var isEnabled = hvm != null && !string.IsNullOrEmpty(hvm.RepoPath);
            BtnAction.IsEnabled = isEnabled;
            BtnAction.Background = isEnabled ? NewSessionButtonBrush : DisabledButtonBrush;
            BtnAction.Foreground = isEnabled ? EnabledTextBrush : DisabledTextBrush;
        }
    }

    private void SessionSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_sessionsLoaded || _allSessions == null)
            return;

        var filter = SessionSearchBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(filter))
        {
            SessionList.ItemsSource = _allSessions;
        }
        else
        {
            SessionList.ItemsSource = _allSessions
                .Where(s => s.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.ProjectName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.ProjectPath.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.DisplaySummary.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private void HandoverSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_handoversLoaded || _allHandovers == null)
            return;

        var filter = HandoverSearchBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(filter))
        {
            HandoverList.ItemsSource = _allHandovers;
        }
        else
        {
            HandoverList.ItemsSource = _allHandovers
                .Where(h => h.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || h.RepoDisplay.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || h.DateDisplay.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || (h.SessionName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }
    }

    private void RepoSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyRepoFilter();
    }

    private void ApplyRepoFilter()
    {
        if (_allRepos == null)
            return;

        var filter = RepoSearchBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(filter))
        {
            RepoList.ItemsSource = _allRepos;
        }
        else
        {
            RepoList.ItemsSource = _allRepos
                .Where(r => (r.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                         || (r.Path?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionHistoryViewModel vm)
        {
            SelectedResumeSessionId = vm.ClaudeSessionId;
            SelectedPath = vm.ProjectPath;
            FileLog.Write($"[NewSessionDialog] Session selected: claude={vm.ClaudeSessionId}, path: {vm.ProjectPath}");
        }
        else
        {
            SelectedResumeSessionId = null;
        }

        UpdateActionButton();
    }

    private void RepoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RepoList.SelectedItem is RepositoryConfig repo)
        {
            PathInput.Text = repo.Path;
            SelectedPath = repo.Path;
            SelectedResumeSessionId = null;
            FileLog.Write($"[NewSessionDialog] Repo selected: {repo.Path}");
        }

        UpdateActionButton();
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[NewSessionDialog] BtnBrowse_Click");

        var dialog = new OpenFolderDialog
        {
            Title = "Select Repository Folder"
        };

        if (dialog.ShowDialog(this) == true)
        {
            PathInput.Text = dialog.FolderName;
            SelectedPath = dialog.FolderName;
            SelectedResumeSessionId = null;

            // Clear repo selection
            RepoList.SelectedItem = null;

            // Add to registry if not already there
            if (_registry != null)
            {
                _registry.TryAdd(dialog.FolderName);
                _allRepos = _registry.Repositories.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
                RepoList.ItemsSource = _allRepos;
            }

            UpdateActionButton();
            FileLog.Write($"[NewSessionDialog] Browsed to: {dialog.FolderName}");
        }
    }

    private void BtnRemoveRepo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string path)
            return;

        FileLog.Write($"[NewSessionDialog] BtnRemoveRepo_Click: {path}");

        if (_registry != null)
        {
            _registry.Remove(path);
            _allRepos = _registry.Repositories.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

            // Reapply the current search filter instead of showing all repos
            ApplyRepoFilter();

            // Clear selection and path input if the removed repo was selected
            if (PathInput.Text == path)
            {
                PathInput.Text = string.Empty;
                SelectedPath = null;
                RepoList.SelectedItem = null;
                UpdateActionButton();
            }

            FileLog.Write($"[NewSessionDialog] Removed repository: {path}");
        }
    }

    private void BtnCoaching_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string category)
            return;

        FileLog.Write($"[NewSessionDialog] BtnCoaching_Click: category={category}");

        SelectedPath = category == "log"
            ? CcStorage.Logs()
            : CcStorage.CoachingCategory(category);
        SelectedResumeSessionId = null;
        BypassPermissionsCheckBox.IsChecked = true;

        FileLog.Write($"[NewSessionDialog] BtnCoaching_Click: starting session at {SelectedPath}");
        DialogResult = true;
    }

    private void BtnAction_Click(object sender, RoutedEventArgs e)
    {
        if (MainTabs.SelectedIndex == 0) // New Session tab
        {
            SelectedPath = PathInput.Text;
            SelectedResumeSessionId = null;

            if (string.IsNullOrWhiteSpace(SelectedPath))
            {
                FileLog.Write("[NewSessionDialog] BtnAction_Click: No path specified for new session");
                return;
            }

            FileLog.Write($"[NewSessionDialog] BtnAction_Click: Starting new session at {SelectedPath}");
            DialogResult = true;
        }
        else if (MainTabs.SelectedIndex == 1) // Resume Session tab
        {
            if (SessionList.SelectedItem is not SessionHistoryViewModel vm)
            {
                FileLog.Write("[NewSessionDialog] BtnAction_Click: No session selected for resume");
                return;
            }

            SelectedResumeSessionId = vm.ClaudeSessionId;
            SelectedPath = vm.ProjectPath;

            FileLog.Write($"[NewSessionDialog] BtnAction_Click: Resuming session claude={vm.ClaudeSessionId}, path={vm.ProjectPath}");
            DialogResult = true;
        }
        else // Handovers tab - Start Session
        {
            if (HandoverList.SelectedItem is not HandoverViewModel hvm || string.IsNullOrEmpty(hvm.RepoPath))
                return;

            SelectedPath = hvm.RepoPath;
            SelectedResumeSessionId = null;
            SelectedHandoverPath = hvm.FilePath;

            FileLog.Write($"[NewSessionDialog] BtnAction_Click: Starting session from handover, repo={hvm.RepoPath}, handover={hvm.FilePath}");
            DialogResult = true;
        }
    }

    private async Task LoadHandoversAsync()
    {
        FileLog.Write("[NewSessionDialog] LoadHandoversAsync: starting");

        try
        {
            var dir = CcStorage.VaultHandovers();
            var files = await Task.Run(() =>
            {
                if (!Directory.Exists(dir))
                    return Array.Empty<string>();
                return Directory.GetFiles(dir, "*.md")
                    .OrderByDescending(f => Path.GetFileName(f))
                    .ToArray();
            });

            _handoversLoaded = true;
            HandoverLoadingText.Visibility = Visibility.Collapsed;

            if (files.Length > 0)
            {
                _allHandovers = files.Select(f => new HandoverViewModel(f)).ToList();
                HandoverList.ItemsSource = _allHandovers;
                HandoverList.Visibility = Visibility.Visible;
                FileLog.Write($"[NewSessionDialog] LoadHandoversAsync: found {files.Length} handovers");
            }
            else
            {
                NoHandoversText.Visibility = Visibility.Visible;
                FileLog.Write("[NewSessionDialog] LoadHandoversAsync: no handovers found");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[NewSessionDialog] LoadHandoversAsync FAILED: {ex.Message}");
            HandoverLoadingText.Visibility = Visibility.Collapsed;
            NoHandoversText.Text = "Error loading handovers";
            NoHandoversText.Visibility = Visibility.Visible;
        }
    }

    private async void HandoverList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HandoverList.SelectedItem is not HandoverViewModel hvm)
        {
            HandoverPreview.Visibility = Visibility.Collapsed;
            HandoverPlaceholder.Visibility = Visibility.Visible;
            HandoverRepoPath.Text = "No handover selected";
            UpdateActionButton();
            return;
        }

        FileLog.Write($"[NewSessionDialog] HandoverList_SelectionChanged: {hvm.FilePath}");

        // Update repo path header
        HandoverRepoPath.Text = !string.IsNullOrEmpty(hvm.RepoPath)
            ? hvm.RepoPath
            : "Repository path not found in handover";

        var content = await Task.Run(() => File.ReadAllText(hvm.FilePath));
        HandoverPreview.Text = content;
        HandoverPreview.Visibility = Visibility.Visible;
        HandoverPlaceholder.Visibility = Visibility.Collapsed;
        UpdateActionButton();
    }

    private void BtnCopyHandover_Click(object sender, RoutedEventArgs e)
    {
        if (HandoverList.SelectedItem is not HandoverViewModel hvm)
            return;

        var content = File.ReadAllText(hvm.FilePath);
        Clipboard.SetText(content);
        FileLog.Write($"[NewSessionDialog] BtnCopyHandover_Click: Copied to clipboard: {hvm.FilePath}");

        // Brief visual feedback
        BtnCopyHandover.Content = "Copied!";
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) =>
        {
            BtnCopyHandover.Content = "Copy to Clipboard";
            timer.Stop();
        };
        timer.Start();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[NewSessionDialog] BtnCancel_Click");
        DialogResult = false;
    }
}
