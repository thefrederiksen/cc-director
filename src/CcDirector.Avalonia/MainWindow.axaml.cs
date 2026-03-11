using System.Collections.ObjectModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CcDirector.Core.Pipes;
using CcDirector.Core.Sessions;
using CcDirector.Core.Skills;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

// ==================== VIEW MODELS ====================

public class HookEventViewModel
{
    private static readonly Dictionary<string, ISolidColorBrush> EventBrushes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Stop"] = new SolidColorBrush(Color.Parse("#22C55E")),
        ["Notification"] = new SolidColorBrush(Color.Parse("#F59E0B")),
    };
    private static readonly ISolidColorBrush DefaultBrush = new SolidColorBrush(Color.Parse("#AAAAAA"));

    public string Timestamp { get; }
    public string SessionId { get; }
    public string SessionIdShort { get; }
    public string EventName { get; }
    public string Detail { get; }
    public ISolidColorBrush EventBrush { get; }

    public HookEventViewModel(PipeMessage msg)
    {
        Timestamp = DateTime.Now.ToString("HH:mm:ss");
        EventName = msg.HookEventName ?? "Unknown";
        SessionId = msg.SessionId ?? "";
        SessionIdShort = SessionId.Length > 8 ? SessionId.Substring(0, 8) : SessionId;
        Detail = BuildDetail(msg);
        EventBrush = EventBrushes.GetValueOrDefault(EventName, DefaultBrush);
    }

    private static string BuildDetail(PipeMessage msg)
    {
        if (!string.IsNullOrEmpty(msg.ToolName))
            return msg.ToolName;
        if (!string.IsNullOrEmpty(msg.Message))
            return msg.Message;
        if (!string.IsNullOrEmpty(msg.Prompt))
            return msg.Prompt.Length > 100 ? msg.Prompt.Substring(0, 100) + "..." : msg.Prompt;
        if (!string.IsNullOrEmpty(msg.Reason))
            return msg.Reason;
        return "";
    }
}

public class QueueItemViewModel
{
    public Guid Id { get; init; }
    public string Index { get; init; } = "";
    public string Preview { get; init; } = "";
    public string FullText { get; init; } = "";
}

public class ScreenshotViewModel
{
    public string FilePath { get; }
    public string FileName { get; }
    public string TimeLabel { get; }
    public Bitmap? Thumbnail { get; }

    public ScreenshotViewModel(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        TimeLabel = File.GetLastWriteTime(filePath).ToString("MMM d, h:mm tt");

        try
        {
            using var stream = File.OpenRead(filePath);
            Thumbnail = new Bitmap(stream);
        }
        catch
        {
            Thumbnail = null;
        }
    }
}

// ==================== MAIN WINDOW ====================

public partial class MainWindow : Window
{
    private SessionManager _sessionManager = null!;
    private readonly ObservableCollection<SessionViewModel> _sessions = new();
    private SessionViewModel? _activeSession;

    // Slash command autocomplete
    private readonly SlashCommandProvider _slashCommandProvider = new();
    private List<SlashCommandItem> _filteredSlashCommands = new();

    // Right panel state
    private bool _rightPanelExpanded = true;
    private readonly ObservableCollection<HookEventViewModel> _hookEvents = new();
    private readonly List<HookEventViewModel> _allHookEvents = new();
    private readonly ObservableCollection<QueueItemViewModel> _queueItems = new();
    private readonly ObservableCollection<ScreenshotViewModel> _screenshots = new();
    private FileSystemWatcher? _screenshotWatcher;
    private DispatcherTimer? _screenshotDebounceTimer;
    private string? _screenshotsDirectory;

    public MainWindow()
    {
        InitializeComponent();
        FileLog.Write("[MainWindow] Avalonia MainWindow initialized");

        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] MainWindow_Loaded");

        var app = (App)global::Avalonia.Application.Current!;
        _sessionManager = app.SessionManager;

        SessionList.ItemsSource = _sessions;
        HookEventList.ItemsSource = _hookEvents;
        QueueItemsList.ItemsSource = _queueItems;
        ScreenshotList.ItemsSource = _screenshots;

        // Wire up hook event routing
        app.EventRouter.OnRawMessage += OnHookEventReceived;

        // Wire source control view file event
        GitChangesView.ViewFileRequested += OnGitViewFileRequested;

        // Wire usage dashboard to usage service
        UsageDashboardView.SetUsageService(app.ClaudeUsageService);

        // Wire prompt input text changes for slash command autocomplete
        PromptInput.TextChanged += PromptInput_TextChanged;

        SetBuildInfo();
        _ = InitializeScreenshotsPanelAsync();
        _ = ShowStartupWorkspacePicker();
    }

    private void SetBuildInfo()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (exePath != null && File.Exists(exePath))
            {
                var buildTime = File.GetLastWriteTime(exePath);
                BuildInfoText.Text = $"Build: {buildTime:HH:mm:ss}";
                ToolTip.SetTip(BuildInfoText, $"Built: {buildTime:yyyy-MM-dd HH:mm:ss}\nPath: {exePath}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] SetBuildInfo FAILED: {ex.Message}");
            BuildInfoText.Text = "Build: unknown";
        }
    }

    // ==================== WORKSPACE STARTUP ====================

    private async Task ShowStartupWorkspacePicker()
    {
        var app = (App)global::Avalonia.Application.Current!;

        if (!app.WorkspaceStore.LoadAll().Any())
        {
            FileLog.Write("[MainWindow] ShowStartupWorkspacePicker: no saved workspaces");
            return;
        }

        FileLog.Write("[MainWindow] ShowStartupWorkspacePicker: showing workspace picker");

        var dialog = new LoadWorkspaceDialog(app.WorkspaceStore, startupMode: true);
        dialog.SetOwner(this);
        var result = await dialog.ShowDialog<bool?>(this);

        if (result != true || dialog.SelectedWorkspace == null)
        {
            FileLog.Write("[MainWindow] ShowStartupWorkspacePicker: user skipped");
            return;
        }

        await LoadWorkspaceAsync(dialog.SelectedWorkspace);
    }

    private async Task LoadWorkspaceAsync(WorkspaceDefinition workspace)
    {
        FileLog.Write($"[MainWindow] LoadWorkspaceAsync: '{workspace.Name}' with {workspace.Sessions.Count} sessions");

        var sorted = workspace.Sessions.OrderBy(s => s.SortOrder).ToList();
        int total = sorted.Count;

        for (int i = 0; i < total; i++)
        {
            var entry = sorted[i];
            FileLog.Write($"[MainWindow] LoadWorkspaceAsync: creating session {i + 1}/{total}: {entry.RepoPath}");

            var vm = CreateSession(entry.RepoPath, claudeArgs: entry.ClaudeArgs);
            if (vm != null)
                vm.Rename(entry.CustomName, entry.CustomColor);

            // Delay between sessions to prevent Claude Code settings corruption
            if (i < total - 1)
                await Task.Delay(2500);
        }

        FileLog.Write($"[MainWindow] LoadWorkspaceAsync: workspace '{workspace.Name}' loaded");
    }

    // ==================== SESSION MANAGEMENT ====================

    private SessionViewModel? CreateSession(string repoPath, string? resumeSessionId = null, string? claudeArgs = null)
    {
        FileLog.Write($"[MainWindow] CreateSession: repoPath={repoPath}, args={claudeArgs ?? "default"}");
        try
        {
            var session = _sessionManager.CreateSession(repoPath, claudeArgs);
            FileLog.Write($"[MainWindow] CreateSession: session created, id={session.Id}, pid={session.ProcessId}");

            var vm = new SessionViewModel(session);
            _sessions.Add(vm);
            SessionList.SelectedItem = vm;
            FileLog.Write($"[MainWindow] CreateSession: added to UI");
            return vm;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] CreateSession FAILED: {ex.Message}");
            return null;
        }
    }

    private void SelectSession(SessionViewModel? vm)
    {
        if (vm == _activeSession) return;

        // Detach from previous session
        if (_activeSession != null)
        {
            TerminalHost.Detach();
            GitChangesView.Detach();
            CleanView.Detach();
        }

        _activeSession = vm;

        if (vm == null)
        {
            SessionHeaderBanner.IsVisible = false;
            PlaceholderText.IsVisible = true;
            TerminalHost.IsVisible = false;
            PromptBarBorder.IsVisible = false;
            GitChangesView.Detach();
            CleanView.Detach();
            return;
        }

        // Update header
        SessionHeaderBanner.IsVisible = true;
        HeaderSessionName.Text = vm.DisplayName;
        HeaderActivityLabel.Text = vm.ActivityLabel;

        // Attach terminal
        PlaceholderText.IsVisible = false;
        TerminalHost.IsVisible = true;
        TerminalHost.Attach(vm.Session);

        // Attach source control
        GitChangesView.Attach(vm.Session.RepoPath);

        // Attach clean view (Agent tab)
        CleanView.Attach(vm.Session);

        // Show prompt bar
        PromptBarBorder.IsVisible = true;

        // Refresh right panel for new session
        RefreshHookEventsPanel();
        RefreshQueuePanel();

        FileLog.Write($"[MainWindow] SelectSession: {vm.DisplayName}");
    }

    private async Task CloseAllSessionsAsync()
    {
        FileLog.Write("[MainWindow] CloseAllSessionsAsync");
        TerminalHost.Detach();
        GitChangesView.Detach();
        CleanView.Detach();
        _activeSession = null;

        var snapshots = _sessions.ToList();
        _sessions.Clear();

        foreach (var vm in snapshots)
        {
            try
            {
                await _sessionManager.KillSessionAsync(vm.Session.Id);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] CloseAllSessionsAsync: failed to kill {vm.Session.Id}: {ex.Message}");
            }
            _sessionManager.RemoveSession(vm.Session.Id);
        }

        SessionHeaderBanner.IsVisible = false;
        PlaceholderText.IsVisible = true;
        TerminalHost.IsVisible = false;
        PromptBarBorder.IsVisible = false;

        FileLog.Write($"[MainWindow] CloseAllSessionsAsync: removed {snapshots.Count} session(s)");
    }

    // ==================== EVENT HANDLERS ====================

    private void SessionList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionViewModel vm)
            SelectSession(vm);
    }

    private void BtnNewSession_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnNewSession_Click");
        _ = ShowNewSessionDialog();
    }

    private async Task ShowNewSessionDialog()
    {
        var app = (App)global::Avalonia.Application.Current!;
        var dialog = new NewSessionDialog(
            app.RepositoryRegistry,
            app.SessionHistoryStore);
        var result = await dialog.ShowDialog<bool?>(this);

        if (result == true && dialog.SelectedPath != null)
        {
            CreateSession(dialog.SelectedPath);
        }
    }

    private void BtnAppMenu_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnAppMenu_Click");
        _ = ShowAppMenu();
    }

    private async Task ShowAppMenu()
    {
        var app = (App)global::Avalonia.Application.Current!;

        var menu = new ContextMenu();

        var saveWorkspace = new MenuItem { Header = "Save Workspace..." };
        saveWorkspace.Click += async (_, _) =>
        {
            var sessionData = _sessions.Select(vm => new SessionData(
                vm.DisplayName,
                vm.Session.RepoPath,
                vm.Session.CustomName,
                vm.Session.CustomColor,
                vm.Session.ClaudeArgs));
            var dialog = new SaveWorkspaceDialog(app.WorkspaceStore, sessionData);
            await dialog.ShowDialog<bool?>(this);
        };

        var loadWorkspace = new MenuItem { Header = "Load Workspace..." };
        loadWorkspace.Click += async (_, _) =>
        {
            var dialog = new LoadWorkspaceDialog(app.WorkspaceStore);
            var result = await dialog.ShowDialog<bool?>(this);
            if (result == true && dialog.SelectedWorkspace != null)
            {
                if (_sessions.Count > 0)
                    await CloseAllSessionsAsync();
                await LoadWorkspaceAsync(dialog.SelectedWorkspace);
            }
        };

        var clearWorkspace = new MenuItem { Header = "Clear Workspace" };
        clearWorkspace.Click += async (_, _) =>
        {
            if (_sessions.Count == 0) return;
            await CloseAllSessionsAsync();
        };

        var separator1 = new Separator();

        var openLogs = new MenuItem { Header = "Open Logs" };
        openLogs.Click += (_, _) =>
        {
            var logDir = Path.GetDirectoryName(FileLog.CurrentLogPath);
            if (logDir != null && Directory.Exists(logDir))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = logDir,
                    UseShellExecute = true
                });
            }
        };

        menu.Items.Add(saveWorkspace);
        menu.Items.Add(loadWorkspace);
        menu.Items.Add(clearWorkspace);
        menu.Items.Add(separator1);
        menu.Items.Add(openLogs);

        menu.Open(BtnAppMenu);
    }

    // ==================== LEFT TAB SWITCHING ====================

    private string _activeLeftTab = "Terminal";
    private static readonly IBrush TransparentBrush = Brushes.Transparent;
    private static readonly IBrush InactiveTextBrush = new SolidColorBrush(Color.Parse("#888888"));

    private void AgentTabButton_Click(object? sender, RoutedEventArgs e)
    {
        SwitchLeftTab("Agent");
    }

    private void TerminalTabButton_Click(object? sender, RoutedEventArgs e)
    {
        SwitchLeftTab("Terminal");
    }

    private void SourceControlTabButton_Click(object? sender, RoutedEventArgs e)
    {
        SwitchLeftTab("SourceControl");
    }

    private void SwitchLeftTab(string tab)
    {
        if (_activeLeftTab == tab) return;
        _activeLeftTab = tab;
        FileLog.Write($"[MainWindow] SwitchLeftTab: {tab}");

        var accentBrush = (IBrush)(this.FindResource("AccentBrush") ?? Brushes.DodgerBlue);
        var whiteBrush = Brushes.White;

        // Update button styles
        AgentTabButton.Background = tab == "Agent" ? accentBrush : TransparentBrush;
        AgentTabButton.Foreground = tab == "Agent" ? whiteBrush : InactiveTextBrush;
        TerminalTabButton.Background = tab == "Terminal" ? accentBrush : TransparentBrush;
        TerminalTabButton.Foreground = tab == "Terminal" ? whiteBrush : InactiveTextBrush;
        SourceControlTabButton.Background = tab == "SourceControl" ? accentBrush : TransparentBrush;
        SourceControlTabButton.Foreground = tab == "SourceControl" ? whiteBrush : InactiveTextBrush;

        // Show/hide panels
        AgentPanel.IsVisible = tab == "Agent";
        TerminalPanel.IsVisible = tab == "Terminal";
        SourceControlPanel.IsVisible = tab == "SourceControl";
    }

    private void BtnSend_Click(object? sender, RoutedEventArgs e)
    {
        SendPrompt();
    }

    private void PromptInput_KeyDown(object? sender, KeyEventArgs e)
    {
        // Slash command popup navigation
        if (SlashCommandPopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down:
                    if (SlashCommandList.SelectedIndex < _filteredSlashCommands.Count - 1)
                        SlashCommandList.SelectedIndex++;
                    if (SlashCommandList.SelectedItem is { } downItem)
                        SlashCommandList.ScrollIntoView(downItem);
                    e.Handled = true;
                    return;

                case Key.Up:
                    if (SlashCommandList.SelectedIndex > 0)
                        SlashCommandList.SelectedIndex--;
                    if (SlashCommandList.SelectedItem is { } upItem)
                        SlashCommandList.ScrollIntoView(upItem);
                    e.Handled = true;
                    return;

                case Key.Tab:
                    InsertSelectedSlashCommand();
                    e.Handled = true;
                    return;

                case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                    InsertSelectedSlashCommand();
                    e.Handled = true;
                    return;

                case Key.Escape:
                    SlashCommandPopup.IsOpen = false;
                    e.Handled = true;
                    return;
            }
        }

        // Ctrl+Shift+Enter = Queue prompt
        if (e.Key == Key.Enter && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            QueueCurrentPrompt();
            return;
        }

        // Enter sends, Shift+Enter inserts newline
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            SendPrompt();
        }
    }

    // ==================== SLASH COMMAND AUTOCOMPLETE ====================

    private void PromptInput_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var text = PromptInput.Text ?? "";

        // Only trigger when / is the first non-whitespace character
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("/"))
        {
            SlashCommandPopup.IsOpen = false;
            return;
        }

        // Extract the slash command prefix (text from / to first space)
        var afterSlash = trimmed.Substring(1);
        var spaceIndex = afterSlash.IndexOf(' ');
        var filter = spaceIndex >= 0 ? afterSlash.Substring(0, spaceIndex) : afterSlash;

        // If there's a space after the command, popup should close (command is complete)
        if (spaceIndex >= 0)
        {
            SlashCommandPopup.IsOpen = false;
            return;
        }

        var repoPath = _activeSession?.Session.RepoPath;
        var allCommands = _slashCommandProvider.GetCommands(repoPath);

        _filteredSlashCommands = string.IsNullOrEmpty(filter)
            ? allCommands
            : allCommands.Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (_filteredSlashCommands.Count == 0)
        {
            SlashCommandPopup.IsOpen = false;
            return;
        }

        SlashCommandList.ItemsSource = _filteredSlashCommands;
        SlashCommandList.SelectedIndex = 0;
        SlashCommandPopup.IsOpen = true;
    }

    private void SlashCommandList_Tapped(object? sender, TappedEventArgs e)
    {
        InsertSelectedSlashCommand();
    }

    private void SlashCommandList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SlashCommandList.SelectedItem is not SlashCommandItem selected)
        {
            SlashCommandDocPanel.IsVisible = false;
            return;
        }

        SlashCommandDocTitle.Text = "/" + selected.Name;
        SlashCommandDocSource.Text = selected.Source == "project" ? "Project skill" : "Global skill";
        SlashCommandDocDesc.Text = selected.Description;

        if (!string.IsNullOrWhiteSpace(selected.Documentation))
        {
            SlashCommandDocBody.Text = selected.Documentation;
            SlashCommandDocBody.IsVisible = true;
        }
        else
        {
            SlashCommandDocBody.Text = string.Empty;
            SlashCommandDocBody.IsVisible = false;
        }

        SlashCommandDocPanel.IsVisible = true;
    }

    private void InsertSelectedSlashCommand()
    {
        if (SlashCommandList.SelectedItem is not SlashCommandItem selected)
            return;

        FileLog.Write($"[MainWindow] InsertSelectedSlashCommand: /{selected.Name}");
        PromptInput.Text = "/" + selected.Name + " ";
        PromptInput.CaretIndex = PromptInput.Text.Length;
        SlashCommandPopup.IsOpen = false;
        PromptInput.Focus();
    }

    // ==================== SEND / QUEUE / HANDOVER ====================

    private void SendPrompt()
    {
        if (_activeSession == null) return;

        var text = PromptInput.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        FileLog.Write($"[MainWindow] SendPrompt: {text.Length} chars to session {_activeSession.Session.Id}");

        PromptInput.Text = "";
        ClearNotification();

        CleanView.InjectUserPrompt(text);
        _activeSession.Session.SendText(text + "\n");
    }

    private void BtnQueuePrompt_Click(object? sender, RoutedEventArgs e)
    {
        QueueCurrentPrompt();
    }

    private void QueueCurrentPrompt()
    {
        if (_activeSession == null || string.IsNullOrWhiteSpace(PromptInput.Text))
            return;

        var text = PromptInput.Text.Trim();
        FileLog.Write($"[MainWindow] QueueCurrentPrompt: session={_activeSession.Session.Id}, text=\"{(text.Length > 60 ? text[..60] + "..." : text)}\"");
        _activeSession.Session.PromptQueue?.Enqueue(text);
        PromptInput.Text = "";

        RefreshQueuePanel();

        // Auto-open queue tab
        if (_rightPanelExpanded)
            RightPanelTabs.SelectedItem = QueueTab;

        UpdateQueueButtonStyle();
    }

    private void BtnHandover_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnHandover_Click");
        if (_activeSession == null)
        {
            FileLog.Write("[MainWindow] BtnHandover_Click: no active session");
            return;
        }

        _activeSession.Session.SendText("/handover\n");
        FileLog.Write($"[MainWindow] BtnHandover_Click: sent /handover to session {_activeSession.Session.Id}");
    }

    private void UpdateQueueButtonStyle()
    {
        var queue = _activeSession?.Session.PromptQueue;
        var count = queue?.Count ?? 0;

        BtnQueuePrompt.Content = count > 0 ? $"Queue ({count})" : "Queue";

        if (count > 0)
        {
            BtnQueuePrompt.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            BtnQueuePrompt.Foreground = Brushes.White;
        }
        else
        {
            BtnQueuePrompt.Background = (IBrush)(this.FindResource("ButtonBackground") ?? Brushes.DarkGray);
            BtnQueuePrompt.Foreground = (IBrush)(this.FindResource("TextForeground") ?? Brushes.LightGray);
        }
    }

    // ==================== NOTIFICATION BAR ====================

    private void ShowNotification(string message)
    {
        FileLog.Write($"[MainWindow] ShowNotification: {message}");
        NotificationText.Text = message;
        NotificationIcon.IsVisible = true;
        NotificationBar.IsVisible = true;
    }

    private void ClearNotification()
    {
        NotificationText.Text = string.Empty;
        NotificationIcon.IsVisible = false;
        NotificationBar.IsVisible = false;
    }

    // ==================== RIGHT PANEL TOGGLE ====================

    private void RightPanelToggle_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] RightPanelToggle_Click");
        _rightPanelExpanded = !_rightPanelExpanded;

        if (_rightPanelExpanded)
        {
            RightPanel.IsVisible = true;
            RightPanel.Width = 280;
            RightPanelToggle.Content = "<<";
        }
        else
        {
            RightPanel.IsVisible = false;
            RightPanel.Width = 0;
            RightPanelToggle.Content = ">>";
        }
    }

    // ==================== HOOK EVENTS ====================

    private void OnHookEventReceived(PipeMessage msg)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var vm = new HookEventViewModel(msg);

            _allHookEvents.Add(vm);

            // Cap at 500 events
            if (_allHookEvents.Count > 500)
                _allHookEvents.RemoveAt(0);

            // Show if matches active session or no session selected
            var activeClaudeId = _activeSession?.Session.ClaudeSessionId;
            if (activeClaudeId == null || vm.SessionId == activeClaudeId)
            {
                _hookEvents.Add(vm);
                HookEventsEmptyText.IsVisible = false;
                HookEventList.IsVisible = true;

                // Auto-scroll to bottom
                if (HookEventList.ItemCount > 0)
                    HookEventList.ScrollIntoView(vm);
            }
        });
    }

    private void RefreshHookEventsPanel()
    {
        _hookEvents.Clear();
        var activeClaudeId = _activeSession?.Session.ClaudeSessionId;

        foreach (var evt in _allHookEvents)
        {
            if (activeClaudeId == null || evt.SessionId == activeClaudeId)
                _hookEvents.Add(evt);
        }

        HookEventsEmptyText.IsVisible = _hookEvents.Count == 0;
        HookEventList.IsVisible = _hookEvents.Count > 0;

        if (_hookEvents.Count > 0)
            HookEventList.ScrollIntoView(_hookEvents[_hookEvents.Count - 1]);
    }

    private void BtnClearHookEvents_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnClearHookEvents_Click");
        _hookEvents.Clear();
        HookEventsEmptyText.IsVisible = true;
        HookEventList.IsVisible = false;
    }

    // ==================== QUEUE ====================

    private void RefreshQueuePanel()
    {
        _queueItems.Clear();

        var queue = _activeSession?.Session.PromptQueue;
        if (queue == null || queue.Count == 0)
        {
            UpdateQueueBadge(0);
            return;
        }

        var items = queue.Items;
        for (int i = 0; i < items.Count; i++)
        {
            var text = items[i].Text;
            _queueItems.Add(new QueueItemViewModel
            {
                Id = items[i].Id,
                Index = $"#{i + 1}",
                Preview = text.Length > 300 ? text.Substring(0, 300) + "..." : text,
                FullText = text,
            });
        }

        UpdateQueueBadge(items.Count);
    }

    private void UpdateQueueBadge(int count)
    {
        QueueCountText.Text = count == 1 ? "1 item" : $"{count} items";
        QueueTab.Header = count > 0 ? $"Queue ({count})" : "Queue";
        QueueEmptyText.IsVisible = count == 0;
        QueueItemsList.IsVisible = count > 0;
        UpdateQueueButtonStyle();
    }

    private void BtnClearQueue_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnClearQueue_Click");
        _activeSession?.Session.PromptQueue?.Clear();
        RefreshQueuePanel();
    }

    private void QueueItemPop_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Guid itemId)
            return;

        FileLog.Write($"[MainWindow] QueueItemPop_Click: {itemId}");
        var item = _queueItems.FirstOrDefault(q => q.Id == itemId);
        if (item == null) return;

        // Insert into prompt input
        PromptInput.Text = (PromptInput.Text ?? "") + item.FullText;
        _activeSession?.Session.PromptQueue?.Remove(itemId);
        RefreshQueuePanel();
    }

    private void QueueItemRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Guid itemId)
            return;

        FileLog.Write($"[MainWindow] QueueItemRemove_Click: {itemId}");
        _activeSession?.Session.PromptQueue?.Remove(itemId);
        RefreshQueuePanel();
    }

    // ==================== SCREENSHOTS ====================

    private async Task InitializeScreenshotsPanelAsync()
    {
        FileLog.Write("[MainWindow] InitializeScreenshotsPanelAsync: starting");

        try
        {
            _screenshotsDirectory = await Task.Run(() => ResolveScreenshotsDirectory());

            if (_screenshotsDirectory == null || !Directory.Exists(_screenshotsDirectory))
            {
                FileLog.Write("[MainWindow] InitializeScreenshotsPanelAsync: no screenshots directory found");
                return;
            }

            FileLog.Write($"[MainWindow] InitializeScreenshotsPanelAsync: directory={_screenshotsDirectory}");

            var vms = await Task.Run(() => LoadScreenshotViewModels(_screenshotsDirectory));

            foreach (var vm in vms)
                _screenshots.Add(vm);

            FileLog.Write($"[MainWindow] InitializeScreenshotsPanelAsync: loaded {vms.Count} screenshots");

            // Start file watcher
            _screenshotWatcher = new FileSystemWatcher(_screenshotsDirectory)
            {
                EnableRaisingEvents = true,
                IncludeSubdirectories = false,
            };
            _screenshotWatcher.Created += OnScreenshotFileChanged;
            _screenshotWatcher.Deleted += OnScreenshotFileChanged;
            _screenshotWatcher.Renamed += OnScreenshotFileChanged;

            _screenshotDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300),
            };
            _screenshotDebounceTimer.Tick += async (_, _) =>
            {
                _screenshotDebounceTimer.Stop();
                await RefreshScreenshots();
            };
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] InitializeScreenshotsPanelAsync FAILED: {ex.Message}");
        }
    }

    private static string? ResolveScreenshotsDirectory()
    {
        // Check cc-director config first
        try
        {
            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "cc-director", "config");
            var configPath = Path.Combine(configDir, "config.json");
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("screenshots", out var ss) &&
                    ss.TryGetProperty("source_directory", out var dir))
                {
                    var path = dir.GetString();
                    if (path != null && Directory.Exists(path))
                        return path;
                }
            }
        }
        catch { /* Non-critical */ }

        // Auto-detect OneDrive Screenshots
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var oneDrive = Path.Combine(userProfile, "OneDrive", "Pictures", "Screenshots");
        if (Directory.Exists(oneDrive))
            return oneDrive;

        // Local Pictures/Screenshots
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var local = Path.Combine(pictures, "Screenshots");
        if (Directory.Exists(local))
            return local;

        return null;
    }

    private static List<ScreenshotViewModel> LoadScreenshotViewModels(string directory)
    {
        var extensions = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };
        return Directory.GetFiles(directory)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .Take(50)
            .Select(f => new ScreenshotViewModel(f))
            .ToList();
    }

    private void OnScreenshotFileChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _screenshotDebounceTimer?.Stop();
            _screenshotDebounceTimer?.Start();
        });
    }

    private async Task RefreshScreenshots()
    {
        if (_screenshotsDirectory == null) return;

        FileLog.Write("[MainWindow] RefreshScreenshots");

        var vms = await Task.Run(() => LoadScreenshotViewModels(_screenshotsDirectory));

        _screenshots.Clear();
        foreach (var vm in vms)
            _screenshots.Add(vm);
    }

    private void BtnRefreshScreenshots_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnRefreshScreenshots_Click");
        _ = RefreshScreenshots();
    }

    private void BtnClearScreenshots_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnClearScreenshots_Click");
        // Clear from UI only (not from disk)
        _screenshots.Clear();
    }

    private void ScreenshotView_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string filePath)
            return;

        FileLog.Write($"[MainWindow] ScreenshotView_Click: {filePath}");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] ScreenshotView_Click FAILED: {ex.Message}");
        }
    }

    private async void ScreenshotCopyPath_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string filePath)
            return;

        FileLog.Write($"[MainWindow] ScreenshotCopyPath_Click: {filePath}");
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(filePath);
    }

    private void ScreenshotDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string filePath)
            return;

        FileLog.Write($"[MainWindow] ScreenshotDelete_Click: {filePath}");
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);

            var vm = _screenshots.FirstOrDefault(s => s.FilePath == filePath);
            if (vm != null)
                _screenshots.Remove(vm);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] ScreenshotDelete_Click FAILED: {ex.Message}");
        }
    }

    // ==================== SOURCE CONTROL ====================

    private void OnGitViewFileRequested(string fullPath)
    {
        FileLog.Write($"[MainWindow] OnGitViewFileRequested: {fullPath}");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OnGitViewFileRequested FAILED: {ex.Message}");
        }
    }

    // ==================== WINDOW CLOSING ====================

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        FileLog.Write("[MainWindow] OnClosing");

        // Detach terminal, source control, clean view, and usage dashboard
        TerminalHost.Detach();
        GitChangesView.Detach();
        CleanView.Detach();
        UsageDashboardView.Detach();
        _activeSession = null;

        // Cleanup screenshot watcher
        _screenshotDebounceTimer?.Stop();
        _screenshotWatcher?.Dispose();

        // Unwire hook events
        try
        {
            var app = (App)global::Avalonia.Application.Current!;
            app.EventRouter.OnRawMessage -= OnHookEventReceived;
        }
        catch { /* App may be shutting down */ }

        base.OnClosing(e);
    }
}
