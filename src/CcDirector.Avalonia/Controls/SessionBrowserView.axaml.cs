using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Browses all Claude Code sessions across all projects.
/// Groups by project path with collapsible headers.
/// Follows Attach/Detach pattern (stateless -- just call LoadAsync).
/// </summary>
public partial class SessionBrowserView : UserControl
{
    private static readonly ISolidColorBrush ProjectNameBrush = new SolidColorBrush(Color.Parse("#569CD6"));
    private static readonly ISolidColorBrush SecondaryBrush = new SolidColorBrush(Color.Parse("#888888"));
    private static readonly ISolidColorBrush CardBackgroundBrush = new SolidColorBrush(Color.Parse("#252526"));
    private static readonly ISolidColorBrush CardHoverBrush = new SolidColorBrush(Color.Parse("#2D2D30"));
    private static readonly ISolidColorBrush TextBrush = new SolidColorBrush(Color.Parse("#CCCCCC"));
    private static readonly ISolidColorBrush IndicatorBrush = new SolidColorBrush(Color.Parse("#888888"));
    private const string MonoFont = "Cascadia Mono,Consolas,monospace";

    private List<ClaudeSessionMetadata> _allSessions = new();

    /// <summary>Fired when user double-clicks a session to resume it. Args: (repoPath, sessionId).</summary>
    public event Action<string, string>? SessionResumeRequested;

    public SessionBrowserView()
    {
        InitializeComponent();
    }

    /// <summary>Load all sessions from disk. Call this when the view becomes visible.</summary>
    public async Task LoadAsync()
    {
        FileLog.Write("[SessionBrowserView] LoadAsync");
        LoadingText.IsVisible = true;
        ContentScroller.IsVisible = false;
        EmptyText.IsVisible = false;

        _allSessions = await Task.Run(ClaudeSessionReader.ScanAllProjects);

        FileLog.Write($"[SessionBrowserView] LoadAsync: found {_allSessions.Count} sessions");

        BuildTree(_allSessions);

        LoadingText.IsVisible = false;
        ContentScroller.IsVisible = true;
        EmptyText.IsVisible = _allSessions.Count == 0;
    }

    private void BtnRefresh_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SessionBrowserView] BtnRefresh_Click");
        _ = LoadAsync();
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(query))
        {
            BuildTree(_allSessions);
            return;
        }

        var filtered = _allSessions.Where(s =>
            (s.ProjectPath ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (s.Summary ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (s.FirstPrompt ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (s.GitBranch ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)
        ).ToList();

        BuildTree(filtered);
        EmptyText.IsVisible = filtered.Count == 0 && _allSessions.Count > 0;
    }

    private void BuildTree(List<ClaudeSessionMetadata> sessions)
    {
        TreeContainer.Children.Clear();

        var groups = sessions
            .GroupBy(s => s.ProjectPath ?? "(unknown)")
            .OrderByDescending(g => g.Max(s => s.Modified));

        foreach (var group in groups)
        {
            AddProjectGroup(group.Key, group.OrderByDescending(s => s.Modified).ToList());
        }
    }

    private void AddProjectGroup(string projectPath, List<ClaudeSessionMetadata> sessions)
    {
        var projectName = GetProjectName(projectPath);

        // Expand indicator
        var indicator = new TextBlock
        {
            Text = "[-]",
            Foreground = IndicatorBrush,
            FontFamily = new FontFamily(MonoFont),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };

        // Project name
        var nameText = new TextBlock
        {
            Text = projectName,
            Foreground = ProjectNameBrush,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Count
        var countText = new TextBlock
        {
            Text = $"({sessions.Count})",
            Foreground = SecondaryBrush,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4, 8, 4, 4),
            Children = { indicator, nameText, countText },
        };

        // Session cards container
        var sessionPanel = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        foreach (var session in sessions)
        {
            AddSessionItem(sessionPanel, session);
        }

        // Toggle expand/collapse on header click
        header.PointerPressed += (_, _) =>
        {
            sessionPanel.IsVisible = !sessionPanel.IsVisible;
            indicator.Text = sessionPanel.IsVisible ? "[-]" : "[+]";
        };
        header.Cursor = new Cursor(StandardCursorType.Hand);

        TreeContainer.Children.Add(header);
        TreeContainer.Children.Add(sessionPanel);
    }

    private void AddSessionItem(StackPanel container, ClaudeSessionMetadata session)
    {
        var displayText = GetSessionDisplayText(session);

        // Summary
        var summaryText = new TextBlock
        {
            Text = displayText,
            Foreground = TextBrush,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
        };
        ToolTip.SetTip(summaryText, displayText);

        // Details row
        var details = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0),
        };

        details.Children.Add(new TextBlock
        {
            Text = $"{session.MessageCount} msgs",
            Foreground = SecondaryBrush,
            FontFamily = new FontFamily(MonoFont),
            FontSize = 10,
            Margin = new Thickness(0, 0, 12, 0),
        });

        details.Children.Add(new TextBlock
        {
            Text = TimeAgo(session.Modified),
            Foreground = SecondaryBrush,
            FontFamily = new FontFamily(MonoFont),
            FontSize = 10,
            Margin = new Thickness(0, 0, 12, 0),
        });

        if (!string.IsNullOrEmpty(session.GitBranch))
        {
            details.Children.Add(new TextBlock
            {
                Text = session.GitBranch,
                Foreground = ProjectNameBrush,
                FontFamily = new FontFamily(MonoFont),
                FontSize = 10,
            });
        }

        var content = new StackPanel
        {
            Children = { summaryText, details },
        };

        var card = new Border
        {
            Background = CardBackgroundBrush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 2),
            Child = content,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = session,
        };

        // Hover effect
        card.PointerEntered += (_, _) => card.Background = CardHoverBrush;
        card.PointerExited += (_, _) => card.Background = CardBackgroundBrush;

        // Double-click to resume
        card.DoubleTapped += (_, _) =>
        {
            if (card.Tag is not ClaudeSessionMetadata meta) return;
            FileLog.Write($"[SessionBrowserView] Resume requested: session={meta.SessionId}, project={meta.ProjectPath}");
            SessionResumeRequested?.Invoke(meta.ProjectPath ?? "", meta.SessionId);
        };

        // Context menu
        var resumeItem = new MenuItem { Header = "Resume Session" };
        resumeItem.Click += (_, _) =>
        {
            if (card.Tag is not ClaudeSessionMetadata meta) return;
            SessionResumeRequested?.Invoke(meta.ProjectPath ?? "", meta.SessionId);
        };

        var copyIdItem = new MenuItem { Header = "Copy Session ID" };
        copyIdItem.Click += async (_, _) =>
        {
            if (card.Tag is not ClaudeSessionMetadata meta) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(meta.SessionId);
        };

        card.ContextMenu = new ContextMenu
        {
            Items = { resumeItem, copyIdItem },
        };

        container.Children.Add(card);
    }

    private static string GetSessionDisplayText(ClaudeSessionMetadata session)
    {
        if (!string.IsNullOrWhiteSpace(session.Summary))
            return session.Summary;

        if (!string.IsNullOrWhiteSpace(session.FirstPrompt))
        {
            var prompt = session.FirstPrompt;
            return prompt.Length > 80 ? prompt[..80] + "..." : prompt;
        }

        return "(no summary)";
    }

    private static string GetProjectName(string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath))
            return "(unknown)";

        // Get last segment
        var trimmed = projectPath.TrimEnd('/', '\\');
        var lastSep = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        return lastSep >= 0 ? trimmed[(lastSep + 1)..] : trimmed;
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
