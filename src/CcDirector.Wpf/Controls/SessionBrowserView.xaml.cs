using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;

namespace CcDirector.Wpf.Controls;

/// <summary>
/// Session browser showing all historical Claude sessions grouped by project.
/// </summary>
public partial class SessionBrowserView : UserControl
{
    // Frozen brushes
    private static readonly SolidColorBrush ProjectHeaderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)));
    private static readonly SolidColorBrush CardBackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)));
    private static readonly SolidColorBrush CardHoverBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)));
    private static readonly SolidColorBrush SummaryBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)));
    private static readonly SolidColorBrush SecondaryBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));
    private static readonly SolidColorBrush BranchBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)));
    private static readonly SolidColorBrush SeparatorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)));

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Courier New");

    private List<ClaudeSessionMetadata> _allSessions = new();

    /// <summary>
    /// Fired when the user double-clicks a session to resume it.
    /// Parameters: (repoPath, sessionId).
    /// </summary>
    public event Action<string, string>? SessionResumeRequested;

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    public SessionBrowserView()
    {
        InitializeComponent();
        Loaded += SessionBrowserView_Loaded;
    }

    private void SessionBrowserView_Loaded(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[SessionBrowserView] Loaded");
        _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        FileLog.Write("[SessionBrowserView] LoadAsync");
        LoadingText.Visibility = Visibility.Visible;
        ContentScroller.Visibility = Visibility.Collapsed;

        var sessions = await Task.Run(ClaudeSessionReader.ScanAllProjects);

        _allSessions = sessions;
        BuildTree(_allSessions);

        LoadingText.Visibility = Visibility.Collapsed;
        ContentScroller.Visibility = Visibility.Visible;
        FileLog.Write($"[SessionBrowserView] Loaded {sessions.Count} sessions");
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[SessionBrowserView] BtnRefresh_Click");
        await LoadAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        FileLog.Write($"[SessionBrowserView] SearchBox_TextChanged: query=\"{query}\"");

        if (string.IsNullOrEmpty(query))
        {
            BuildTree(_allSessions);
            return;
        }

        var filtered = _allSessions.Where(s =>
            (s.ProjectPath != null && s.ProjectPath.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
            (s.Summary != null && s.Summary.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
            (s.FirstPrompt != null && s.FirstPrompt.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
            (s.GitBranch != null && s.GitBranch.Contains(query, StringComparison.OrdinalIgnoreCase))
        ).ToList();

        BuildTree(filtered);
    }

    private void BuildTree(List<ClaudeSessionMetadata> sessions)
    {
        FileLog.Write($"[SessionBrowserView] BuildTree: {sessions.Count} sessions");
        ProjectsPanel.Children.Clear();

        // Group by ProjectPath, sort groups by most recent session
        var groups = sessions
            .GroupBy(s => s.ProjectPath ?? "(unknown)")
            .OrderByDescending(g => g.Max(s => s.Modified))
            .ToList();

        foreach (var group in groups)
        {
            AddProjectGroup(group.Key, group.OrderByDescending(s => s.Modified).ToList());
        }
    }

    private void AddProjectGroup(string projectPath, List<ClaudeSessionMetadata> sessions)
    {
        var projectName = GetProjectName(projectPath);

        // Project header (clickable to expand/collapse)
        var headerPanel = new DockPanel
        {
            Margin = new Thickness(0, 12, 0, 4),
            Cursor = Cursors.Hand
        };

        var expandIndicator = new TextBlock
        {
            Text = "[-]",
            Foreground = SecondaryBrush,
            FontSize = 12,
            FontFamily = MonoFont,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        headerPanel.Children.Add(expandIndicator);

        var headerText = new TextBlock
        {
            Text = projectName,
            Foreground = ProjectHeaderBrush,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerPanel.Children.Add(headerText);

        var countText = new TextBlock
        {
            Text = $"  ({sessions.Count})",
            Foreground = SecondaryBrush,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerPanel.Children.Add(countText);

        ProjectsPanel.Children.Add(headerPanel);

        // Sessions container
        var sessionsPanel = new StackPanel();

        foreach (var session in sessions)
        {
            AddSessionItem(sessionsPanel, session, projectPath);
        }

        ProjectsPanel.Children.Add(sessionsPanel);

        // Toggle expand/collapse
        headerPanel.MouseLeftButtonDown += (_, _) =>
        {
            if (sessionsPanel.Visibility == Visibility.Visible)
            {
                sessionsPanel.Visibility = Visibility.Collapsed;
                expandIndicator.Text = "[+]";
            }
            else
            {
                sessionsPanel.Visibility = Visibility.Visible;
                expandIndicator.Text = "[-]";
            }
        };
    }

    private void AddSessionItem(StackPanel parent, ClaudeSessionMetadata session, string projectPath)
    {
        var card = new Border
        {
            Background = CardBackgroundBrush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 2, 0, 2),
            Cursor = Cursors.Hand,
            Tag = session
        };

        var stack = new StackPanel();

        // Summary / first prompt line
        var summaryText = GetSessionDisplayText(session);
        var summaryBlock = new TextBlock
        {
            Text = summaryText,
            Foreground = SummaryBrush,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = summaryText
        };
        stack.Children.Add(summaryBlock);

        // Details line: message count, time ago, git branch
        var detailsPanel = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };

        var messageCountBlock = new TextBlock
        {
            Text = $"{session.MessageCount} msgs",
            Foreground = SecondaryBrush,
            FontSize = 11,
            FontFamily = MonoFont,
            Margin = new Thickness(0, 0, 12, 0)
        };
        detailsPanel.Children.Add(messageCountBlock);

        if (session.Modified != DateTime.MinValue)
        {
            var timeAgoBlock = new TextBlock
            {
                Text = TimeAgo(session.Modified),
                Foreground = SecondaryBrush,
                FontSize = 11,
                FontFamily = MonoFont,
                Margin = new Thickness(0, 0, 12, 0)
            };
            detailsPanel.Children.Add(timeAgoBlock);
        }

        if (!string.IsNullOrEmpty(session.GitBranch))
        {
            var branchBlock = new TextBlock
            {
                Text = session.GitBranch,
                Foreground = BranchBrush,
                FontSize = 11,
                FontFamily = MonoFont
            };
            detailsPanel.Children.Add(branchBlock);
        }

        stack.Children.Add(detailsPanel);
        card.Child = stack;

        // Hover effect
        card.MouseEnter += (_, _) => card.Background = CardHoverBrush;
        card.MouseLeave += (_, _) => card.Background = CardBackgroundBrush;

        // Double-click to resume
        card.MouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount == 2)
            {
                FileLog.Write($"[SessionBrowserView] SessionResumeRequested: repo={projectPath}, session={session.SessionId}");
                SessionResumeRequested?.Invoke(projectPath, session.SessionId);
            }
        };

        // Context menu
        var contextMenu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)),
            Foreground = SummaryBrush,
            BorderBrush = SeparatorBrush
        };

        var resumeItem = new MenuItem { Header = "Resume Session" };
        resumeItem.Click += (_, _) =>
        {
            FileLog.Write($"[SessionBrowserView] ContextMenu Resume: repo={projectPath}, session={session.SessionId}");
            SessionResumeRequested?.Invoke(projectPath, session.SessionId);
        };
        contextMenu.Items.Add(resumeItem);

        var copyIdItem = new MenuItem { Header = "Copy Session ID" };
        copyIdItem.Click += (_, _) =>
        {
            FileLog.Write($"[SessionBrowserView] ContextMenu CopyId: {session.SessionId}");
            Clipboard.SetText(session.SessionId);
        };
        contextMenu.Items.Add(copyIdItem);

        card.ContextMenu = contextMenu;

        parent.Children.Add(card);
    }

    private static string GetSessionDisplayText(ClaudeSessionMetadata session)
    {
        if (!string.IsNullOrWhiteSpace(session.Summary))
            return session.Summary;

        if (!string.IsNullOrWhiteSpace(session.FirstPrompt))
        {
            var text = session.FirstPrompt.Trim();
            if (text.Length > 80)
                text = text[..80] + "...";
            return text;
        }

        return "(no summary)";
    }

    private static string GetProjectName(string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath))
            return "(unknown)";

        // Get last segment of the path
        var trimmed = projectPath.TrimEnd('\\', '/');
        var lastSep = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        if (lastSep >= 0 && lastSep < trimmed.Length - 1)
            return trimmed[(lastSep + 1)..];

        return trimmed;
    }

    private static string TimeAgo(DateTime dt)
    {
        var span = DateTime.Now - dt;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        if (span.TotalDays < 30) return $"{(int)(span.TotalDays / 7)}w ago";
        return dt.ToString("yyyy-MM-dd");
    }
}
