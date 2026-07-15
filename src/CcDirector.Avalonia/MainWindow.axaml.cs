using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CcDirector.Avalonia.HostedAi;
using CcDirector.Core.HostedAi;
using Avalonia.VisualTree;
using CcDirector.ControlApi;
using CcDirector.Core.Account;
using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.GatewayConnection;
using CcDirector.Core.Home;
using CcDirector.Core.Network;
using CcDirector.Core.Onboarding;
using CcDirector.Core.Sessions;
using CcDirector.Core.Settings;
using CcDirector.Core.Skills;
using CcDirector.Core.Tools;
using CcDirector.Core.Utilities;
using FileViewerControls = CcDirector.Avalonia.Controls;

namespace CcDirector.Avalonia;

// ==================== VIEW MODELS ====================

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

    // Session git status polling
    private readonly CcDirector.Core.Git.GitStatusProvider _gitStatusProvider = new();
    private global::Avalonia.Threading.DispatcherTimer? _sessionGitTimer;
    private global::Avalonia.Threading.DispatcherTimer? _dictationLockTimer;
    private bool _sessionGitRefreshRunning;

    // Interactive TUI mode
    private bool _isInteractiveTuiMode;

    /// <summary>
    /// Claude Code slash commands that launch interactive TUI dialogs requiring direct keyboard input.
    /// When sent from PromptInput, these are intercepted and handled via native dialogs or redirected.
    /// When typed directly in the Terminal tab's ConPTY, they still work natively.
    /// </summary>
    private static readonly HashSet<string> InteractiveTuiCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "config", "settings",
        "status",
        "help",
        "context",
        "copy",
        "diff",
        "hooks",
        "model",
        "theme",
        "permissions", "allowed-tools",
        "resume", "continue",
        "rewind", "checkpoint",
        "export",
        "output-style",
        "memory",
        "stats",
        "plugin",
        "mcp",
        "agents",
    };

    // Terminal scrollbar state
    private bool _updatingScrollBar;

    // Right panel state
    private bool _rightPanelExpanded = true;
    private readonly ObservableCollection<QueueItemViewModel> _queueItems = new();
    private readonly ObservableCollection<ScreenshotViewModel> _screenshots = new();
    private FileSystemWatcher? _screenshotWatcher;
    private DispatcherTimer? _screenshotDebounceTimer;
    private string? _screenshotsDirectory;
    // The image file types the Screenshots panel loads and clears. Kept in one place so listing
    // (LoadScreenshotViewModels) and Clear All (DeleteAllScreenshots) agree on what a screenshot is.
    private static readonly string[] ScreenshotExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    public MainWindow()
    {
        InitializeComponent();
        FileLog.Write("[MainWindow] Avalonia MainWindow initialized");

        Loaded += MainWindow_Loaded;
        Activated += MainWindow_Activated;

        // Register KeyDown as tunnel so it fires before AcceptsReturn consumes Ctrl+Enter
        PromptInput.AddHandler(KeyDownEvent, PromptInput_KeyDown, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Window-level Ctrl+H = open Speak dialog. Tunnel routing so the embedded
        // terminal panel does not eat the keystroke (xterm treats Ctrl+H as
        // Backspace). Gated on the prompt bar being visible -- same condition
        // that gates the Speak button itself, i.e. Terminal tab with an active
        // session.
        AddHandler(KeyDownEvent, MainWindow_KeyDown, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);

        AddHandler(DragDrop.DropEvent, PromptInput_Drop);
        AddHandler(DragDrop.DragOverEvent, PromptInput_DragOver);

        TerminalHost.ScrollChanged += OnTerminalScrollChanged;
        TerminalHost.ViewFileRequested += OnTerminalViewFileRequested;
        TerminalHost.BrowserLaunchFailed += OnTerminalBrowserLaunchFailed;
        TerminalScrollBar.PropertyChanged += TerminalScrollBar_PropertyChanged;

        SessionList.AddHandler(DragDrop.DragOverEvent, SessionList_DragOver);
        SessionList.AddHandler(DragDrop.DropEvent, SessionList_Drop);
        SessionList.AddHandler(PointerPressedEvent, SessionList_PointerPressed, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Alpha gating: Start FIFO and Handover are alpha features, hidden by default.
        // Re-gate live when the flag is toggled in the Settings dialog.
        ApplyAlphaFeatureVisibility();
        AlphaMode.Changed += OnAlphaModeChanged;
        Closed += (_, _) => AlphaMode.Changed -= OnAlphaModeChanged;

        BuildNativeMenu();
    }

    private void OnAlphaModeChanged()
    {
        // The flag could be toggled off the UI thread (e.g. a future REST write); always hop to it.
        // BuildNativeMenu is rebuilt too because the Session menu's Start FIFO item is alpha-gated.
        Dispatcher.UIThread.Post(() =>
        {
            ApplyAlphaFeatureVisibility();
            BuildNativeMenu();
        });
    }

    /// <summary>Show or hide the alpha-gated toolbar/prompt-bar buttons per the alpha flag.</summary>
    private void ApplyAlphaFeatureVisibility()
    {
        var alpha = AlphaMode.IsEnabled;
        BtnStartFifo.IsVisible = alpha;
        BtnHandover.IsVisible = alpha;
        FileLog.Write($"[MainWindow] ApplyAlphaFeatureVisibility: alphaFeatures={alpha}");
    }

    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (_activeLeftTab == "Terminal" && _activeSession != null)
            Dispatcher.UIThread.Post(() => TerminalHost.Focus());
        else
            Dispatcher.UIThread.Post(() => PromptInput.Focus());
    }

    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] MainWindow_Loaded");

        var app = (App)global::Avalonia.Application.Current!;
        _sessionManager = app.SessionManager;

        SessionList.ItemsSource = _sessions;
        SlimSessionList.ItemsSource = _sessions;
        QueueItemsList.ItemsSource = _queueItems;
        ScreenshotList.ItemsSource = _screenshots;

        // Restore the persisted sidebar collapsed state (no re-persist on restore).
        if (SidebarConfig.Collapsed)
            SetSidebarCollapsed(true, persist: false);

        // Keep group brackets/headers (issue #225) correct after any add/remove/restore.
        // Cheap flag recompute; the drop handler also calls it explicitly after a reorder.
        _sessions.CollectionChanged += (_, _) => RecomputeGroupPositions();

        // Keep the "N need you" header count instant: recompute whenever a session is
        // added/removed or ANY session's status color flips (e.g. a background session goes
        // red while you are on another). The 15s timer remains a backstop.
        _sessions.CollectionChanged += OnSessionsCollectionChanged;

        // Subscribe to session registration for ClaudeSessionId persistence
        _sessionManager.OnClaudeSessionRegistered += OnClaudeSessionRegistered;

        // Sessions created via the Control API (web Manager) need to be wrapped
        // into the Avalonia sidebar so the desktop user can interact with them too.
        _sessionManager.OnSessionCreated += OnExternalSessionCreated;

        // Sessions renamed via the Control API (PATCH /sessions/{sid}) need to refresh
        // the matching SessionViewModel and persist state.
        _sessionManager.OnSessionRenamed += OnExternalSessionRenamed;

        // Sessions killed via the Control API (DELETE /sessions/{sid} from the
        // Cockpit, the Gateway, or a session killing itself) must drop their
        // rail row too - without this the row stays behind wrapping a dead,
        // disposed session (issue #202, root cause of #193).
        _sessionManager.OnSessionRemoved += OnExternalSessionRemoved;

        // Wire source control view file event
        GitChangesView.ViewFileRequested += OnGitViewFileRequested;

        // Wire prompt input text changes for slash command autocomplete
        PromptInput.TextChanged += PromptInput_TextChanged;
        PromptInput.LostFocus += (_, _) => SlashCommandPopup.IsOpen = false;
        PromptInput.GotFocus += PromptInput_GotFocus;

        SetBuildInfo();
        _ = InitializeScreenshotsPanelAsync();
        // No automatic workspace picker on startup (like VS Code). Use File | Open Workspace.

        // Home page (empty-state): its actions route to the existing flows. Paint it now
        // so the very first frame at zero sessions is the home, not a blank content area.
        HomeView.NewSessionRequested += (_, _) => { FileLog.Write("[MainWindow] Home -> New Session"); _ = ShowNewSessionDialog(); };
        HomeView.OpenToolsRequested += (_, _) => { FileLog.Write("[MainWindow] Home -> Tools tab in Settings"); _ = OpenSettingsAsync(onToolsTab: true); };
        HomeView.RepairToolsRequested += (_, _) => _ = RepairToolsAsync();
        HomeView.OpenSettingsRequested += (_, _) => BtnSettings_Click(this, new RoutedEventArgs());
        HomeView.GatewayClicked += (_, _) => OpenGatewayConnectionPanel();
        UpdateHomeVisibility();

        // Start session git status polling (15s interval)
        _sessionGitTimer = new global::Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15),
        };
        _sessionGitTimer.Tick += async (_, _) =>
        {
            foreach (var vm in _sessions) vm.RefreshTimeLabels();
            UpdateNeedsYouCount();
            await RefreshSessionGitStatusAsync();
        };
        _sessionGitTimer.Start();

        // Issue #1181, Task 3b: refresh each session's "receiving a dictation" flag once a second so the
        // rail can paint it orange while a phone dictation is inbound. One cheap disk read per session per
        // tick (the durable marker), NOT per render; the Session raises a change event only when it flips,
        // so the rail repaints just on the edges. (Task 4 will additionally compute this at the Gateway so
        // the phone and cockpit show the same state.)
        _dictationLockTimer = new global::Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _dictationLockTimer.Tick += (_, _) =>
        {
            foreach (var vm in _sessions) vm.Session.RefreshReceivingDictation();
        };
        _dictationLockTimer.Start();

        // Scheduler-leader indicator: show "LEADER" pill on the sidebar and
        // append " -- Leader" to the window title while this Director holds
        // the scheduler mutex. Polled at 5s; the underlying flag is updated
        // by the election thread so the read is just a volatile bool check.
        _schedulerLeaderTimer = new global::Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _schedulerLeaderTimer.Tick += (_, _) => RefreshSchedulerLeaderIndicator();
        _schedulerLeaderTimer.Start();
        RefreshSchedulerLeaderIndicator();

        WireGatewayStatusBox();
        InitDirectorInfo();

        MaybeShowFirstRunWizards();
    }

    /// <summary>
    /// Run the first-run wizards in order on the UI thread after the main window is shown: first the
    /// onboarding wizard (issue #370) when onboarding has not been completed, then the tool-detection
    /// wizard (issue #392) when no agent is configured. Both are gated so a returning user sees
    /// neither. Posted to Background priority so they open after the first render, never blocking it.
    /// </summary>
    private void MaybeShowFirstRunWizards()
    {
        FileLog.Write("[MainWindow] MaybeShowFirstRunWizards");
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await MaybeShowOnboardingWizardAsync();
                await MaybeShowToolDetectionWizardAsync();
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] MaybeShowFirstRunWizards FAILED: {ex.Message}");
            }
        }, global::Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// On first launch (no gateway.url configured and no onboarding-complete marker, issue #370),
    /// open the onboarding wizard that walks the user from launch to a working agent. Once completed
    /// or dismissed it never auto-opens again. If the user chooses "Create first session" on the
    /// final step, route them straight to the New Session dialog.
    /// </summary>
    private async Task MaybeShowOnboardingWizardAsync()
    {
        FileLog.Write("[MainWindow] MaybeShowOnboardingWizardAsync");
        if (!OnboardingModel.ShouldShowOnboarding())
        {
            FileLog.Write("[MainWindow] MaybeShowOnboardingWizardAsync: onboarding already complete; not auto-opening");
            return;
        }

        var wantsNewSession = await OpenOnboardingWizardAsync();
        if (wantsNewSession)
        {
            FileLog.Write("[MainWindow] MaybeShowOnboardingWizardAsync: user chose to create first session");
            await ShowNewSessionDialog();
        }
    }

    /// <summary>Open the onboarding wizard modally; returns true when the user asked to create a session.</summary>
    internal async Task<bool> OpenOnboardingWizardAsync()
    {
        FileLog.Write("[MainWindow] OpenOnboardingWizardAsync");
        var app = global::Avalonia.Application.Current as App;
        var options = app?.SessionManager?.Options ?? app?.Options
            ?? throw new InvalidOperationException("AgentOptions not loaded.");
        var dialog = new OnboardingWizardDialog(options);
        await dialog.ShowDialog<bool?>(this);
        return dialog.WantsNewSession;
    }

    /// <summary>
    /// On first run (no agent tools configured yet, issue #392), auto-open the tool-detection
    /// wizard so a new user gets a near-zero-effort setup. Once any tool is configured the
    /// wizard never auto-opens again - it can still be re-run on demand from Settings &gt; Agents.
    /// Runs after the onboarding wizard (issue #370) in the first-run chain.
    /// </summary>
    private async Task MaybeShowToolDetectionWizardAsync()
    {
        FileLog.Write("[MainWindow] MaybeShowToolDetectionWizardAsync");
        if (!ToolDetectionWizardModel.IsFirstRun())
        {
            FileLog.Write("[MainWindow] MaybeShowToolDetectionWizardAsync: tools already configured; not auto-opening");
            return;
        }

        await OpenToolDetectionWizardAsync();
    }

    /// <summary>Open the first-run tool-detection wizard modally over the main window.</summary>
    internal async Task OpenToolDetectionWizardAsync()
    {
        FileLog.Write("[MainWindow] OpenToolDetectionWizardAsync");
        var app = global::Avalonia.Application.Current as App;
        var options = app?.SessionManager?.Options ?? app?.Options
            ?? throw new InvalidOperationException("AgentOptions not loaded.");
        var dialog = new ToolDetectionWizardDialog(options);
        await dialog.ShowDialog<bool?>(this);
    }

    private global::Avalonia.Threading.DispatcherTimer? _directorInfoTimer;

    private void InitDirectorInfo()
    {
        FileLog.Write("[MainWindow] InitDirectorInfo");
        // The Director address lives on the always-visible app toolbar now, so it must
        // resolve even though the Control API binds its port on a background task that may
        // finish after the window loads. Set what we know now, then poll until the port is
        // bound (it never changes once set), so the toolbar is never stuck on "...".
        if (TrySetDirectorInfo())
            return;

        _directorInfoTimer = new global::Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _directorInfoTimer.Tick += (_, _) =>
        {
            if (TrySetDirectorInfo())
            {
                _directorInfoTimer?.Stop();
                _directorInfoTimer = null;
            }
        };
        _directorInfoTimer.Start();
    }

    /// <summary>
    /// Sets the toolbar Director address from the Control API port. Returns true once the
    /// port is bound (a real value was written), false while it is still starting.
    /// </summary>
    private bool TrySetDirectorInfo()
    {
        var app = global::Avalonia.Application.Current as App;
        var port = app?.ControlApiHost?.Port;
        if (port is > 0)
        {
            DirectorInfoText.Text = $"{Environment.MachineName}:{port.Value}";
            return true;
        }
        DirectorInfoText.Text = $"{Environment.MachineName}:...";
        return false;
    }

    private async void BtnCopyDirectorInfo_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnCopyDirectorInfo_Click");
        try
        {
            var app = global::Avalonia.Application.Current as App;
            var port = app?.ControlApiHost?.Port;
            if (port is null or 0)
            {
                ShowNotification("Control API not started yet.");
                return;
            }
            var url = await Task.Run(() =>
                TailscaleIdentity.ResolveAdvertisedControlApiEndpoint(port.Value));
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) { ShowNotification("Clipboard unavailable."); return; }
            await clipboard.SetTextAsync(url);
            ShowNotification($"Copied: {url}");
            FileLog.Write($"[MainWindow] BtnCopyDirectorInfo_Click: copied {url}");
            BtnCopyDirectorInfo.Content = "Copied!";
            await Task.Delay(1500);
            BtnCopyDirectorInfo.Content = "Copy";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnCopyDirectorInfo_Click FAILED: {ex.Message}");
            ShowNotification($"Copy failed: {ex.Message}");
        }
    }

    // ==================== GATEWAY CONNECTION INDICATOR (issues #223/#224) ====================

    private global::Avalonia.Threading.DispatcherTimer? _gatewayAttachTimer;
    private GatewayConnectionMonitor? _gatewayMonitor;

    private const string GatewayIconRing = "M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 Z M8,3 A5,5 0 1 1 8,13 A5,5 0 1 1 8,3 Z";
    private const string GatewayIconCheck = "M6.2,12.4 L1.6,7.8 L3,6.4 L6.2,9.6 L13,2.8 L14.4,4.2 Z";
    private const string GatewayIconCross = "M3.4,2 L8,6.6 L12.6,2 L14,3.4 L9.4,8 L14,12.6 L12.6,14 L8,9.4 L3.4,14 L2,12.6 L6.6,8 L2,3.4 Z";

    /// <summary>
    /// Attach the sidebar indicator to the host's GatewayConnectionMonitor. The
    /// ControlApiHost starts in the background after the window opens, so retry on a
    /// short timer until it exists, then go fully event-driven.
    /// </summary>
    private void WireGatewayStatusBox()
    {
        // Line 2 of the box (account signed-in) is fed by a heartbeat poll of the Gateway's
        // GET /account/status; line 1 (Gateway reachable) is fed by the GatewayConnectionMonitor
        // attached below. Both repaint the one box through GatewayStatusBoxPresenter.
        WireAccountStatusPoll();

        _gatewayAttachTimer = new global::Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _gatewayAttachTimer.Tick += (_, _) => TryAttachGatewayMonitor();
        _gatewayAttachTimer.Start();
        TryAttachGatewayMonitor();
    }

    private void TryAttachGatewayMonitor()
    {
        if (_gatewayMonitor is not null) return;
        var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
        if (host is null) return;

        _gatewayMonitor = host.GatewayMonitor;
        _gatewayMonitor.Changed += () => Dispatcher.UIThread.Post(UpdateGatewayStatusBox);

        // Same host: wire the Control-API status indicator. The bind may have already failed
        // in the background before we attached, so paint the current state now AND subscribe
        // for later changes (the event can fire on a background thread -> marshal to UI).
        host.StartupStatusChanged += () => Dispatcher.UIThread.Post(UpdateControlApiIndicator);

        _gatewayAttachTimer?.Stop();
        _gatewayAttachTimer = null;
        UpdateGatewayStatusBox();
        UpdateControlApiIndicator();
        FileLog.Write("[MainWindow] Gateway status box attached to GatewayConnectionMonitor");
    }

    private bool _controlApiFailureNotified;

    /// <summary>
    /// Paint the Control-API status indicator from <see cref="ControlApiHost.StartupError"/>.
    /// Hidden while the API is healthy (consistent with the auto-update indicator); RED and
    /// visible when the bind failed, with a one-time loud notification so the degraded state
    /// grabs attention immediately rather than only living in a sidebar tile.
    /// </summary>
    private void UpdateControlApiIndicator()
    {
        var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
        if (host is null) return;

        var error = host.StartupError;
        if (string.IsNullOrEmpty(error))
        {
            // Healthy (or not yet failed): no tile.
            ControlApiIndicator.IsVisible = false;
            return;
        }

        ControlApiIndicatorSub.Text = "Remote, Gateway & phone access are off. Close another Director or free a port, then restart. Click for details.";
        var tip = $"Control API failed to start:\n{error}\n\n"
                + "The local app still works (session badges are live), but this Director is "
                + "invisible to the fleet and cannot be driven remotely.\n"
                + "Fix: free a Control-API port (7879-7898) by closing another Director, then restart this one.";
        ToolTip.SetTip(ControlApiIndicator, tip);
        ControlApiIndicator.IsVisible = true;

        if (!_controlApiFailureNotified)
        {
            _controlApiFailureNotified = true;
            FileLog.Write($"[MainWindow] Control API DOWN surfaced in UI: {error}");
            ShowNotification($"Control API failed to start: {error}. Remote/Gateway access is off -- see the sidebar.");
        }
    }

    private void ControlApiIndicator_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
            var error = host?.StartupError ?? "unknown error";
            ShowNotification($"Control API down: {error}. Free a port in 7879-7898 (close another Director) and restart this one.");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] ControlApiIndicator_PointerPressed FAILED: {ex.Message}");
        }
    }

    // Cached line-2 (account) inputs, refreshed by the account status poll; combined with the live
    // monitor state (line 1) to paint the one box through GatewayStatusBoxPresenter (spec section 6).
    private GatewayAccountSignInState _boxAccount = GatewayAccountSignInState.Unknown;
    private string? _boxAccountEmail;
    private bool _boxDeviceKeyPresent;
    private bool _boxGatewayConfigured;
    private string? _boxGatewayHost;

    // True once the two-way handshake has proven Connected at least once this run, so a later failure
    // reads as "was working, now unreachable" (red repair) rather than "never set up" (spec section 4).
    private bool _boxWasEverConnected;

    /// <summary>
    /// Paint the ONE bottom-left status box from both verification sources (design spec section 6):
    /// line 1 (Gateway reachable) from the live <see cref="GatewayConnectionMonitor"/>, line 2 (account
    /// signed in) from the cached account-status poll. Both are reduced by
    /// <see cref="GatewayStatusBoxPresenter"/> - which runs the same resolver the panel uses - so the box
    /// and the panel can never disagree. GREEN is EARNED: line 1 only on a proven two-way handshake
    /// (the EXAMPLE-PC failure mode of heartbeats-fine-callback-dead shows RED, the point of #224); line 2
    /// only on the Gateway's own signed-in report.
    /// </summary>
    private void UpdateGatewayStatusBox()
    {
        var inputs = BuildStatusBoxInputs();
        if (inputs.Connection == GatewayConnectionVerification.Connected)
            _boxWasEverConnected = true;

        var content = GatewayStatusBoxPresenter.Describe(inputs, _boxGatewayHost, _boxAccountEmail);

        var (bg, border) = BoxColors(content.Visual);
        GatewayStatusBox.Background = Brush.Parse(bg);
        GatewayStatusBox.BorderBrush = Brush.Parse(border);
        PaintCheckLine(GatewayConnectedMarker, GatewayConnectedLine, content.Connected);
        PaintCheckLine(GatewaySignedInMarker, GatewaySignedInLine, content.SignedIn);
        ToolTip.SetTip(GatewayStatusBox, content.Tooltip);

        // A missing gateway is NOT an error (a legitimate local-only Director); only the red failure
        // states count against readiness and surface a problem row on the status screen.
        _gatewayError = content.Visual == GatewayStatusBoxVisual.Red;
        ApplyHomeHealth();
    }

    // Combine the live monitor state (line 1) with the cached account poll (line 2) into the resolver's
    // full input snapshot. GatewayConfigured is true when either source says so, so the box resolves
    // correctly whichever attached first.
    private GatewayConnectionInputs BuildStatusBoxInputs()
    {
        var m = _gatewayMonitor;
        var (connection, leg) = MapMonitor(m);
        var configured = _boxGatewayConfigured || (m is not null && m.Status != GatewayConnectionStatus.NotConfigured);
        return new GatewayConnectionInputs(
            GatewayConfigured: configured,
            Connection: connection,
            FailedLeg: leg,
            WasEverConnected: _boxWasEverConnected,
            DeviceKeyPresent: _boxDeviceKeyPresent,
            Account: _boxAccount);
    }

    // Map the monitor's raw status onto the resolver's connection verification plus the failing leg.
    // Gateway Cleanup mission (tunnel-only): a failure is always the OUTBOUND reach now - this Director could
    // not get its tunnel up. The Callback leg (the Gateway dialing this Director back) no longer exists, so it
    // is never reported: the handshake that used to distinguish the two legs is gone with the dial-back it
    // measured. NoTailnetIdentity is a local identity failure named generically here (the panel names it
    // precisely in Step 1 repair).
    private static (GatewayConnectionVerification, GatewayConnectionFailedLeg) MapMonitor(GatewayConnectionMonitor? m)
    {
        if (m is null) return (GatewayConnectionVerification.Unknown, GatewayConnectionFailedLeg.None);
        return m.Status switch
        {
            GatewayConnectionStatus.Connected => (GatewayConnectionVerification.Connected, GatewayConnectionFailedLeg.None),
            GatewayConnectionStatus.Connecting => (GatewayConnectionVerification.Verifying, GatewayConnectionFailedLeg.None),
            GatewayConnectionStatus.Failed => (GatewayConnectionVerification.Failed, GatewayConnectionFailedLeg.OutboundReach),
            GatewayConnectionStatus.NoTailnetIdentity => (GatewayConnectionVerification.Failed, GatewayConnectionFailedLeg.None),
            _ => (GatewayConnectionVerification.Unknown, GatewayConnectionFailedLeg.None),
        };
    }

    // The box surface applies colors and glyphs; the presenter's per-line marker state decides which
    // (spec section 6). Colors live here with the surface, not in the Core presenter.
    private static void PaintCheckLine(global::Avalonia.Controls.Shapes.Path marker, TextBlock text, GatewayStatusLine line)
    {
        var (glyph, color) = MarkerStyle(line.Marker);
        marker.Data = Geometry.Parse(glyph);
        marker.Fill = Brush.Parse(color);
        text.Text = line.Text;
        text.Foreground = Brush.Parse(color);
    }

    private static (string Glyph, string Color) MarkerStyle(GatewayCheckState marker) => marker switch
    {
        GatewayCheckState.Passed => (GatewayIconCheck, "#22C55E"),   // green filled check
        GatewayCheckState.Working => (GatewayIconRing, "#F0B848"),   // amber ring, in progress
        GatewayCheckState.Failed => (GatewayIconCross, "#EF4444"),   // red cross, named leg
        GatewayCheckState.Pending => (GatewayIconRing, "#F0B848"),   // amber ring, the actionable nudge
        _ => (GatewayIconRing, "#777777"),                           // muted ring, cannot tell yet
    };

    private static (string Background, string Border) BoxColors(GatewayStatusBoxVisual visual) => visual switch
    {
        GatewayStatusBoxVisual.Green => ("#1B3A2A", "#22C55E"),
        GatewayStatusBoxVisual.Red => ("#3A1B1B", "#DC2626"),
        // Amber (needs attention) and Yellow (verifying) share the warm scheme; the line content and
        // markers carry the distinction (line 1 "Connecting..." with a working ring for yellow).
        _ => ("#3A331B", "#F0B848"),
    };

    /// <summary>
    /// One click opens the Gateway Connection panel on the resolver's current step (spec section 6):
    /// green -> the Done view, connected-but-not-signed-in -> Step 2 (sign in), everything else -> Step 1
    /// (the automatic scan / progress / named failure). First-time setup and re-sign-in are the same flow
    /// into the same panel.
    /// </summary>
    private void GatewayStatusBox_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            FileLog.Write("[MainWindow] GatewayStatusBox clicked; opening the connection panel on its current step");
            OpenGatewayConnectionPanel();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] GatewayStatusBox_PointerPressed FAILED: {ex.Message}");
        }
    }

    // Open the one reusable Gateway Connection panel in a tool window, on the resolver's current step
    // (spec section 6). CreateForCurrentState resolves the step from the live handshake state: a proven
    // connection opens on Done/Step 2, a prior failure opens Step 1 in REPAIR mode (Phase 5), everything
    // else opens the first-time scan.
    private void OpenGatewayConnectionPanel()
    {
        try
        {
            var window = new Window
            {
                Title = "Gateway Connection",
                Width = 560,
                Height = 660,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = global::Avalonia.Media.Brush.Parse("#252526"),
                Content = Controls.GatewayConnectionPanel.CreateForCurrentState(),
            };
            window.Show(this);
            FileLog.Write("[MainWindow] Gateway Connection panel opened (on the resolver's current step)");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OpenGatewayConnectionPanel FAILED: {ex.Message}");
        }
    }

    // TEMPORARY (Gateway Connection mission): the View-menu entry opens the panel for testing.
    private void OpenGatewayConnectionPreview() => OpenGatewayConnectionPanel();

    // ==================== GATEWAY STATUS BOX - ACCOUNT LINE (line 2) ====================

    private global::Avalonia.Threading.DispatcherTimer? _accountPollTimer;
    private bool _accountReadInFlight;

    /// <summary>How often line 2 of the status box re-reads the Gateway's signed-in status.</summary>
    private static readonly TimeSpan AccountPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>The Cockpit Account page route (issue #852). Appended to the Gateway-resolved
    /// Cockpit front-door URL. Retained for <see cref="BuildAccountUrl"/>, which is still unit-tested.</summary>
    private const string CockpitAccountRoute = "/account";

    /// <summary>
    /// Build the Cockpit Account page URL from the gateway's Tailscale front-door URL (issue #852):
    /// {frontDoor}/account, with a single clean separator so a front door ending in a slash never
    /// yields "//account". Pure string building, so it is unit-testable without a UI thread.
    /// </summary>
    internal static string BuildAccountUrl(string frontDoorUrl) =>
        frontDoorUrl.TrimEnd('/') + CockpitAccountRoute;

    /// <summary>
    /// Wire the account line of the status box (spec section 6, line 2): a heartbeat poll that reads the
    /// connected Gateway's <c>GET /account/status</c> off the UI thread, caches the line-2 inputs, and
    /// repaints the one box. Purely informational and never a gate - the box paints immediately and updates
    /// when the first read returns, so the sidebar never waits on the network (#651/#664).
    /// </summary>
    private void WireAccountStatusPoll()
    {
        _accountPollTimer = new global::Avalonia.Threading.DispatcherTimer
        {
            Interval = AccountPollInterval,
        };
        _accountPollTimer.Tick += AccountPollTimer_Tick;
        _accountPollTimer.Start();
        // Kick the first read immediately so line 2 resolves shortly after startup without waiting a
        // full poll interval. The timer Tick handler is the catching boundary.
        AccountPollTimer_Tick(null, EventArgs.Empty);
        FileLog.Write("[MainWindow] Account status poll started (status box line 2)");
    }

    private async void AccountPollTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            await RefreshAccountStatusAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] AccountPollTimer_Tick FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Read the Gateway's signed-in status off the UI thread, then fold it into the status box (spec
    /// section 6, line 2). The read is best-effort (an unreachable Gateway is a result value, never an
    /// exception out of the client), and overlapping reads are skipped so a slow Gateway cannot pile up
    /// timer ticks.
    /// </summary>
    private async Task RefreshAccountStatusAsync()
    {
        if (_accountReadInFlight) return;
        _accountReadInFlight = true;
        try
        {
            // Snapshot the config on the UI thread, then do the network read off it.
            var config = GatewayConfig.Load();
            var status = await Task.Run(() => new GatewayAccountStatusClient().GetStatusAsync(config));
            Dispatcher.UIThread.Post(() => ApplyAccountStatus(config, status));
        }
        finally
        {
            _accountReadInFlight = false;
        }
    }

    /// <summary>
    /// Fold the Gateway's account status into the status box's line-2 inputs and repaint (spec sections 4,
    /// 6): the signed-in state, the email (shown on the green line only), whether this device holds its own
    /// token, whether a Gateway is configured, and the Gateway host for the tooltip. An unreachable or
    /// not-configured Gateway maps to a MUTED "cannot tell yet" - never a false sign-out (decision 3). The
    /// email is used only to render the identity; no token ever reaches the box (security DT-05).
    /// </summary>
    private void ApplyAccountStatus(GatewayConfig config, GatewayAccountStatus status)
    {
        _boxAccount = MapAccount(status);
        _boxAccountEmail = status.SignedIn ? status.Email : null;
        _boxDeviceKeyPresent = !string.IsNullOrWhiteSpace(config.Token);
        _boxGatewayConfigured = status.GatewayConfigured;
        _boxGatewayHost = SafeHost(config.Url);

        // Log the state and booleans, never the email (PII; CodingStyle Section 4 / 12).
        FileLog.Write($"[MainWindow] ApplyAccountStatus: account={_boxAccount}, deviceKey={_boxDeviceKeyPresent}, configured={status.GatewayConfigured}, reachable={status.Reachable}");
        UpdateGatewayStatusBox();
    }

    // Map the Gateway's account report onto the resolver's signed-in input. A not-configured Gateway is
    // Unknown and an unreachable one is Unavailable - both muted, never a false sign-out (decision 3).
    private static GatewayAccountSignInState MapAccount(GatewayAccountStatus status)
    {
        if (!status.GatewayConfigured) return GatewayAccountSignInState.Unknown;
        if (!status.Reachable) return GatewayAccountSignInState.Unavailable;
        return status.SignedIn ? GatewayAccountSignInState.SignedIn : GatewayAccountSignInState.SignedOut;
    }

    private static string? SafeHost(string? url)
        => !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    // ==================== HOME (empty-state) ====================

    /// <summary>Last computed readiness, cached so a gateway change can re-tint the header
    /// without re-running the (async) tool/key probes.</summary>
    private HomeStatus? _lastHomeStatus;

    /// <summary>True when the gateway is in an error state (Failed / no tailnet identity).
    /// A simply-absent gateway is NOT an error - a local-only Director is legitimate.</summary>
    private bool _gatewayError;

    /// <summary>True when the user asked to see the status screen (View &gt; Status) while a
    /// session is open. Cleared the moment a session is selected, so the terminal returns.</summary>
    private bool _statusRequested;

    /// <summary>Show the status screen over the content area without closing any session.</summary>
    private void ShowStatusView()
    {
        FileLog.Write("[MainWindow] ShowStatusView");
        _statusRequested = true;
        UpdateHomeVisibility();
    }

    /// <summary>
    /// True when any full-content in-window overlay (Tools / Comms / Connections / Scheduler)
    /// is open. The Home page must not paint over an open overlay, so UpdateHomeVisibility
    /// consults this (issue #447).
    /// </summary>
    private bool IsContentOverlayOpen()
        => CommsOverlay.IsVisible
           || ConnectionsOverlay.IsVisible || SchedulerOverlay.IsVisible;

    /// <summary>
    /// Show the full-screen home page exactly when this Director has zero sessions - it is
    /// the "nothing is running, here is the state, start something" screen. The window menu
    /// bar stays; the toolbar is hidden so only the home shows beneath it. The home appears
    /// (and disappears) the moment the session count crosses zero.
    /// </summary>
    private void UpdateHomeVisibility()
    {
        // The status screen sits in the main content cell (ZIndex 30, over the terminal). The
        // window chrome - toolbar and session rail - stays visible around it. It shows when
        // there are no sessions, or on demand (View > Status, _statusRequested). The content
        // overlays (Tools/Comms/etc.) sit lower in the same cell; if the status screen paints
        // over an open overlay its actions look dead (issue #447), so it yields while one is up.
        var overlayOpen = IsContentOverlayOpen();
        var showHome = (_sessions.Count == 0 || _statusRequested) && !overlayOpen;
        HomeView.IsVisible = showHome;
        FileLog.Write($"[MainWindow] UpdateHomeVisibility: showHome={showHome}, sessions={_sessions.Count}, statusRequested={_statusRequested}, overlayOpen={overlayOpen}");

        if (!showHome) return;

        HomeView.SetVersion(AppVersion.Display);
        // Paint the gateway status box from current state (no-op until the monitor is attached;
        // its Changed event repaints it once it is).
        UpdateGatewayStatusBox();
        _ = RefreshHomeAsync();
    }

    /// <summary>
    /// Gather the readiness facts off the UI thread (tool detection probes PATH; the key
    /// resolver may call the Gateway), then render the rows. Per the responsive-UI rule the
    /// card shows "Checking..." immediately and fills in when the probe completes.
    /// </summary>
    private async Task RefreshHomeAsync()
    {
        HomeView.SetBusy();
        try
        {
            var app = (App)global::Avalonia.Application.Current!;
            var options = app.Options;

            var facts = await Task.Run<(List<AgentCliFact> clis, int built, int total, List<string> missing)>(() =>
            {
                var detector = new ToolDetectionService();
                var clis = ToolDetectionService.SupportedTools.Select(tool =>
                {
                    var det = detector.DetectTool(tool, options);
                    var validation = ToolDetectionService.ReadValidationStatus(tool, options);
                    var version = validation?.Ok == true ? validation.Version : null;
                    return new AgentCliFact(ToolDetectionService.DisplayName(tool), det.Found, version);
                }).ToList();

                // Only consider tools this install is EXPECTED to provide (shim, built, or on PATH); tools
                // never installed here (extras tier, other bundles, manifest drift) must not raise a warning.
                // Availability is judged by PATH OR the bundled bin dir (issue #448), not bin-dir presence
                // alone - a machine where cc-* resolve on PATH is fully working even if this build's bin is empty.
                var catalog = new ToolCatalogService().GetCatalog();
                var expected = catalog.Where(d => d.IsExpected).ToList();
                var built = expected.Count(d => d.IsAvailable);
                var total = expected.Count;
                var missing = expected.Where(d => !d.IsAvailable).Select(d => d.Name).ToList();

                return (clis, built, total, missing);
            });

            _lastClis = facts.clis;
            _lastBuildFacts = (facts.built, facts.total, facts.missing);
            _lastHomeStatus = HomeStatusBuilder.Build(facts.clis, facts.built, facts.total, facts.missing, _lastToolHealth, _lastBasePythonBroken);

            ApplyHomeHealth();

            // Run the tool checks in the background so the tools row shows real pass/fail/not-built
            // instead of just build status. The home renders immediately; the breakdown fills in.
            _ = RefreshToolHealthAsync();

            // Startup auto self-heal: if any EXPECTED tool is broken (installed but not runnable), repair
            // it automatically - no click needed. Guarded to fire at most once per run and never loop: a
            // failed repair leaves the manual "Fix" button for a retry rather than re-triggering forever.
            if (facts.missing.Count > 0 && !_autoRepairAttempted && !_repairingTools)
            {
                _autoRepairAttempted = true;
                FileLog.Write($"[MainWindow] startup auto self-heal: {facts.missing.Count} broken tool(s) detected, repairing automatically");
                _ = RepairToolsAsync(auto: true);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] RefreshHomeAsync FAILED: {ex.Message}");
        }
    }

    private bool _repairingTools;

    /// <summary>Set once a run when startup auto self-heal has fired, so a failed repair never loops.</summary>
    private bool _autoRepairAttempted;

    /// <summary>
    /// Health-based repair of the cc-* Python tools - from the Home "Fix it" button (<paramref name="auto"/>
    /// false) or fired automatically on startup when a broken tool is detected (<paramref name="auto"/> true).
    /// Forces a venv rebuild via <see cref="CcDirector.Setup.Engine.ToolUpdater.RepairPythonToolsAsync"/> (which
    /// is NOT version-gated, so it actually fixes a half-installed toolset the silent auto-update would skip),
    /// streams live progress onto the tools row, then re-runs the readiness check so the card flips green.
    /// Runs the slow pip work off the UI thread; guarded so a double-click cannot start two rebuilds.
    /// </summary>
    private async Task RepairToolsAsync(bool auto = false)
    {
        if (_repairingTools) return;
        _repairingTools = true;
        FileLog.Write(auto ? "[MainWindow] Tools auto self-heal started" : "[MainWindow] Tools repair requested from Home");
        try
        {
            HomeView.SetToolsRepairing(auto ? "auto-repairing..." : "starting...");
            var layout = CcDirector.Setup.Engine.InstallLayout.Default();
            var progress = new Progress<string>(msg => HomeView.SetToolsRepairing(msg));
            var result = await Task.Run(() =>
                new CcDirector.Setup.Engine.ToolUpdater(layout).RepairPythonToolsAsync(progress));
            FileLog.Write($"[MainWindow] Tools repair done: success={result.Success}, count={result.ToolCount}, msg={result.Message}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] RepairToolsAsync FAILED: {ex.Message}");
        }
        finally
        {
            _repairingTools = false;
            _lastToolHealth = null; // the tools changed - re-run the health check on the next refresh
            await RefreshHomeAsync();
        }
    }

    // Cached so the background tool-health pass can rebuild the home status without re-detecting CLIs.
    private List<AgentCliFact>? _lastClis;
    private (int built, int total, List<string> missing)? _lastBuildFacts;
    private CcDirector.Core.Tools.ToolHealthSummary? _lastToolHealth;
    // Cached result of the last shared base-Python runtime probe (issue #995), so the immediate home render
    // in RefreshHomeAsync reflects a known-broken runtime without re-launching the probe on the fast path.
    private bool _lastBasePythonBroken;
    private bool _toolHealthRunning;

    // ---- Active cc-* tools indicator state machine (issue #829) ----
    // The rail badge is no longer a passive "fix me" warning: when drift is detected and
    // tools.autoUpdate.enabled is true it shows the orange "Syncing tools..." state, auto-runs
    // ToolReconciler.ReconcileAsync (one at a time, with backoff), returns to green when in sync,
    // and only falls to red "Tools need attention" after repeated failures. The pure transition
    // rules live in ToolsSyncStateMachine; this window owns the in-flight guard, the cooldown, and
    // the retry timer so reconcile is never thrashed.
    private readonly ToolsSyncStateMachine _toolsSync = new();
    private bool _toolsReconcileInFlight;
    private DateTime _toolsReconcileCooldownUntil = DateTime.MinValue;
    private DispatcherTimer? _toolsReconcileRetryTimer;

    /// <summary>
    /// Run every cc-* tool's checks off the UI thread and roll them up into pass/fail/not-built, then
    /// re-apply the home status so the tools row shows the real breakdown (the screenshot complaint:
    /// the home said "all systems go" while the Tools page showed a FAIL and not-built tools). Bounded
    /// concurrency, guarded against pile-up. Auth-gated tools declare no smoke test, so this is just
    /// their presence+version check; tools with a read-only smoke run that too.
    /// </summary>
    private async Task RefreshToolHealthAsync(bool force = false, bool driveSync = true)
    {
        if (_toolHealthRunning) return;
        if (_lastToolHealth is not null && !force) return; // computed once this session; reuse the cache
        _toolHealthRunning = true;
        try
        {
            var (summary, basePythonBroken) = await Task.Run(async () =>
            {
                var catalog = new ToolCatalogService().GetCatalog();
                var runner = new ToolTestRunner();
                using var gate = new System.Threading.SemaphoreSlim(Math.Max(1, Environment.ProcessorCount - 1));
                var inputs = await Task.WhenAll(catalog.Select(async d =>
                {
                    // Availability (PATH or bundled bin), not bin-only IsBuilt, decides whether the tool
                    // can run its checks (issue #448). A PATH-only tool's BinaryPath is its PATH-resolved exe.
                    if (!d.IsAvailable)
                        return new CcDirector.Core.Tools.ToolHealthInput(d.Name, false, d.IsExpected, false);
                    await gate.WaitAsync();
                    try
                    {
                        var results = await runner.RunAllForToolAsync(d);
                        return new CcDirector.Core.Tools.ToolHealthInput(d.Name, true, d.IsExpected, results.All(r => r.Passed));
                    }
                    finally { gate.Release(); }
                }));
                // Probe the shared base Python directly. Every Python cc-* tool delegates to it, so if it is
                // hollow (present but cannot import its standard library) they ALL fail at once - a single,
                // repairable runtime failure the per-tool breakdown would otherwise show as N unrelated fails.
                var pyBroken = !CcDirector.Setup.Engine.PythonRuntimeProbe.IsBasePythonHealthy(
                    CcDirector.Setup.Engine.InstallLayout.Default());
                return (summary: CcDirector.Core.Tools.ToolHealthSummary.From(inputs), basePythonBroken: pyBroken);
            });

            _lastToolHealth = summary;
            _lastBasePythonBroken = basePythonBroken;
            FileLog.Write($"[MainWindow] tool health: pass={summary.Pass}, fail={summary.Fail}, notBuilt={summary.NotBuilt}, broken={summary.Broken}, basePythonBroken={basePythonBroken}");

            if (_lastClis is { } clis && _lastBuildFacts is { } bf)
            {
                _lastHomeStatus = HomeStatusBuilder.Build(clis, bf.built, bf.total, bf.missing, summary, basePythonBroken);
                ApplyHomeHealth();
            }

            // Startup auto self-heal for a hollow shared Python runtime (issue #995): the tool exes exist and
            // resolve on PATH, so the missing-tools trigger in RefreshHomeAsync never fires - yet every Python
            // tool is failing because the shared base Python cannot start. Repair re-provisions it. Guarded to
            // fire at most once per run so a failed repair leaves the manual "Fix" button rather than looping.
            if (basePythonBroken && !_autoRepairAttempted && !_repairingTools)
            {
                _autoRepairAttempted = true;
                FileLog.Write("[MainWindow] startup auto self-heal: shared base Python runtime is broken, repairing automatically");
                _ = RepairToolsAsync(auto: true);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] RefreshToolHealthAsync FAILED: {ex.Message}");
        }
        finally
        {
            _toolHealthRunning = false;
        }

        // Drive the active tools indicator off this fresh health snapshot (issue #829). Skipped on the
        // post-reconcile re-probe (driveSync=false), where RunToolsReconcileAsync records the attempt
        // outcome itself so the success/failure bookkeeping is not double-counted.
        if (driveSync)
            await DriveToolsSyncAsync();
    }

    /// <summary>
    /// Evaluate the active cc-* tools indicator (issue #829) against the latest health snapshot and, when
    /// warranted, start an automatic reconcile. "Drift" is the existing warning condition (the tools row is
    /// not green) OR - when auto-update is on - reconcile-detectable drift (a missing shim, an orphaned legacy
    /// alias shim, a broken venv) that the row alone would not show. When auto-update is OFF the indicator
    /// behaves exactly as it did before this issue: a passive warning on the row, no auto-reconcile. Starting
    /// a reconcile is debounced (one in flight) and cooldown-gated (backoff between attempts) so the badge
    /// never thrashes the reconcile engine.
    /// </summary>
    private async Task DriveToolsSyncAsync()
    {
        var toolsCheck = _lastHomeStatus?.Checks.FirstOrDefault(c => c.Title == "cc-* tools");
        var healthDrift = toolsCheck is not null && toolsCheck.Level != HomeCheckLevel.Ok;
        var enabled = ToolAutoUpdateSetting.Get();

        // Probe the reconciler only when auto-update is on and the row itself is green - that is the only case
        // where reconcile-detectable drift would otherwise go unseen. The probe is pure reads, run off the UI
        // thread. When auto-update is off we use ONLY the row signal, preserving the pre-issue passive behavior.
        var hasDrift = healthDrift;
        if (enabled && !healthDrift && !_toolsReconcileInFlight)
        {
            hasDrift = await Task.Run(() =>
                new CcDirector.Setup.Engine.ToolReconciler(CcDirector.Setup.Engine.InstallLayout.Default()).HasDrift());
        }

        var previousState = _toolsSync.State;
        var decision = _toolsSync.Evaluate(hasDrift, enabled, _toolsReconcileInFlight);
        if (decision.State != previousState)
            FileLog.Write($"[MainWindow] tools indicator state: {previousState} -> {decision.State} (drift={hasDrift}, autoUpdate={enabled})");

        UpdateToolsIndicator();

        // Start a reconcile only when the machine asks for one, nothing is in flight, no other tool repair is
        // running, and the backoff cooldown has elapsed - the no-thrash guarantee.
        if (decision.ShouldReconcile
            && !_toolsReconcileInFlight
            && !_repairingTools
            && DateTime.UtcNow >= _toolsReconcileCooldownUntil)
        {
            StartToolsReconcile();
        }
    }

    /// <summary>
    /// Kick off a single automatic reconcile for the active indicator. The orange "Syncing tools..." state is
    /// already set by <see cref="ToolsSyncStateMachine.Evaluate"/>; this renders it immediately (responsive
    /// &lt;100ms, before any awaited work) and then runs the reconcile off the UI thread.
    /// </summary>
    private void StartToolsReconcile()
    {
        _toolsReconcileInFlight = true;
        FileLog.Write("[MainWindow] tools auto-reconcile starting (indicator Syncing)");
        UpdateToolsIndicator(); // paint orange now - the reconcile runs in the background
        _ = RunToolsReconcileAsync();
    }

    /// <summary>
    /// Run one reconcile, re-probe drift, and record the outcome on the state machine. Success (the reconcile
    /// did not fail AND drift is gone) returns the badge to green; otherwise it counts a failed attempt and,
    /// below the retry ceiling, schedules a backoff retry. At the ceiling the machine is already in
    /// <see cref="ToolsIndicatorState.NeedsAttention"/> (red) and no further retry is scheduled.
    /// </summary>
    private async Task RunToolsReconcileAsync()
    {
        CcDirector.Setup.Engine.ReconcileOutcome outcome;
        try
        {
            var result = await Task.Run(() =>
                new CcDirector.Setup.Engine.ToolReconciler(CcDirector.Setup.Engine.InstallLayout.Default()).ReconcileAsync());
            outcome = result.Outcome;
            FileLog.Write($"[MainWindow] tools auto-reconcile done: outcome={outcome}, actions={result.Actions.Count}" +
                          (result.Error is null ? "" : $", error={result.Error}"));
        }
        catch (Exception ex)
        {
            outcome = CcDirector.Setup.Engine.ReconcileOutcome.Failed;
            FileLog.Write($"[MainWindow] tools auto-reconcile FAILED: {ex.Message}");
        }
        finally
        {
            _toolsReconcileInFlight = false;
        }

        // Re-probe health so we judge against the post-reconcile reality (driveSync:false - we record the
        // outcome here rather than letting the snapshot path start another reconcile).
        _lastToolHealth = null;
        await RefreshToolHealthAsync(force: true, driveSync: false);

        var toolsCheck = _lastHomeStatus?.Checks.FirstOrDefault(c => c.Title == "cc-* tools");
        var healthDrift = toolsCheck is not null && toolsCheck.Level != HomeCheckLevel.Ok;
        var reconcilerDrift = await Task.Run(() =>
            new CcDirector.Setup.Engine.ToolReconciler(CcDirector.Setup.Engine.InstallLayout.Default()).HasDrift());
        var driftRemains = healthDrift || reconcilerDrift;

        var previousState = _toolsSync.State;
        if (outcome != CcDirector.Setup.Engine.ReconcileOutcome.Failed && !driftRemains)
        {
            _toolsSync.OnReconcileSucceeded();
            _toolsReconcileCooldownUntil = DateTime.MinValue;
            FileLog.Write($"[MainWindow] tools indicator state: {previousState} -> {_toolsSync.State} (reconcile resolved drift)");
        }
        else
        {
            _toolsSync.OnReconcileFailed();
            FileLog.Write($"[MainWindow] tools indicator state: {previousState} -> {_toolsSync.State} " +
                          $"(reconcile ineffective; failures={_toolsSync.ConsecutiveFailures}, outcome={outcome}, driftRemains={driftRemains})");

            if (_toolsSync.State == ToolsIndicatorState.Syncing)
                ScheduleToolsReconcileRetry();
        }

        UpdateToolsIndicator();
    }

    /// <summary>
    /// Schedule the next reconcile attempt after the state machine's backoff, so retries are spaced out
    /// instead of tight-looped. One-shot: it fires once, then re-drives the indicator which (if drift still
    /// stands and the cooldown has elapsed) starts the next reconcile.
    /// </summary>
    private void ScheduleToolsReconcileRetry()
    {
        var backoff = _toolsSync.NextBackoff();
        _toolsReconcileCooldownUntil = DateTime.UtcNow + backoff;
        FileLog.Write($"[MainWindow] tools auto-reconcile retry scheduled in {backoff.TotalSeconds:0}s");

        _toolsReconcileRetryTimer?.Stop();
        _toolsReconcileRetryTimer = new DispatcherTimer { Interval = backoff };
        _toolsReconcileRetryTimer.Tick += async (_, _) =>
        {
            _toolsReconcileRetryTimer?.Stop();
            try
            {
                await DriveToolsSyncAsync();
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] tools auto-reconcile retry FAILED: {ex.Message}");
            }
        };
        _toolsReconcileRetryTimer.Start();
    }

    /// <summary>
    /// Combine the cached readiness with the live gateway state into the status screen.
    /// "Healthy" requires every check green AND the gateway not in an error state. When
    /// healthy the screen is quiet (all-clear); otherwise it lists only the failing checks.
    /// Cheap and idempotent: called after a refresh and on every gateway change.
    /// </summary>
    private void ApplyHomeHealth()
    {
        // The rail's cc-* tools indicator lives outside the home view, so paint it first and
        // unconditionally (it must hide itself when there is no status yet, too).
        UpdateToolsIndicator();

        if (_lastHomeStatus is not { } status) return;

        var healthy = status.AllReady && !_gatewayError;
        var summary = _gatewayMonitor?.Status == GatewayConnectionStatus.Connected
            ? "Gateway connected - ready to work"
            : "Ready to start a session";
        // When all tools pass, show the count (and any optional not-installed) quietly here rather than
        // as an alarm, so a healthy machine reads "24 of 29 tools passing - 5 not installed".
        if (_lastToolHealth is { } h && h.Total > 0)
        {
            summary = $"{h.Pass} of {h.Total} tools passing";
            if (h.NotBuilt > 0) summary += $" - {h.NotBuilt} not installed";
        }
        HomeView.SetStatus(status, healthy, _gatewayError, summary);
    }

    // Indicator palettes (issue #829). Inline one-off colors for this single rail control, as the
    // VisualStyle guide permits for one-offs; kept here as named brushes so each state reads clearly.
    private static readonly IBrush SyncOrangeBorder = new SolidColorBrush(Color.Parse("#E0822E"));
    private static readonly IBrush SyncOrangeBackground = new SolidColorBrush(Color.Parse("#3A2B17"));
    private static readonly IBrush SyncOrangeText = new SolidColorBrush(Color.Parse("#F0A040"));
    private static readonly IBrush SyncOrangeSub = new SolidColorBrush(Color.Parse("#C99A6A"));
    private static readonly IBrush WarnAmberBorder = new SolidColorBrush(Color.Parse("#F0B848"));
    private static readonly IBrush WarnAmberBackground = new SolidColorBrush(Color.Parse("#3A331B"));
    private static readonly IBrush WarnAmberText = new SolidColorBrush(Color.Parse("#F0B848"));
    private static readonly IBrush WarnAmberSub = new SolidColorBrush(Color.Parse("#B59868"));
    private static readonly IBrush AttentionRedBorder = new SolidColorBrush(Color.Parse("#E0574A"));
    private static readonly IBrush AttentionRedBackground = new SolidColorBrush(Color.Parse("#3A1E1B"));
    private static readonly IBrush AttentionRedText = new SolidColorBrush(Color.Parse("#F0746A"));
    private static readonly IBrush AttentionRedSub = new SolidColorBrush(Color.Parse("#C98A82"));
    private static readonly IBrush GlyphForeground = new SolidColorBrush(Color.Parse("#1E1E1E"));

    /// <summary>
    /// Paint the rail's cc-* tools indicator from the active state machine (issue #829). InSync hides the
    /// badge; Syncing shows the orange "Syncing tools..." state with a live progress spinner; Warning is the
    /// legacy passive amber warning (auto-update off); NeedsAttention is the red "Tools need attention"
    /// to-do after repeated reconcile failures. The Warning/NeedsAttention states are clickable (open
    /// Settings on the Tools tab); the transient Syncing state is not a to-do, so its cursor is the arrow.
    /// </summary>
    private void UpdateToolsIndicator()
    {
        var toolsCheck = _lastHomeStatus?.Checks.FirstOrDefault(c => c.Title == "cc-* tools");
        var detail = toolsCheck?.Detail ?? "";

        switch (_toolsSync.State)
        {
            case ToolsIndicatorState.Syncing:
                ToolsIndicator.IsVisible = true;
                ToolsIndicator.Background = SyncOrangeBackground;
                ToolsIndicator.BorderBrush = SyncOrangeBorder;
                ToolsIndicator.Cursor = new Cursor(StandardCursorType.Arrow);
                ToolsIndicatorDot.Fill = SyncOrangeBorder;
                ToolsIndicatorGlyph.IsVisible = false;
                ToolsIndicatorLabel.Text = "Syncing tools...";
                ToolsIndicatorLabel.Foreground = SyncOrangeText;
                ToolsIndicatorSub.Text = "reconciling cc-* tools";
                ToolsIndicatorSub.Foreground = SyncOrangeSub;
                ToolsIndicatorSpinner.IsVisible = true;
                ToolTip.SetTip(ToolsIndicator, "Bringing the cc-* tools back in sync...");
                break;

            case ToolsIndicatorState.NeedsAttention:
                ToolsIndicator.IsVisible = true;
                ToolsIndicator.Background = AttentionRedBackground;
                ToolsIndicator.BorderBrush = AttentionRedBorder;
                ToolsIndicator.Cursor = new Cursor(StandardCursorType.Hand);
                ToolsIndicatorDot.Fill = AttentionRedBorder;
                ToolsIndicatorGlyph.IsVisible = true;
                ToolsIndicatorLabel.Text = "Tools need attention";
                ToolsIndicatorLabel.Foreground = AttentionRedText;
                ToolsIndicatorSub.Text = string.IsNullOrEmpty(detail) ? "click to open Settings and repair" : detail;
                ToolsIndicatorSub.Foreground = AttentionRedSub;
                ToolsIndicatorSpinner.IsVisible = false;
                ToolTip.SetTip(ToolsIndicator,
                    "Automatic tool sync did not resolve the problem.\nClick to open Settings and repair the tools.");
                break;

            case ToolsIndicatorState.Warning:
                ToolsIndicator.IsVisible = true;
                ToolsIndicator.Background = WarnAmberBackground;
                ToolsIndicator.BorderBrush = WarnAmberBorder;
                ToolsIndicator.Cursor = new Cursor(StandardCursorType.Hand);
                ToolsIndicatorDot.Fill = WarnAmberBorder;
                ToolsIndicatorGlyph.IsVisible = true;
                ToolsIndicatorLabel.Text = "cc-* tools";
                ToolsIndicatorLabel.Foreground = WarnAmberText;
                ToolsIndicatorSub.Text = detail;
                ToolsIndicatorSub.Foreground = WarnAmberSub;
                ToolsIndicatorSpinner.IsVisible = false;
                ToolTip.SetTip(ToolsIndicator,
                    $"Some cc-* tools are missing or failing ({detail}).\nClick to open Settings and download/repair the tools.");
                break;

            case ToolsIndicatorState.InSync:
            default:
                ToolsIndicator.IsVisible = false;
                ToolsIndicatorSpinner.IsVisible = false;
                break;
        }
    }

    private async void ToolsIndicator_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            // The orange Syncing state is a transient progress signal, not a to-do - a click does nothing
            // (the Director is already fixing the drift automatically). Only the actionable Warning /
            // NeedsAttention states open Settings.
            if (_toolsSync.State == ToolsIndicatorState.Syncing)
            {
                FileLog.Write("[MainWindow] ToolsIndicator clicked while Syncing - ignored (auto-reconcile in progress)");
                return;
            }

            FileLog.Write("[MainWindow] ToolsIndicator clicked -> opening Settings on the Tools tab");
            await OpenSettingsAsync(onToolsTab: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] ToolsIndicator_PointerPressed FAILED: {ex.Message}");
        }
    }

    private global::Avalonia.Threading.DispatcherTimer? _schedulerLeaderTimer;
    private bool _lastLeaderState;

    private void RefreshSchedulerLeaderIndicator()
    {
        var scheduler = (global::Avalonia.Application.Current as App)?.Scheduler;
        var isLeader = scheduler?.IsLeader == true;
        if (isLeader == _lastLeaderState) return;

        _lastLeaderState = isLeader;
        Title = isLeader ? "Director -- Leader" : "Director";
    }

    private void SetBuildInfo()
    {
        try
        {
            // Product version front and center ("v0.6.3 (1cc1abd)"); the build
            // timestamp stays in the tooltip - it is still the fastest way to
            // confirm a local slot build actually deployed.
            BuildInfoText.Text = AppVersion.Display;
            var tip = $"Version: {AppVersion.Full}";
            var exePath = Environment.ProcessPath;
            if (exePath != null && File.Exists(exePath))
            {
                var buildTime = File.GetLastWriteTime(exePath);
                tip += $"\nBuilt: {buildTime:yyyy-MM-dd HH:mm:ss}\nPath: {exePath}";
            }
            ToolTip.SetTip(BuildInfoText, tip);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] SetBuildInfo FAILED: {ex.Message}");
            BuildInfoText.Text = "v?";
        }
    }

    // ==================== WORKSPACE LOADING ====================

    private async Task LoadWorkspaceAsync(WorkspaceDefinition workspace)
    {
        FileLog.Write($"[MainWindow] LoadWorkspaceAsync: '{workspace.Name}' with {workspace.Sessions.Count} sessions");

        var progress = new WorkspaceProgressDialog(workspace.Name);
        progress.Show(this);

        try
        {
            var sorted = workspace.Sessions.OrderBy(s => s.SortOrder).ToList();
            int total = sorted.Count;

            for (int i = 0; i < total; i++)
            {
                var entry = sorted[i];
                FileLog.Write($"[MainWindow] LoadWorkspaceAsync: creating session {i + 1}/{total}: {entry.RepoPath}");

                progress.UpdateProgress(i + 1, total, entry.CustomName ?? entry.RepoPath);

                var vm = CreateSession(entry.RepoPath, claudeArgs: entry.ClaudeArgs);
                if (vm != null)
                {
                    vm.Rename(entry.CustomName, entry.CustomColor);
                    SaveSessionToHistory(vm);
                }

                // Delay between sessions to prevent Claude Code settings corruption
                if (i < total - 1)
                    await Task.Delay(2500);
            }

            progress.SetComplete();
            FileLog.Write($"[MainWindow] LoadWorkspaceAsync: workspace '{workspace.Name}' loaded");
        }
        finally
        {
            progress.Close();
        }
    }


    // ==================== SESSION MANAGEMENT ====================

    /// <summary>
    /// Called by SessionManager.OnSessionCreated when a session was created from outside
    /// MainWindow (notably the web Manager via POST /sessions). Wraps the session in a
    /// SessionViewModel and adds it to the sidebar collection on the UI thread.
    /// </summary>
    private void OnExternalSessionCreated(Session session)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_sessions.Any(s => s.Session.Id == session.Id))
                {
                    FileLog.Write($"[MainWindow] OnExternalSessionCreated: session {session.Id} already wrapped, skipping");
                    return;
                }
                FileLog.Write($"[MainWindow] OnExternalSessionCreated: wrapping {session.Id} (repo={session.RepoPath})");
                var vm = new SessionViewModel(session);
                _sessions.Add(vm);

                // If nothing is currently shown, surface the externally-created session
                // (e.g. from the web Manager or Control API) instead of leaving the
                // terminal on the empty "Select a session to begin" state.
                if (_activeSession is null)
                {
                    SessionList.SelectedItem = vm;
                    FileLog.Write($"[MainWindow] OnExternalSessionCreated: auto-selected {session.Id} (no active session)");
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] OnExternalSessionCreated FAILED: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Called by SessionManager.OnSessionRenamed when a session's CustomName was
    /// updated from outside MainWindow (notably PATCH /sessions/{sid} on the Control API).
    /// Updates the matching SessionViewModel on the UI thread and triggers a persist.
    /// </summary>
    private void OnExternalSessionRenamed(Session session, string? newName)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var vm = _sessions.FirstOrDefault(s => s.Session.Id == session.Id);
                if (vm is null)
                {
                    FileLog.Write($"[MainWindow] OnExternalSessionRenamed: no VM for session {session.Id}");
                    return;
                }
                FileLog.Write($"[MainWindow] OnExternalSessionRenamed: session={session.Id}, name=\"{newName}\"");
                vm.Rename(newName);
                PersistSessionState();
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] OnExternalSessionRenamed FAILED: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Called by SessionManager.OnSessionRemoved when a session was removed from
    /// outside MainWindow (notably DELETE /sessions/{sid} on the Control API: a
    /// Cockpit/Gateway kill, or a session killing itself). Drops the matching
    /// rail row on the UI thread; the row used to stay behind forever, wrapping
    /// a dead disposed session (issue #202, root cause of #193).
    ///
    /// Idempotent by construction: the desktop's own close flows
    /// (CloseSessionAsync, CloseAllSessionsAsync) remove the row from _sessions
    /// BEFORE calling RemoveSession, so for those this finds no VM and no-ops.
    /// </summary>
    private void OnExternalSessionRemoved(Session session)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var vm = _sessions.FirstOrDefault(s => s.Session.Id == session.Id);
                if (vm is null) return; // desktop-initiated close already pruned the row
                FileLog.Write($"[MainWindow] OnExternalSessionRemoved: dropping rail row for {session.Id}");

                if (_activeSession == vm)
                {
                    // Same active-session teardown CloseSessionAsync performs:
                    // unhook the per-session handlers, detach every session-bound
                    // view, and fall back to the placeholder state.
                    vm.Session.OnClaudeMetadataChanged -= OnActiveSessionMetadataChanged;
                    vm.Session.OnActivityStateChanged -= OnActiveSessionActivityChanged;
                    vm.Session.OnPendingPromptTextChanged -= OnActiveSessionPendingPromptTextChanged;
                    TerminalHost.Detach();
                    GitChangesView.Detach();
                    _activeSession = null;

                    SetSessionHeaderVisible(false);
                    PlaceholderText.IsVisible = true;
                    TerminalDock.IsVisible = false;
                    PromptBarBorder.IsVisible = false;
                }

                _sessions.Remove(vm);
                PersistSessionState();
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] OnExternalSessionRemoved FAILED: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Last failure message from <see cref="CreateSession"/>, so UI callers can surface
    /// why a launch failed instead of swallowing it. Reset at the start of each attempt.
    /// </summary>
    private string? _lastSessionCreateError;

    /// <summary>Construct the <see cref="IAgent"/> strategy for the given agent kind.</summary>
    private IAgent CreateAgent(AgentKind agentKind) =>
        AgentPluginRegistry.CreateAgent(agentKind, _sessionManager.Options);

    /// <summary>
    /// Build a catalog agent (issue #490) whose executable path is the selected entry's configured
    /// path, not the legacy per-type machine path. The agents read their path from
    /// <see cref="AgentOptions"/>, so we hand them a per-launch copy of the running options with
    /// only this entry's path slotted into the matching property; all other options (buffer sizes,
    /// keys, default Claude args) are preserved. A blank entry path falls through to the running
    /// options' default for that type so a not-yet-configured entry still launches its standard
    /// binary. RawCli is handled by the caller via <see cref="RawCliAgent"/> and never reaches here.
    /// </summary>
    private IAgent CreateAgentForEntry(AgentKind agentKind, string entryExecutablePath) =>
        AgentPluginRegistry.CreateAgentWithPathOverride(agentKind, _sessionManager.Options, entryExecutablePath);

    /// <summary>Create a session using a pre-built <see cref="IAgent"/> (e.g. a
    /// <see cref="RawCliAgent"/> constructed by the dialog).</summary>
    private SessionViewModel? CreateSession(string repoPath, string? resumeSessionId, string? userArgs, IAgent agent, Guid? groupId = null, string? groupRole = null, string? groupName = null)
    {
        FileLog.Write($"[MainWindow] CreateSession: repoPath={repoPath}, agent={agent.Kind}, exe={agent.ExecutablePath}, group={groupId?.ToString() ?? "none"}, resume={resumeSessionId ?? "null"}");
        _lastSessionCreateError = null;
        try
        {
            var session = _sessionManager.CreateSession(repoPath, agent, userArgs, SessionBackendType.ConPty, resumeSessionId, groupId, groupRole, groupName);
            FileLog.Write($"[MainWindow] CreateSession: session created, id={session.Id}, pid={session.ProcessId}");

            var vm = new SessionViewModel(session);
            _sessions.Add(vm);
            SessionList.SelectedItem = vm;
            FileLog.Write($"[MainWindow] CreateSession: added to UI");

            return vm;
        }
        catch (Exception ex)
        {
            _lastSessionCreateError = ex.Message;
            FileLog.Write($"[MainWindow] CreateSession FAILED: {ex.Message}");
            return null;
        }
    }

    private SessionViewModel? CreateSession(string repoPath, string? resumeSessionId = null, string? claudeArgs = null, AgentKind agentKind = AgentKind.ClaudeCode, Guid? groupId = null, string? groupRole = null, string? groupName = null)
    {
        FileLog.Write($"[MainWindow] CreateSession: repoPath={repoPath}, agent={agentKind}, group={groupId?.ToString() ?? "none"}, resume={resumeSessionId ?? "null"}, args={claudeArgs ?? "default"}");
        _lastSessionCreateError = null;
        try
        {
            IAgent agent = CreateAgent(agentKind);
            var session = _sessionManager.CreateSession(repoPath, agent, claudeArgs, SessionBackendType.ConPty, resumeSessionId, groupId, groupRole, groupName);
            FileLog.Write($"[MainWindow] CreateSession: session created, id={session.Id}, pid={session.ProcessId}");

            var vm = new SessionViewModel(session);
            _sessions.Add(vm);
            SessionList.SelectedItem = vm;
            FileLog.Write($"[MainWindow] CreateSession: added to UI");

            return vm;
        }
        catch (Exception ex)
        {
            _lastSessionCreateError = ex.Message;
            FileLog.Write($"[MainWindow] CreateSession FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Create a GitHub Actions remote session: the work runs on a GitHub-hosted
    /// runner and streams into a normal session window. Surfaces setup failures
    /// (missing token, etc.) as an explicit dialog rather than silently failing.
    /// </summary>
    private async Task CreateRemoteSessionAsync(RemoteSessionConfig config)
    {
        FileLog.Write($"[MainWindow] CreateRemoteSessionAsync: {config.Slug} mode={config.TriggerMode}");
        try
        {
            var session = _sessionManager.CreateGitHubActionsSession(config);
            FileLog.Write($"[MainWindow] CreateRemoteSessionAsync: session created, id={session.Id}");

            var vm = new SessionViewModel(session);
            _sessions.Add(vm);
            SessionList.SelectedItem = vm;

            ShowRenameDialog(vm);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] CreateRemoteSessionAsync FAILED: {ex.Message}");
            await MessageBox.ShowAsync(this,
                "Could not start remote session",
                "Director could not start the GitHub Actions session.\n\n" + ex.Message);
        }
    }

    /// <summary>
    /// Human-readable name and install guidance for an agent CLI, shown when its
    /// executable cannot be found on PATH.
    /// </summary>
    private static (string DisplayName, string InstallHint) AgentInstallInfo(AgentKind kind)
    {
        var plugin = AgentPluginRegistry.Get(kind);
        return (plugin.DisplayName, plugin.Detection.InstallHint);
    }

    private void SelectSession(SessionViewModel? vm)
    {
        // Selecting a session returns to its terminal, dismissing an on-demand status view.
        if (vm != null && _statusRequested)
        {
            _statusRequested = false;
            HomeView.IsVisible = false;
        }

        // Close overlays when switching to any session
        if (CommsOverlay.IsVisible)
        {
            CommsOverlay.IsVisible = false;
            if (_commsInitialized)
                CommManagerView.StopPolling();
        }
        if (ConnectionsOverlay.IsVisible)
        {
            ConnectionsOverlay.IsVisible = false;
            if (_connectionsInitialized)
                ConnectionsView.StopPolling();
        }

        if (vm == _activeSession) return;

        // Save prompt text and selected tab for outgoing session
        if (_activeSession != null)
        {
            _activeSession.Session.PendingPromptText = PromptInput.Text;
            _activeSession.Session.SelectedTabName = _activeLeftTab;
            FileLog.Write($"[MainWindow] SelectSession: saved prompt and tab={_activeLeftTab} for {_activeSession.Session.Id}");

            _activeSession.Session.OnClaudeMetadataChanged -= OnActiveSessionMetadataChanged;
            _activeSession.Session.OnActivityStateChanged -= OnActiveSessionActivityChanged;
            _activeSession.Session.OnPendingPromptTextChanged -= OnActiveSessionPendingPromptTextChanged;
            _activeSession.Session.OnIsTranscribingChanged -= OnActiveSessionTranscribingChanged;
            TerminalHost.Detach();
            GitChangesView.Detach();
        }

        _activeSession = vm;

        // Driver-capability action buttons (Stop / Interrupt / Clear context /
        // History) follow the active session; null hides them all.
        ActionBar.Configure(_sessionManager, vm?.Session);

        if (vm == null)
        {
            SetSessionHeaderVisible(false);
            PlaceholderText.IsVisible = true;
            TerminalDock.IsVisible = false;
            PromptBarBorder.IsVisible = false;
            TabBarRefreshButton.IsVisible = false;
            TabBarCaptureButton.IsVisible = false;
            GitChangesView.Detach();
            return;
        }

        // Subscribe to metadata and activity changes for header updates
        vm.Session.OnClaudeMetadataChanged += OnActiveSessionMetadataChanged;
        vm.Session.OnActivityStateChanged += OnActiveSessionActivityChanged;
        // Subscribe to wingman-injected prompt text. The wingman watches the
        // terminal buffer for text Claude Code has placed in its own input line
        // and pushes it through this event; we mirror it into "Type a message..."
        // when the box is empty.
        vm.Session.OnPendingPromptTextChanged += OnActiveSessionPendingPromptTextChanged;
        // Lock the compose surface while this session is transcribing a dictated utterance in the
        // background, so a second Speak/Send/Queue cannot fire into a session mid-transcribe.
        vm.Session.OnIsTranscribingChanged += OnActiveSessionTranscribingChanged;

        // Update header
        SetSessionHeaderVisible(true);
        UpdateSessionHeader();

        // Attach terminal
        PlaceholderText.IsVisible = false;
        TerminalDock.IsVisible = true;
        TerminalHost.Attach(vm.Session);
        UpdateScrollBar();

        // Attach source control (hide tab if no .git)
        GitChangesView.Attach(vm.Session.RepoPath);
        UpdateSourceControlTabVisibility(vm.Session.RepoPath);

        // Show prompt bar
        PromptBarBorder.IsVisible = true;

        // Restore prompt text for incoming session
        PromptInput.Text = vm.Session.PendingPromptText ?? "";
        PromptInput.CaretIndex = PromptInput.Text.Length;

        // Restore last selected tab. The Session/Agent tabs, the Voice/History tabs, and the
        // Wingman tab were removed. Normalize any persisted values from older builds and default
        // to Terminal.
        var tabName = vm.Session.SelectedTabName;
        if (string.Equals(tabName, "Session", StringComparison.Ordinal) ||
            string.Equals(tabName, "Agent", StringComparison.Ordinal) ||
            string.Equals(tabName, "Voice", StringComparison.Ordinal) ||
            string.Equals(tabName, "History", StringComparison.Ordinal) ||
            string.Equals(tabName, "Wingman", StringComparison.Ordinal))
            tabName = "Terminal";
        if (string.IsNullOrEmpty(tabName)) tabName = "Terminal";
        if (tabName != _activeLeftTab)
            SwitchLeftTab(tabName);

        // Switch document tabs to new session
        SwitchDocumentTabsToSession(vm.Session.Id);

        // Refresh right panel for new session
        RefreshQueuePanel();

        // Apply the incoming session's transcribing lock (usually unlocked; locked if you switch to a
        // session that is still transcribing a dictated utterance in the background).
        ApplyComposeLock(vm.Session.IsTranscribing);

        // Persist session state (debounced)
        PersistSessionState();

        // Redirect focus to terminal or prompt
        if (_activeLeftTab == "Terminal")
            Dispatcher.UIThread.Post(() => TerminalHost.Focus());
        else
            Dispatcher.UIThread.Post(() => PromptInput.Focus());

        FileLog.Write($"[MainWindow] SelectSession: {vm.DisplayName}");
    }

    private void OnActiveSessionMetadataChanged(ClaudeSessionMetadata? metadata)
    {
        Dispatcher.UIThread.Post(UpdateSessionHeader);
    }

    /// <summary>True while the on-screen session is transcribing a dictated utterance in the
    /// background (the Speak-Send fire-and-forget window). The compose actions and their keyboard
    /// shortcuts no-op during this brief window so a second action cannot race the dictated prompt.</summary>
    private bool IsActiveSessionTranscribing() => _activeSession?.Session.IsTranscribing == true;

    /// <summary>Fires (possibly off the UI thread) when the active session's transcribing flag flips.
    /// Marshals to the UI thread and locks or unlocks the compose surface.</summary>
    private void OnActiveSessionTranscribingChanged(bool isTranscribing)
    {
        Dispatcher.UIThread.Post(() => ApplyComposeLock(isTranscribing));
    }

    /// <summary>
    /// Lock (or unlock) the prompt-bar compose surface for the active session. While a dictated
    /// utterance transcribes and submits in the background, the input box, Send, Speak, Queue,
    /// Explain and Handover are disabled, and the action bar's Clear context / History are disabled
    /// via <see cref="Controls.SessionActionBar.SetTranscribingLock"/> - Stop and Interrupt stay live.
    /// The keyboard shortcuts (Ctrl+H / Ctrl+Enter / Ctrl+Shift+Enter) are guarded separately by
    /// <see cref="IsActiveSessionTranscribing"/> so they no-op too. Unlocks automatically when the
    /// background send clears the flag.
    /// </summary>
    private void ApplyComposeLock(bool locked)
    {
        PromptInput.IsEnabled = !locked;
        BtnSend.IsEnabled = !locked;
        BtnSpeak.IsEnabled = !locked;
        BtnQueuePrompt.IsEnabled = !locked;
        BtnExplain.IsEnabled = !locked;
        BtnHandover.IsEnabled = !locked;
        ActionBar.SetTranscribingLock(locked);
        FileLog.Write($"[MainWindow] ApplyComposeLock: locked={locked}");
    }

    private void OnActiveSessionActivityChanged(ActivityState oldState, ActivityState newState)
    {
        Dispatcher.UIThread.Post(UpdateSessionHeader);
    }

    /// <summary>
    /// Mirror wingman-detected Claude Code prompt injections into the
    /// "Type a message..." textbox. Only acts on wingman-sourced writes
    /// (source=="wingman") so the textbox's own user-driven save (source=="user")
    /// doesn't loop back. Never clobbers text the user is currently composing.
    /// </summary>
    private void OnActiveSessionPendingPromptTextChanged(string? text, string source)
    {
        if (!string.Equals(source, "wingman", StringComparison.Ordinal)) return;
        if (string.IsNullOrEmpty(text)) return;
        // Capture into a non-nullable local so the lambda below sees a definite
        // string and we don't need the null-forgiving operator (forbidden by CodingStyle).
        string injectedText = text;

        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_activeSession is null) return;
                // Honor user input: only fill an empty box. If they've started typing,
                // the wingman's suggestion is silently dropped for this cycle.
                if (!string.IsNullOrEmpty(PromptInput.Text)) return;
                PromptInput.Text = injectedText;
                PromptInput.CaretIndex = injectedText.Length;
                FileLog.Write($"[MainWindow] wingman injected prompt text: len={injectedText.Length}, preview=\"{(injectedText.Length > 60 ? injectedText[..60] + "..." : injectedText)}\"");
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OnActiveSessionPendingPromptTextChanged FAILED: {ex.Message}");
        }
    }

    private async Task CloseAllSessionsAsync()
    {
        FileLog.Write("[MainWindow] CloseAllSessionsAsync");
        if (_activeSession != null)
        {
            _activeSession.Session.OnClaudeMetadataChanged -= OnActiveSessionMetadataChanged;
            _activeSession.Session.OnActivityStateChanged -= OnActiveSessionActivityChanged;
            _activeSession.Session.OnPendingPromptTextChanged -= OnActiveSessionPendingPromptTextChanged;
        }
        TerminalHost.Detach();
        GitChangesView.Detach();
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

        SetSessionHeaderVisible(false);
        PlaceholderText.IsVisible = true;
        TerminalDock.IsVisible = false;
        PromptBarBorder.IsVisible = false;

        FileLog.Write($"[MainWindow] CloseAllSessionsAsync: removed {snapshots.Count} session(s)");
    }

    // ==================== SIDEBAR COLLAPSE ====================

    /// <summary>Width of the collapsed sidebar strip: room for the status dots.</summary>
    private const double SidebarCollapsedWidth = 36;

    /// <summary>
    /// The sidebar column width before the last collapse, so expand restores the
    /// user's splitter-chosen width rather than snapping back to the default.
    /// </summary>
    private GridLength _sidebarExpandedWidth = new(264);

    private void SidebarCollapse_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            SetSidebarCollapsed(true, persist: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] SidebarCollapse_Click FAILED: {ex}");
        }
    }

    private void SidebarExpand_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            SetSidebarCollapsed(false, persist: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] SidebarExpand_Click FAILED: {ex}");
        }
    }

    /// <summary>
    /// Collapse the session sidebar to a slim status-dot strip, or expand it back.
    /// Swaps the full/slim panels, resizes the grid column, and disables the
    /// splitter while collapsed.
    /// </summary>
    private void SetSidebarCollapsed(bool collapsed, bool persist)
    {
        FileLog.Write($"[MainWindow] SetSidebarCollapsed: collapsed={collapsed}, persist={persist}");

        var column = MainLayoutGrid.ColumnDefinitions[0];
        if (collapsed)
        {
            // Remember the current (possibly splitter-resized) width for expand.
            if (column.Width.IsAbsolute && column.Width.Value > SidebarCollapsedWidth)
                _sidebarExpandedWidth = column.Width;
            column.Width = new GridLength(SidebarCollapsedWidth);
        }
        else
        {
            column.Width = _sidebarExpandedWidth;
        }

        SidebarFullPanel.IsVisible = !collapsed;
        SidebarSlimPanel.IsVisible = collapsed;
        SidebarSplitter.IsEnabled = !collapsed;

        if (persist)
            SidebarConfig.SetCollapsed(collapsed);
    }

    /// <summary>
    /// A status dot in the collapsed sidebar strip was clicked: select that session
    /// (same effect as clicking its row in the expanded list).
    /// </summary>
    private void SlimSessionDot_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Control { DataContext: SessionViewModel vm })
                return;
            FileLog.Write($"[MainWindow] SlimSessionDot_Click: {vm.Session.Id}");
            SessionList.SelectedItem = vm;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] SlimSessionDot_Click FAILED: {ex}");
        }
    }

    // ==================== SESSION CONTEXT MENU ====================

    private void SessionMenuButton_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] SessionMenuButton_Click");
        if (sender is not Button button)
            return;

        // Find the SessionViewModel from the button's DataContext
        var vm = button.DataContext as SessionViewModel;
        if (vm == null)
            return;

        var menu = new ContextMenu();

        // The menu is rebuilt on every open, so a plain AlphaMode.IsEnabled check below is
        // enough to gate alpha-only items - no AlphaMode.Changed rewiring is needed here.

        // --- Section 1: the session's own identity and lifecycle ---

        var rename = new MenuItem { Header = "Rename" };
        ToolTip.SetTip(rename, "Give this session a memorable name shown in the list.");
        rename.Click += (_, _) => ShowRenameDialog(vm);

        // On-hold toggle: parks the session out of the FIFO rotation and paints its
        // list strip dark blue so you can see at a glance which sessions you've set aside.
        var hold = new MenuItem { Header = vm.IsOnHold ? "Unsnooze" : "Snooze" };
        ToolTip.SetTip(hold, vm.IsOnHold
            ? "Unsnooze this session and return it to the \"Your Turn\" rotation."
            : "Snooze this session so it drops out of the \"Your Turn\" rotation and is marked dark blue.");
        hold.Click += (_, _) => ToggleSessionHold(vm);

        // --- Section 2: open the session's repository in an external tool ---

        var openExplorer = new MenuItem { Header = "Open in Explorer" };
        ToolTip.SetTip(openExplorer, "Open this session's repository folder in File Explorer.");
        openExplorer.Click += (_, _) => OpenInExplorer(vm);

        var openVsCode = new MenuItem { Header = "Open in VS Code" };
        ToolTip.SetTip(openVsCode, "Open this session's repository in Visual Studio Code.");
        openVsCode.Click += (_, _) => OpenInVsCode(vm);

        // --- Section 3: advanced / power-user actions ---

        // Copy a full handover block (session name + id, plus this Director's identity
        // and version) to the clipboard so it can be handed to another agent (e.g. via
        // the Control API) to locate, recall from memory, and talk to this exact session.
        var copyId = new MenuItem { Header = "Copy Handover Info" };
        ToolTip.SetTip(copyId, "Copy this session's name, id and Director identity so another agent can find and talk to it.");
        copyId.Click += async (_, _) =>
        {
            try
            {
                await CopySessionNameAndId(vm);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] Copy Handover Info FAILED: {ex.Message}");
                ShowNotification("Copy failed");
            }
        };

        // Save this live session as a reusable named session (issue #508): captures its repository,
        // agent and current name so it can be relaunched in one click from the New Session dialog's
        // Named Sessions tab. Saving from a running session means the repo and agent are real and
        // verified - the preset is valid the moment it is created.
        var saveNamed = new MenuItem { Header = "Save as named session" };
        ToolTip.SetTip(saveNamed, "Save this session's repository, agent and name as a reusable preset for one-click relaunch.");
        saveNamed.Click += async (_, _) =>
        {
            try
            {
                await SaveSessionAsNamedAsync(vm);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] Save as named session FAILED: {ex.Message}");
                ShowNotification("Could not save the named session");
            }
        };

        var relink = new MenuItem { Header = "Relink Session..." };
        ToolTip.SetTip(relink, "Recovery: re-point this row at a different underlying conversation transcript.");
        relink.Click += (_, _) => _ = ShowRelinkDialog(vm);

        // --- Section 4: close ---

        var close = new MenuItem { Header = "Close Session" };
        ToolTip.SetTip(close, "Close this session and remove it from the list.");
        close.Click += (_, _) => _ = CloseSessionAsync(vm);

        menu.Items.Add(rename);
        menu.Items.Add(hold);
        menu.Items.Add(new Separator());
        menu.Items.Add(openExplorer);
        menu.Items.Add(openVsCode);
        menu.Items.Add(new Separator());
        menu.Items.Add(copyId);
        menu.Items.Add(saveNamed);
        menu.Items.Add(relink);
        menu.Items.Add(new Separator());
        menu.Items.Add(close);

        menu.Open(button);
    }

    /// <summary>
    /// Save a live session as a named session (issue #508): name = the session's own name, plus its
    /// repository, its agent (resolved from <see cref="Session.AgentKind"/> back to a registered
    /// agent-entry id), and its colour. Confirms before overwriting an existing preset of the same
    /// name. The preset then appears on the New Session dialog's Named Sessions tab for one-click
    /// relaunch. Throws on failure; the caller (the menu Click handler) reports it.
    /// </summary>
    private async Task SaveSessionAsNamedAsync(SessionViewModel vm)
    {
        FileLog.Write($"[MainWindow] SaveSessionAsNamedAsync: session={vm.Session.Id}");

        var name = (vm.DisplayName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowNotification("Rename the session first, then save it as a named session.");
            return;
        }

        var repoPath = vm.Session.RepoPath;
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            ShowNotification("This session has no repository path to save.");
            return;
        }

        var store = new NamedSessionStore();
        var slug = NamedSessionStore.ToSlug(name);

        // A preset with this name already exists - confirm before overwriting it.
        if (store.Exists(slug))
        {
            var overwrite = await MessageBox.ShowConfirmAsync(this,
                "Named session exists",
                $"A named session called \"{name}\" already exists. Overwrite it?",
                "Overwrite", "Cancel");
            if (!overwrite)
            {
                FileLog.Write("[MainWindow] SaveSessionAsNamedAsync: user declined overwrite");
                return;
            }
        }

        // Resolve the session's agent kind back to a registered agent-entry id so the preset
        // relaunches with the same agent. No matching entry -> empty id (the preset shows
        // "Unavailable" until the agent is re-registered), which is the designed failure direction.
        var agentId = AgentEntryStore.ReadCurrentEntries()
            .FirstOrDefault(en => en.Enabled && en.Type == vm.Session.AgentKind)?.Id ?? "";

        var now = DateTimeOffset.UtcNow;
        var existing = store.Load(slug);
        var definition = new NamedSessionDefinition
        {
            Name = name,
            RepoPath = repoPath,
            AgentId = agentId,
            Color = vm.Session.CustomColor,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
        };

        if (store.Save(definition))
        {
            FileLog.Write($"[MainWindow] SaveSessionAsNamedAsync: saved \"{name}\" slug={slug} repo={repoPath} agent={agentId}");
            ShowNotification($"Saved \"{name}\" as a named session");
        }
        else
        {
            ShowNotification("Could not save the named session");
        }
    }

    private async void ToggleSessionHold(SessionViewModel vm)
    {
        // Snooze Length mission (Phase 3): snooze is Gateway-owned. Instead of setting Session.OnHold
        // in-process (which gave no timer), drive the Gateway hold seam so this snooze gets the same
        // Gateway-owned timer the phone and cockpit get. The Gateway records the snooze-until AND forwards
        // the hold back DOWN to this Director, which sets OnHold - so we never set it locally here.
        var target = !vm.Session.OnHold;
        var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;

        // No Gateway -> no snooze (owner rule, no-fallback): require a VERIFIED connection, which proves
        // BOTH legs the round-trip needs (Director->Gateway to record, Gateway->Director to forward back).
        if (host?.GatewayMonitor.Status != GatewayConnectionStatus.Connected || host.GatewayHold is null)
        {
            ShowNotification("You need to be connected to a Gateway to use snooze.");
            return;
        }

        // Immediate feedback (<100ms); the Gateway round-trip runs async off the UI thread - it is NOT
        // always loopback (the Gateway may be on another machine over Tailscale), so it may be slow.
        ShowNotification(target ? $"Snoozing {vm.DisplayName}..." : $"Waking {vm.DisplayName}...");
        try
        {
            await host.GatewayHold.RecordHoldAsync(vm.Session.Id.ToString(), target);
            FileLog.Write($"[MainWindow] ToggleSessionHold via Gateway: session={vm.Session.Id}, onHold={target}");
            ShowNotification(target ? $"{vm.DisplayName} snoozed" : $"{vm.DisplayName} taken off snooze");
        }
        catch (Exception ex)
        {
            // Fail loud: no local OnHold set, so nothing diverges from the Gateway's truth.
            FileLog.Write($"[MainWindow] ToggleSessionHold FAILED: session={vm.Session.Id}: {ex.Message}");
            ShowNotification($"Could not snooze {vm.DisplayName} - {ex.Message}");
        }
    }

    /// <summary>
    /// Copies a full handover block to the clipboard: the session's display name and
    /// stable ID plus the identity of the Director hosting it (Director ID, machine,
    /// version) and the Control API endpoint another machine can reach it at. When this
    /// node is on a tailnet the endpoint is the Tailscale Serve front door
    /// (https://&lt;magicdns&gt;:&lt;port&gt;) - the same address the Director advertises to
    /// the Gateway - so the block is usable from any tailnet machine, not just this one.
    /// This is everything another agent needs to locate the session and talk to it.
    /// </summary>
    private async Task CopySessionNameAndId(SessionViewModel vm)
    {
        var app = global::Avalonia.Application.Current as App;
        var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        var lines = new List<string>
        {
            $"Name: {vm.DisplayName}",
            $"Session ID: {vm.Session.Id}",
            $"Repo: {vm.RepoPath}",
            $"Director ID: {app?.ControlApiHost?.DirectorId ?? "(Control API not started)"}",
            $"Machine: {Environment.MachineName}",
            $"Version: {version}",
        };
        var port = app?.ControlApiHost?.Port;
        if (port is > 0)
        {
            // Resolving the tailnet front door shells the tailscale CLI (up to ~5s); keep
            // it off the UI thread so the copy action stays responsive.
            var endpoint = await Task.Run(() =>
                TailscaleIdentity.ResolveAdvertisedControlApiEndpoint(port.Value));
            lines.Add($"Control API: {endpoint}");
        }

        var text = string.Join("\n", lines);
        FileLog.Write($"[MainWindow] CopySessionNameAndId: session={vm.Session.Id}, director={app?.ControlApiHost?.DirectorId}, version={version}");
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
        {
            FileLog.Write("[MainWindow] CopySessionNameAndId: no clipboard available");
            ShowNotification("Clipboard unavailable");
            return;
        }
        await clipboard.SetTextAsync(text);
        ShowNotification($"Copied handover info for {vm.DisplayName}");
    }

    private async void ShowRenameDialog(SessionViewModel vm)
    {
        FileLog.Write($"[MainWindow] ShowRenameDialog: session={vm.Session.Id}, name={vm.DisplayName}");
        var dialog = new RenameSessionDialog(vm.DisplayName);
        var result = await dialog.ShowDialog<bool?>(this);

        if (result == true)
        {
            vm.Rename(dialog.SessionName, null);
            PersistSessionState();
            UpdateSessionHistory(vm);

            if (_activeSession == vm)
                UpdateSessionHeader();

            FileLog.Write($"[MainWindow] ShowRenameDialog: confirmed, name={dialog.SessionName}");
        }
        else
        {
            FileLog.Write("[MainWindow] ShowRenameDialog: cancelled");
        }
    }

    private async Task ShowRelinkDialog(SessionViewModel vm)
    {
        FileLog.Write($"[MainWindow] ShowRelinkDialog: session={vm.Session.Id}");
        var dialog = new RelinkSessionDialog(vm.Session.RepoPath);
        var result = await dialog.ShowDialog<bool?>(this);

        if (result == true && !string.IsNullOrEmpty(dialog.SelectedSessionId))
        {
            FileLog.Write($"[MainWindow] ShowRelinkDialog: relinking to {dialog.SelectedSessionId}");
            _sessionManager.RelinkClaudeSession(vm.Session.Id, dialog.SelectedSessionId);

            if (_activeSession == vm)
            {
                UpdateSessionHeader();
            }

            ShowNotification($"Session relinked to {dialog.SelectedSessionId[..8]}...");
        }
        else
        {
            FileLog.Write("[MainWindow] ShowRelinkDialog: cancelled");
        }
    }

    private void OpenInExplorer(SessionViewModel vm)
    {
        FileLog.Write($"[MainWindow] OpenInExplorer: {vm.Session.RepoPath}");
        if (!Directory.Exists(vm.Session.RepoPath))
        {
            ShowNotification($"Directory not found: {vm.Session.RepoPath}");
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = vm.Session.RepoPath,
            UseShellExecute = true,
        });
    }

    private void OpenInVsCode(SessionViewModel vm)
    {
        FileLog.Write($"[MainWindow] OpenInVsCode: {vm.Session.RepoPath}");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "code",
            Arguments = $"\"{vm.Session.RepoPath}\"",
            UseShellExecute = true,
        });
    }

    private async Task CloseSessionAsync(SessionViewModel vm)
    {
        FileLog.Write($"[MainWindow] CloseSessionAsync: session={vm.Session.Id}");

        if (_activeSession == vm)
        {
            vm.Session.OnClaudeMetadataChanged -= OnActiveSessionMetadataChanged;
            vm.Session.OnActivityStateChanged -= OnActiveSessionActivityChanged;
            vm.Session.OnPendingPromptTextChanged -= OnActiveSessionPendingPromptTextChanged;
            TerminalHost.Detach();
            GitChangesView.Detach();
            _activeSession = null;

            SetSessionHeaderVisible(false);
            PlaceholderText.IsVisible = true;
            TerminalDock.IsVisible = false;
            PromptBarBorder.IsVisible = false;
        }

        _sessions.Remove(vm);
        PersistSessionState();

        await Task.Run(async () =>
        {
            try
            {
                await _sessionManager.KillSessionAsync(vm.Session.Id);
                _sessionManager.RemoveSession(vm.Session.Id);
                FileLog.Write($"[MainWindow] CloseSessionAsync: cleanup complete for {vm.Session.Id}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] CloseSessionAsync cleanup FAILED: {ex.Message}");
            }
        });
    }

    private const int PersistDebounceMs = 250;
    private CancellationTokenSource? _persistDebounceCts;

    private void PersistSessionState()
    {
        // Sync prompt text on the UI thread before background debounce
        SyncPromptTextToSessions();

        _persistDebounceCts?.Cancel();
        _persistDebounceCts = new CancellationTokenSource();
        var cts = _persistDebounceCts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PersistDebounceMs, cts.Token);
                PersistSessionStateCore();
            }
            catch (TaskCanceledException) { /* debounce superseded */ }
        });
    }

    private void PersistSessionStateCore()
    {
        FileLog.Write("[MainWindow] PersistSessionStateCore");
        try
        {
            var app = (App)global::Avalonia.Application.Current!;
            var persisted = _sessions.Select((vm, i) => new PersistedSession
            {
                Id = vm.Session.Id,
                RepoPath = vm.Session.RepoPath,
                ClaudeArgs = vm.Session.ClaudeArgs,
                CustomName = vm.Session.CustomName,
                CustomColor = vm.Session.CustomColor,
                ClaudeSessionId = vm.Session.ClaudeSessionId,
                ActivityState = vm.Session.ActivityState,
                // Defect 22: a snooze must survive a Director restart. This site is the one that actually
                // runs - SessionManager.BuildPersistedSessions writes a FULLER record but has no
                // production caller (only tests), so persisting the hold there alone would have been a
                // no-op. Both sites carry it; see docs/new_architecture/session-state.html.
                HoldState = vm.Session.HoldState,
                BackendType = vm.Session.BackendType,
                PendingPromptText = vm.Session.PendingPromptText,
                WingmanEnabled = vm.Session.WingmanEnabled,
                SortOrder = i,
            });
            app.SessionStateStore.Save(persisted);

            // Mirror the live roster into the durable crash journal (issue #212 L5). Same
            // snapshot, but keyed per-Director and preserved across an abnormal death so the
            // sessions can be recovered (unlike sessions.json, which is cleared every startup).
            app.CrashJournal?.Update(_sessions.Select(vm => new DirectorCrashJournalSession
            {
                SessionId = vm.Session.Id.ToString(),
                Name = vm.Session.CustomName,
                RepoPath = vm.Session.RepoPath,
                Agent = vm.Session.AgentKind.ToString(),
                ClaudeSessionId = vm.Session.ClaudeSessionId,
                CreatedAtUtc = vm.Session.CreatedAt,
            }));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] PersistSessionStateCore FAILED: {ex.Message}");
        }
    }

    private void SyncPromptTextToSessions()
    {
        for (int i = 0; i < _sessions.Count; i++)
        {
            _sessions[i].Session.SortOrder = i;
        }

        if (_activeSession != null)
        {
            _activeSession.Session.PendingPromptText = PromptInput.Text;
            _activeSession.Session.SelectedTabName = _activeLeftTab;
        }
    }

    // ==================== SESSION HEADER ====================

    private void BtnOpenRemoteThread_Click(object? sender, RoutedEventArgs e)
    {
        var url = _activeSession?.Session.RemoteThreadUrl;
        OpenUrlInBrowser(url);
    }

    private void BtnOpenRemoteActions_Click(object? sender, RoutedEventArgs e)
    {
        var slug = _activeSession?.Session.RemoteRepo;
        if (string.IsNullOrEmpty(slug)) return;
        OpenUrlInBrowser($"https://github.com/{slug}/actions");
    }

    // Open the Cockpit. We ASK THE CONFIGURED GATEWAY (GET /cockpit) rather than hardcoding a
    // host/port: the gateway owns the Cockpit port and returns its Tailscale front-door URL.
    // The base URL comes from the one configured source of truth -- GatewayConfig (the gateway
    // block of config.json). When a remote gateway is configured we probe IT; we fall back to
    // the local 127.0.0.1:7878 default ONLY when no gateway URL is configured at all (the
    // same-machine setup). There is NO localhost fallback in the browser: if the gateway has no
    // tailnet URL (Tailscale down) we say so and open nothing, never a loopback URL that only
    // works on this machine. Both failure paths surface as a modal dialog naming the URL we
    // actually probed: a toolbar button that silently does nothing is just confusing.
    private async void BtnCockpit_Click(object? sender, RoutedEventArgs e)
    {
        var baseUrl = CockpitUrlResolver.ResolveCockpitBase(GatewayConfig.Load());
        FileLog.Write($"[MainWindow] BtnCockpit_Click: asking gateway for Cockpit URL, baseUrl={baseUrl}");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var info = await http.GetFromJsonAsync<global::CcDirector.Gateway.Contracts.CockpitInfoDto>(
                baseUrl + "/cockpit");
            if (info?.Url is { } url)
            {
                FileLog.Write($"[MainWindow] BtnCockpit_Click: opening {url} (up={info.Up}, baseUrl={baseUrl})");
                OpenUrlInBrowser(url);
            }
            else
            {
                FileLog.Write($"[MainWindow] BtnCockpit_Click: gateway at {baseUrl} returned no Tailscale URL (Tailscale unavailable); opening nothing. cc-director never opens a localhost URL.");
                await new MessageDialog(
                    "Cannot Open Cockpit",
                    "Tailscale is unavailable on this machine, so there is no tailnet URL for the " +
                    "Cockpit. Bring Tailscale up and try again. Director never opens a localhost " +
                    "URL because it would only work on this one machine.")
                    .ShowDialog<bool?>(this);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnCockpit_Click FAILED (baseUrl={baseUrl}): {ex.Message}");
            // The "is the Gateway tray app running on THIS machine?" hint only makes sense for
            // the loopback default. For a configured remote gateway the failure is about
            // reachability (the remote gateway is down, or the tailnet is unreachable).
            await new MessageDialog(
                "Cannot Open Cockpit",
                BuildGatewayUnreachableMessage(baseUrl, ex.Message))
                .ShowDialog<bool?>(this);
        }
    }

    // Builds the "could not reach the gateway" message shared by the Cockpit and Learn buttons
    // (#475). The "is the Gateway tray app running on THIS machine?" hint only makes sense for
    // the loopback default; for a configured remote gateway the failure is about reachability
    // (the remote gateway is down, or the tailnet is unreachable). Pure string building, so it
    // is unit-testable without a UI thread.
    internal static string BuildGatewayUnreachableMessage(string baseUrl, string error)
    {
        var hint = CockpitUrlResolver.IsLocalhostDefault(baseUrl)
            ? "\n\nIs the Gateway tray app (devthrottle-gateway) running on this machine?"
            : "\n\nIs the Gateway running on that machine and reachable over your tailnet?";
        return $"Could not reach the gateway at {baseUrl}: {error}{hint}";
    }

    // Builds the Cockpit Learning page URL from the gateway's Tailscale front-door URL (#475).
    // Appends the Learning route (#472) with a single, clean separator so a front door that
    // ends in a slash never yields "//learn". Pure string building, so it is unit-testable.
    internal static string BuildLearnUrl(string frontDoorUrl) =>
        frontDoorUrl.TrimEnd('/') + CockpitLearnRoute;

    // The Cockpit Learning page route (#472). Appended to the Cockpit front-door URL
    // resolved through the gateway, so the Learn button lands on {frontDoor}/learn.
    private const string CockpitLearnRoute = "/learn";

    // Open the Cockpit Learning page (#475). This reuses the SAME resolution as the Cockpit
    // button -- we ASK THE CONFIGURED GATEWAY (GET {base}/cockpit) for the Tailscale front-door
    // URL rather than hardcoding a host/port -- then open {frontDoor}/learn. cc-director never
    // opens a localhost URL: when the gateway has no tailnet URL (Tailscale down) or cannot be
    // reached at all, we surface the explicit "is the Gateway running?" hint and open nothing,
    // never a silent no-op and never a loopback URL that only works on this machine.
    private async void BtnLearn_Click(object? sender, RoutedEventArgs e)
    {
        var baseUrl = CockpitUrlResolver.ResolveCockpitBase(GatewayConfig.Load());
        FileLog.Write($"[MainWindow] BtnLearn_Click: asking gateway for Cockpit URL, baseUrl={baseUrl}");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var info = await http.GetFromJsonAsync<global::CcDirector.Gateway.Contracts.CockpitInfoDto>(
                baseUrl + "/cockpit");
            if (info?.Url is { } frontDoor)
            {
                var learnUrl = BuildLearnUrl(frontDoor);
                FileLog.Write($"[MainWindow] BtnLearn_Click: opening {learnUrl} (up={info.Up}, baseUrl={baseUrl})");
                OpenUrlInBrowser(learnUrl);
            }
            else
            {
                FileLog.Write($"[MainWindow] BtnLearn_Click: gateway at {baseUrl} returned no Tailscale URL (Tailscale unavailable); opening nothing. cc-director never opens a localhost URL.");
                await new MessageDialog(
                    "Cannot Open Learning Page",
                    "Tailscale is unavailable on this machine, so there is no tailnet URL for the " +
                    "Cockpit Learning page. Bring Tailscale up and try again. Director never opens " +
                    "a localhost URL because it would only work on this one machine.")
                    .ShowDialog<bool?>(this);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnLearn_Click FAILED (baseUrl={baseUrl}): {ex.Message}");
            await new MessageDialog(
                "Cannot Open Learning Page",
                BuildGatewayUnreachableMessage(baseUrl, ex.Message))
                .ShowDialog<bool?>(this);
        }
    }

    private static void OpenUrlInBrowser(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OpenUrlInBrowser FAILED for {url}: {ex.Message}");
        }
    }

    // Top bar accent: sidebar-colored when idle, blue when a session is active.
    private static readonly IBrush TopBarIdleBrush = new SolidColorBrush(Color.Parse("#252526"));
    private static readonly IBrush TopBarActiveBrush = new SolidColorBrush(Color.Parse("#007ACC"));

    // Show or hide the per-session identity block in the unified top bar. The bar
    // itself is always visible (so the global tools can never be occluded); only the
    // identity content and the bar's accent color change with the active session.
    private void SetSessionHeaderVisible(bool visible)
    {
        SessionHeaderBanner.IsVisible = visible;
        TopBar.Background = visible ? TopBarActiveBrush : TopBarIdleBrush;
    }

    private void UpdateSessionHeader()
    {
        if (_activeSession == null) return;

        var session = _activeSession.Session;
        // Issue #820: prefix the per-session header title with the three-digit number when present.
        HeaderSessionName.Text = _activeSession.HasNumber
            ? $"{_activeSession.NumberBadge}  {_activeSession.DisplayName}"
            : _activeSession.DisplayName;
        HeaderActivityLabel.Text = _activeSession.ActivityLabel;

        // GitHub Actions remote sessions get a links row (repo slug + thread + Actions).
        if (session.IsRemote)
        {
            HeaderRemoteLinks.IsVisible = true;
            HeaderRemoteRepo.Text = session.RemoteRepo ?? "";
            // "Open thread" is only useful once the thread exists; the run links are in
            // the streamed buffer too, but the Actions button is always reachable.
            BtnOpenRemoteThread.IsEnabled = !string.IsNullOrEmpty(session.RemoteThreadUrl);
        }
        else
        {
            HeaderRemoteLinks.IsVisible = false;
        }

        // Message count
        var msgCount = session.ClaudeMetadata?.MessageCount ?? 0;
        if (msgCount > 0)
        {
            HeaderMessageCountText.Text = $"{msgCount} msgs";
            HeaderMessageCountBadge.IsVisible = true;
        }
        else
        {
            HeaderMessageCountBadge.IsVisible = false;
        }


        UpdateHeaderVerification(_activeSession);
    }

    private static readonly ISolidColorBrush VerifiedBadgeBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly ISolidColorBrush WarningBadgeBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));

    private void UpdateHeaderVerification(SessionViewModel vm)
    {
    }

    private void CheckTerminalVerification()
    {
        if (_activeSession == null) return;

        var status = _activeSession.TerminalVerificationStatus;
        if (status == TerminalVerificationStatus.Matched)
            return;

        var session = _activeSession;
        var terminalText = TerminalHost.GetAllTerminalText();
        if (string.IsNullOrEmpty(terminalText)) return;

        var lineCount = terminalText.Split('\n').Length;
        if (lineCount < 5) return;

        FileLog.Write($"[MainWindow] CheckTerminalVerification: contentLines={lineCount}, status={status}, session={session.Session.Id}");

        Task.Run(() =>
        {
            try
            {
                var result = session.Session.VerifyWithTerminalContent(terminalText, lineCount);

                Dispatcher.UIThread.Post(() =>
                {
                    if (result.IsMatched)
                    {
                        FileLog.Write($"[MainWindow] Terminal verification CONFIRMED: {result.MatchedSessionId} for {session.Session.Id}");
                        if (!string.IsNullOrEmpty(result.MatchedSessionId))
                            _sessionManager.RegisterClaudeSession(result.MatchedSessionId, session.Session.Id);
                        if (_activeSession?.Session.Id == session.Session.Id)
                            UpdateSessionHeader();
                        PersistSessionState();
                    }
                    else if (result.IsPotential)
                    {
                        FileLog.Write($"[MainWindow] Terminal verification POTENTIAL: {result.MatchedSessionId} for {session.Session.Id} ({lineCount} lines)");
                        if (!string.IsNullOrEmpty(result.MatchedSessionId))
                            _sessionManager.RegisterClaudeSession(result.MatchedSessionId, session.Session.Id);
                        if (_activeSession?.Session.Id == session.Session.Id)
                            UpdateSessionHeader();
                        PersistSessionState();
                    }
                    else
                    {
                        FileLog.Write($"[MainWindow] Terminal verification no match: {result.ErrorMessage} for {session.Session.Id} ({lineCount} lines)");
                    }
                });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MainWindow] CheckTerminalVerification FAILED: {ex.Message}");
            }
        });
    }

    private async void BtnRelink_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnRelink_Click");
        if (_activeSession == null) return;
        await ShowRelinkDialog(_activeSession);
    }

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+H from anywhere on the window opens the Speak dialog -- but only
        // when the prompt bar is visible (Terminal tab + active session). Other
        // tabs/states don't have a Speak target.
        if (e.Key == Key.H && e.KeyModifiers == KeyModifiers.Control)
        {
            if (!PromptBarBorder.IsVisible)
            {
                FileLog.Write("[MainWindow] Ctrl+H ignored: prompt bar not visible");
                return;
            }
            // Locked while the session transcribes a dictated utterance in the background: swallow the
            // keystroke so Ctrl+H cannot open a second Speak dialog mid-transcribe.
            if (IsActiveSessionTranscribing())
            {
                FileLog.Write("[MainWindow] Ctrl+H ignored: session transcribing");
                e.Handled = true;
                return;
            }
            FileLog.Write("[MainWindow] Ctrl+H -> BtnSpeak_Click");
            e.Handled = true;
            BtnSpeak_Click(this, new RoutedEventArgs());
        }
    }

    // Explain: pop a small modal that asks the Wingman to read the active session's
    // terminal and explain, in plain language, what happened and what the agent wants.
    // The dialog runs the same read-only briefing the FIFO conveyor uses
    // (WingmanService.BriefingQuestion over AnswerViaSessionAsync), so honing that one
    // briefing improves both. The dialog owns its own cancellation; it appears at once
    // and the call resolves a few seconds later.
    private async void BtnExplain_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var vm = _activeSession;
            if (vm is null)
            {
                ShowNotification("Select a session first to explain it.");
                return;
            }
            var options = (global::Avalonia.Application.Current as App)?.SessionManager?.Options;
            if (options is null)
            {
                FileLog.Write("[MainWindow] BtnExplain_Click: AgentOptions not available");
                ShowNotification("Explain not available: AgentOptions not loaded.");
                return;
            }
            FileLog.Write($"[MainWindow] BtnExplain_Click: explaining session {vm.Session.Id}");
            var dlg = new global::CcDirector.Avalonia.Controls.ExplainDialog(vm.Session, options);
            await dlg.ShowDialog(this);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnExplain_Click FAILED: {ex.Message}");
            ShowNotification($"Explain failed: {ex.Message}");
        }
    }

    private async void BtnSpeak_Click(object? sender, RoutedEventArgs e)
    {
        // Locked out while the active session is still transcribing a previous dictation in the
        // background: no second Speak into a session mid-transcribe (guards the click AND Ctrl+H).
        if (IsActiveSessionTranscribing())
        {
            FileLog.Write("[MainWindow] BtnSpeak_Click ignored: session transcribing");
            return;
        }
        // Desktop dictation. Opens SpeakDialog which captures audio via NAudio
        // (BatchDictationRecorder), sends the completed audio to the Gateway transcription owner, then
        // returns the corrected transcript which we insert into PromptInput.
        try
        {
            var app = global::Avalonia.Application.Current as App;
            var options = app?.SessionManager?.Options;
            if (options is null)
            {
                FileLog.Write("[MainWindow] BtnSpeak_Click: no AgentOptions available");
                ShowNotification("Dictation not available: AgentOptions not loaded.");
                return;
            }
            if (!await global::CcDirector.Avalonia.HostedAi.DesktopHostedAiGate.EnsureReadyAsync(this))
                return;
            FileLog.Write("[MainWindow] BtnSpeak_Click: opening SpeakDialog");
            // Snapshot the caret BEFORE opening the dialog. Focus moves to the
            // dialog, and on some controls CaretIndex can be reset to 0 after
            // focus loss, which would cause inserted text to land at position 0
            // (effectively prepending instead of inserting at the user's caret).
            var existingTextBefore = PromptInput.Text ?? "";
            var caretBefore = PromptInput.CaretIndex;
            if (caretBefore < 0 || caretBefore > existingTextBefore.Length)
                caretBefore = existingTextBefore.Length;
            // Capture the session the user is dictating INTO now, so a fire-and-forget Send submits
            // to the right session even if the user switches away while it transcribes in background.
            var target = _activeSession?.Session;
            var dlg = new global::CcDirector.Avalonia.Voice.SpeakDialog(options)
            {
                // Immediate (fire-and-forget) Send needs a target session to submit into; enable it
                // whenever we have one so pressing Send releases the screen at once.
                EnableBackgroundSend = target is not null,
            };
            await dlg.ShowDialog(this);

            // Immediate Send: the dialog handed us the still-capturing recorder and closed at once. The
            // screen is already released; transcribe + submit in the background while the session shows
            // orange "Transcribing...". Send behaves exactly like the Insert button followed by Enter:
            // the dictation is dropped at the caret we snapshotted, inside any typed text (via the same
            // DictationText.InsertAt the Insert button uses), then submitted. The dialog was modal, so
            // the box could not change since we snapshotted it; clear it now so the user does not see -
            // or re-send - already-committed text. On ANY failure the words are put back in the compose
            // box (OnDictationFailed) so they are never lost.
            if (dlg.IsBackgroundSend && dlg.BackgroundRecorder is not null && target is not null)
            {
                var recorder = dlg.BackgroundRecorder;
                var composerText = existingTextBefore;
                var caret = caretBefore;
                // Split the typed text at the caret so the dictation lands exactly where the user's
                // caret was, dropping neither the typed part nor the spoken part.
                var before = composerText[..caret];
                var after = composerText[caret..];
                PromptInput.Text = "";
                EnsureDictationInfra(options);
                FileLog.Write($"[MainWindow] BtnSpeak_Click: background dictation send to session {target.Id}, composer chars={composerText.Length}, caret={caret}");
                _ = global::CcDirector.Avalonia.Voice.BackgroundDictationSend.RunAsync(
                    recorder, dlg.BackgroundPrefix, target, _dictationTranscriber!,
                    submit: text => global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                        () => SubmitDictatedTextAsync(target, text)),
                    before: before,
                    after: after,
                    onFailed: (err, composedText) => global::Avalonia.Threading.Dispatcher.UIThread.Post(
                        () => OnDictationFailed(target, composerText, composedText, err)));
                return;
            }

            // Insert, or a blocking Send when there was no target session: the dialog already
            // transcribed and returned the text. Insert it at the caret we snapshotted (joining with
            // anything already typed) and submit only if the user chose Send.
            var transcript = dlg.ResultText;
            if (string.IsNullOrWhiteSpace(transcript))
            {
                FileLog.Write("[MainWindow] BtnSpeak_Click: dialog returned no text (cancelled or errored)");
                return;
            }
            InsertIntoPromptInputAt(transcript!, caretBefore);
            FileLog.Write($"[MainWindow] BtnSpeak_Click: inserted {transcript!.Length} chars at caret={caretBefore}, shouldSubmit={dlg.ShouldSubmit}");
            if (dlg.ShouldSubmit)
            {
                SendPrompt();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnSpeak_Click FAILED: {ex.Message}");
            ShowNotification($"Dictation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Insert transcript text at the given caret index in PromptInput. Adds
    /// whitespace separators when needed so the new words do not smush
    /// against existing characters. Caller is expected to snapshot the caret
    /// BEFORE any focus change (e.g. before opening a modal dialog), because
    /// CaretIndex on a TextBox that has just lost focus can be 0.
    /// </summary>
    private void InsertIntoPromptInputAt(string text, int caret)
    {
        var existing = PromptInput.Text ?? "";
        if (caret < 0 || caret > existing.Length) caret = existing.Length;
        var suffixLen = existing.Length - caret;
        var composed = global::CcDirector.Avalonia.Voice.DictationText.InsertAt(existing, caret, text);
        PromptInput.Text = composed;
        // Caret lands right after the inserted content: the untouched suffix is still at the tail, so
        // the insertion ends at composed.Length - suffixLen.
        PromptInput.CaretIndex = composed.Length - suffixLen;
        PromptInput.Focus();
    }

    /// <summary>
    /// Submit a background-dictated message straight into a specific session (fire-and-forget Send,
    /// spec section 10). Unlike <see cref="SendPrompt"/> this does NOT read the compose box - it
    /// targets the session the user dictated into (captured when the Speak dialog opened), so it lands
    /// in the right session even if the user has since switched away or typed something else. Keeps
    /// the important parts of the normal send: a history snapshot (a rewind point) and the shared
    /// submit path. Runs on the UI thread.
    /// </summary>
    private async Task SubmitDictatedTextAsync(Session target, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrEmpty(text)) return;

        FileLog.Write($"[MainWindow] SubmitDictatedTextAsync: {text.Length} chars to session {target.Id}");

        // Rewind point, exactly like SendPrompt.
        target.InitializeHistory();
        target.History?.TakeSnapshot();

        // DevThrottle Stats: desktop dictation - spoken input from the local machine.
        await target.SendTextAsync(text, origin: InputOrigin.DesktopVoice);
    }

    // ===== Fire-and-forget dictation delivery =====
    //
    // A desktop Send/Speak dictation either goes into its session now or fails loudly at once - there is
    // NO durable queue, NO background retry, and NO readiness pre-check. BackgroundDictationSend
    // transcribes off the UI thread and submits straight through the echo-verified terminal submit; that
    // submit itself is the only arbiter of "the session took the text" (the old ActivityState gate
    // rejected healthy idle sessions because that state lags real silence by 10 seconds - see issue
    // #1308). On failure the words are put back in the compose box and reported with a modal, so
    // nothing the user said is ever lost. The recorded WAV itself is saved to disk before the
    // transcription attempt (DictationRecordingStore) and deleted once the words are safe; when
    // transcription fails there is no text to restore, so the file is kept and the failure modal
    // names its path - the audio is the only remaining copy of what was said.

    private global::CcDirector.Core.Transcription.IDictationTranscriber? _dictationTranscriber;
    private bool _dictationInfraReady;

    /// <summary>
    /// Build the dictation transcriber once (idempotent). Needs <see cref="AgentOptions"/> for the
    /// transcriber's method/dictionary resolution, so it is built the first time a dictation is sent.
    /// </summary>
    private void EnsureDictationInfra(AgentOptions options)
    {
        if (_dictationInfraReady) return;
        _dictationTranscriber = new global::CcDirector.Core.Transcription.DictationTranscriber(options);
        _dictationInfraReady = true;
        FileLog.Write("[MainWindow] dictation transcriber ready");
    }

    /// <summary>
    /// A fire-and-forget dictation could not be delivered: transcription failed, or the submit into the
    /// session's composer failed. There is no queue and no retry - put the words back in the compose box
    /// so nothing is lost, and report it with a modal so the failure is impossible to miss (the old
    /// silent hold-notice everyone missed is gone). <paramref name="composedText"/> is the full composed
    /// turn (typed text with the transcript inserted at the caret) when transcription succeeded but the
    /// submit failed; null when the failure happened before a transcript existed, in which case only the
    /// typed <paramref name="composerText"/> can be restored. Runs on the UI thread.
    /// </summary>
    private async void OnDictationFailed(Session target, string composerText, string? composedText, string error)
    {
        try
        {
            var restore = composedText ?? composerText;
            if (!string.IsNullOrEmpty(restore))
            {
                if (_activeSession?.Session == target)
                    InsertIntoPromptInputAt(restore, 0);
                else
                    target.PendingPromptText = global::CcDirector.Avalonia.Voice.DictationText.Join(restore, target.PendingPromptText ?? "");
            }
            var whatSurvived = composedText is not null
                ? "The transcribed text has been put in the message box - review it and press Send when you are ready."
                : "Any text you had typed has been put back - dictate again when you are ready.";
            await MessageBox.ShowAsync(this, "Dictation not sent",
                $"Your dictation was not sent: {error}\n\nNothing was queued. {whatSurvived}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OnDictationFailed FAILED: {ex.Message}");
        }
    }

    private void BtnOpenInBrowser_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_activeSession is null)
            {
                FileLog.Write("[MainWindow] BtnOpenInBrowser_Click: no active session");
                return;
            }
            var app = global::Avalonia.Application.Current as App;
            var port = app?.ControlApiHost?.Port;
            if (port is null or 0)
            {
                FileLog.Write("[MainWindow] BtnOpenInBrowser_Click: ControlApi port not available");
                ShowNotification("Web view not available: Control API has not started yet.");
                return;
            }
            var url = $"http://127.0.0.1:{port}/sessions/{_activeSession.Session.Id}/view";
            FileLog.Write($"[MainWindow] BtnOpenInBrowser_Click: opening {url}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnOpenInBrowser_Click FAILED: {ex.Message}");
            ShowNotification($"Could not open browser: {ex.Message}");
        }
    }

    private void TabBarRefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] TabBarRefreshButton_Click");
        RefreshTerminal();
    }

    private void TabBarCaptureButton_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] TabBarCaptureButton_Click");
        var capturePath = TerminalHost.DumpDiagnosticCapture();
        if (capturePath != null)
        {
            var fileName = System.IO.Path.GetFileName(capturePath);
            HeaderActivityLabel.Text = $"Captured -> {fileName}";
            FileLog.Write($"[MainWindow] TabBarCaptureButton_Click: captured to {capturePath}");
        }
    }

    private void RefreshTerminal()
    {
        if (_activeSession == null) return;

        TerminalHost.ForceRefresh();
        UpdateScrollBar();
        FileLog.Write("[MainWindow] RefreshTerminal: terminal refreshed");
    }

    private void OnTerminalScrollChanged(object? sender, EventArgs e)
    {
        UpdateScrollBar();
        CheckTerminalVerification();
    }

    private void UpdateScrollBar()
    {
        // Read scrollback size, viewport height, and offset from a single
        // atomic snapshot. Avoids the prior bug where three independent
        // property reads could see different intermediate states of the
        // scrollback list while the parser was growing it concurrently.
        var snap = TerminalHost.GetScrollSnapshot();

        // On the alternate screen the snapshot reports the recovered alt-screen
        // scrollback (issue #761). Until a full-screen agent has repainted any lines
        // off the top there is nothing to scroll, so hide the bar -- a visible-but-dead
        // scrollbar reads as "scrolling is broken". Once history exists, show it so the
        // user can scroll back through the running agent's transcript.
        if (TerminalHost.IsOnAlternateScreen && snap.ScrollbackCount == 0)
        {
            TerminalScrollBar.IsVisible = false;
            return;
        }
        TerminalScrollBar.IsVisible = true;

        // Avalonia's ScrollBar hides its thumb when Maximum == 0. When there
        // is no scrollback yet we still want a visible thumb filling the
        // entire track ("you're viewing everything"), so floor Maximum at 1.
        int maximum = Math.Max(snap.ScrollbackCount, 1);

        _updatingScrollBar = true;
        TerminalScrollBar.Maximum = maximum;
        TerminalScrollBar.ViewportSize = snap.ViewportRows;
        TerminalScrollBar.LargeChange = snap.ViewportRows;
        TerminalScrollBar.SmallChange = 3;
        TerminalScrollBar.Value = maximum - snap.ScrollOffset;
        _updatingScrollBar = false;
    }

    private void TerminalScrollBar_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ScrollBar.ValueProperty) return;
        if (_updatingScrollBar) return;

        _updatingScrollBar = true;
        int offset = (int)(TerminalScrollBar.Maximum - TerminalScrollBar.Value);
        TerminalHost.ScrollOffset = offset;
        _updatingScrollBar = false;
    }

    private async void OnTerminalBrowserLaunchFailed(string message)
    {
        FileLog.Write($"[MainWindow] OnTerminalBrowserLaunchFailed: {message}");
        try
        {
            await MessageBox.ShowAsync(this, "Open in Browser", message);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OnTerminalBrowserLaunchFailed FAILED: {ex.Message}");
        }
    }

    private void OnTerminalViewFileRequested(string path)
    {
        FileLog.Write($"[MainWindow] OnTerminalViewFileRequested: {path}");
        try
        {
            if (FileExtensions.IsViewable(path) && File.Exists(path))
            {
                OpenDocumentFile(path);
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OnTerminalViewFileRequested FAILED: {ex.Message}");
        }
    }

    // ==================== EVENT HANDLERS ====================

    private void SessionList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // When an overlay is open, any click on the session list should close it
        // even if the same session is already selected (SelectionChanged won't fire)
        if ((CommsOverlay.IsVisible || ConnectionsOverlay.IsVisible) && _activeSession != null)
            SelectSession(_activeSession);
    }

    private void SessionList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionViewModel vm)
            SelectSession(vm);
    }

    // --- Session drag-and-drop reorder ---

    private async void ColorSquare_PointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not SessionViewModel vm)
            return;

        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;

        FileLog.Write($"[MainWindow] ColorSquare drag started: {vm.DisplayName}");
        var dataObject = new DataObject();
        dataObject.Set("SessionViewModel", vm.Session.Id.ToString());
        await DragDrop.DoDragDrop(e, dataObject, global::Avalonia.Input.DragDropEffects.Move);
    }

    private void SessionList_DragOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains("SessionViewModel"))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void SessionList_Drop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains("SessionViewModel")) return;

        var draggedIdStr = e.Data.Get("SessionViewModel") as string;
        if (string.IsNullOrEmpty(draggedIdStr) || !Guid.TryParse(draggedIdStr, out var draggedId))
            return;

        var draggedVm = _sessions.FirstOrDefault(s => s.Session.Id == draggedId);
        if (draggedVm == null) return;

        int fromIndex = _sessions.IndexOf(draggedVm);
        if (fromIndex < 0) return;

        // Issue #225: a group moves as ONE unit - the pure GroupReorder.MoveBlock computes
        // the new order (whole group lifted, members keep internal order, never split or
        // land inside another group). GetSessionDropIndex maps the pixel to a raw insert
        // index from the live container geometry.
        var pos = e.GetPosition(SessionList);
        int rawTarget = GetSessionDropIndex(pos);

        var reordered = GroupReorder.MoveBlock(_sessions, vm => vm.GroupId, fromIndex, rawTarget);
        // Apply the new order onto the live ObservableCollection by stable position moves.
        for (int i = 0; i < reordered.Count; i++)
        {
            int cur = _sessions.IndexOf(reordered[i]);
            if (cur != i) _sessions.Move(cur, i);
        }

        FileLog.Write($"[MainWindow] SessionList_Drop: applied group-aware reorder, dragged {draggedVm.DisplayName}");
        SessionList.SelectedItem = draggedVm;
        RecomputeGroupPositions();
        PersistSessionState();
    }

    /// <summary>Recompute first/last group flags (issue #225) so the header + bracket reflow
    /// after any list change. Cheap; safe to call on every CollectionChanged.</summary>
    private void RecomputeGroupPositions()
    {
        for (int i = 0; i < _sessions.Count; i++)
        {
            var vm = _sessions[i];
            // Stamp the non-color role glyph from the local fleet on every list rebuild (same place
            // the group first/last flags are stamped) so the rail badge tracks controller changes.
            vm.ResolvedRole = _sessionManager.ResolveLocalRole(vm.Session);
            if (!vm.IsGroupMember) { vm.IsGroupFirst = false; vm.IsGroupLast = false; continue; }
            var gid = vm.GroupId;
            vm.IsGroupFirst = i == 0 || _sessions[i - 1].GroupId != gid;
            vm.IsGroupLast = i == _sessions.Count - 1 || _sessions[i + 1].GroupId != gid;
        }
    }

    private int GetSessionDropIndex(Point pos)
    {
        // Walk list items and find where the drop point falls
        for (int i = 0; i < _sessions.Count; i++)
        {
            var container = SessionList.ContainerFromIndex(i);
            if (container == null) continue;

            var itemPos = container.TranslatePoint(new Point(0, 0), SessionList);
            if (itemPos == null) continue;

            var bounds = container.Bounds;
            double itemTop = itemPos.Value.Y;
            double itemBottom = itemTop + bounds.Height;

            if (pos.Y >= itemTop && pos.Y <= itemBottom)
            {
                bool below = pos.Y > itemTop + bounds.Height / 2;
                return below ? i + 1 : i;
            }
        }

        return _sessions.Count;
    }

    private void BtnNewSession_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnNewSession_Click");
        _ = ShowNewSessionDialog();
    }

    private async Task ShowNewSessionDialog()
    {
        var app = (App)global::Avalonia.Application.Current!;
        var registry = app.RepositoryRegistry;

        var dialog = new NewSessionDialog(registry, app.SessionHistoryStore);
        var result = await dialog.ShowDialog<bool?>(this);

        if (result != true)
        {
            FileLog.Write("[MainWindow] ShowNewSessionDialog: cancelled");
            return;
        }

        // GitHub (Remote) tab: the work runs on a GitHub-hosted runner, not locally.
        if (dialog.RemoteConfig is { } remoteConfig)
        {
            await CreateRemoteSessionAsync(remoteConfig);
            return;
        }

        if (string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            FileLog.Write("[MainWindow] ShowNewSessionDialog: no path selected");
            return;
        }

        var resumeSessionId = dialog.SelectedResumeSessionId;
        var agentKind = dialog.SelectedAgentKind;

        // The selected configured agent entry (issue #490) is the source of truth for the launch:
        // its type/path/preset/model/args build the command line, replacing the legacy per-type
        // lookup. No enabled entry means there is nothing to launch.
        var selectedEntry = dialog.SelectedAgentEntry;
        if (selectedEntry is null)
        {
            FileLog.Write("[MainWindow] ShowNewSessionDialog: no agent entry selected (none configured); aborting");
            await MessageBox.ShowAsync(this,
                "No agent configured",
                "There are no enabled agents to launch.\n\nAdd one in Settings > Agents, then try again.");
            return;
        }

        // Per-entry preset/model/args resolve to the same effective command line the Tools/Agents
        // page previews (issue #436's shared resolver). For Claude the dialog's Bypass-permissions
        // checkbox still applies on top, because it is a per-session choice, not a stored preset.
        var entryArgs = selectedEntry.ToToolConfig().ResolveEffectiveCommandLineArguments().Trim();
        string? agentArgs;
        if (agentKind == AgentKind.ClaudeCode)
        {
            var claudeArgs = entryArgs;
            if (dialog.EnableRemoteControl)
                claudeArgs = $"remote-control {claudeArgs}".Trim();
            if (dialog.BypassPermissions)
                claudeArgs = $"{claudeArgs} {AgentToolCatalog.ClaudeSkipPermissionsArg}".Trim();
            agentArgs = claudeArgs.Length > 0 ? claudeArgs : null;
        }
        else if (agentKind == AgentKind.Cursor)
        {
            // Cursor's permission-bypass equivalent is --force (issue #517, AC9). The bypass
            // checkbox is a per-session opt-in on top of the entry's preset; if the preset
            // already carries --force (the "Automatic (yolo)" preset), don't add it twice.
            var cursorArgs = entryArgs;
            if (dialog.BypassPermissions
                && !cursorArgs.Contains(AgentToolCatalog.CursorForceArg, StringComparison.Ordinal))
            {
                cursorArgs = $"{cursorArgs} {AgentToolCatalog.CursorForceArg}".Trim();
            }
            agentArgs = cursorArgs.Length > 0 ? cursorArgs : null;
        }
        else if (agentKind == AgentKind.Copilot)
        {
            // Copilot's permission-bypass equivalent is --allow-all (issue #625, AC9). The bypass
            // checkbox is a per-session opt-in on top of the entry's preset; if the preset already
            // carries --allow-all (the "Automatic (yolo)" preset), don't add it twice.
            var copilotArgs = entryArgs;
            if (dialog.BypassPermissions
                && !copilotArgs.Contains(AgentToolCatalog.CopilotAllowAllArg, StringComparison.Ordinal))
            {
                copilotArgs = $"{copilotArgs} {AgentToolCatalog.CopilotAllowAllArg}".Trim();
            }
            agentArgs = copilotArgs.Length > 0 ? copilotArgs : null;
        }
        else
        {
            agentArgs = entryArgs.Length > 0 ? entryArgs : null;
        }

        // Build the IAgent from the entry. RawCli uses the entry's executable + its raw args
        // (the dialog seeds the Custom CLI boxes from the entry, so the user-edited boxes win).
        // Catalog agents (Claude/Pi/Codex/Gemini/OpenCode) read their executable path from a
        // per-launch options copy carrying THIS entry's ExecutablePath (issue #490) so two
        // entries of the same type with different paths each launch their own binary.
        IAgent agent;
        if (agentKind == AgentKind.RawCli)
        {
            var customCmd = dialog.SelectedCustomCommand;
            if (string.IsNullOrWhiteSpace(customCmd))
            {
                FileLog.Write("[MainWindow] ShowNewSessionDialog: RawCli selected but no command; aborting");
                return;
            }
            // RawCli carries its own command line on the agent; agentArgs is not reused for it.
            agentArgs = null;
            agent = new RawCliAgent(customCmd, string.IsNullOrWhiteSpace(dialog.SelectedCustomArgs) ? null : dialog.SelectedCustomArgs);
        }
        else
        {
            agent = CreateAgentForEntry(agentKind, selectedEntry.ExecutablePath);
        }

        FileLog.Write($"[MainWindow] ShowNewSessionDialog: path={dialog.SelectedPath}, agent={agentKind}, exe={agent.ExecutablePath}, resume={resumeSessionId ?? "null"}, bypassPermissions={dialog.BypassPermissions}, remoteControl={dialog.EnableRemoteControl}");

        // Preflight: make sure the chosen agent's CLI actually exists before we try to spawn it.
        // Without this, a missing binary makes CreateProcess fail with a cryptic Win32 error that
        // gets swallowed, so the dialog just closes and "nothing happens". Resolve it up front and
        // tell the user exactly what to fix. For RawCli, the exe is the user-supplied command.
        var agentExe = agent.ExecutablePath;
        if (ExecutableResolver.Resolve(agentExe) is null)
        {
            string errorTitle;
            string errorBody;
            if (agentKind == AgentKind.RawCli)
            {
                errorTitle = "Command not found";
                errorBody = $"Director could not find '{agentExe}' on PATH.\n\n"
                    + "Make sure the command is installed and on your PATH, or supply an absolute path.";
            }
            else
            {
                var (agentName, installHint) = AgentInstallInfo(agentKind);
                errorTitle = $"{agentName} is not installed";
                errorBody = $"Director could not start a {agentName} session because its command line tool "
                    + $"could not be found.\n\nLooked for: {agentExe}\n\n{installHint}\n\n"
                    + "If it is installed in a non-standard location, set its path in config.json.";
            }
            FileLog.Write($"[MainWindow] ShowNewSessionDialog: agent {agentKind} executable '{agentExe}' not found on PATH; aborting launch");
            await MessageBox.ShowAsync(this, errorTitle, errorBody);
            return;
        }

        var vm = CreateSession(dialog.SelectedPath, resumeSessionId, agentArgs, agent);
        if (vm == null)
        {
            FileLog.Write("[MainWindow] ShowNewSessionDialog: CreateSession returned null; showing failure dialog");
            await MessageBox.ShowAsync(this,
                "Could not start session",
                "Director could not start the session.\n\n"
                + (_lastSessionCreateError ?? "See the Director log for details."));
            return;
        }

        // Track last used time for repository sorting
        registry?.MarkUsed(dialog.SelectedPath);

        if (!string.IsNullOrEmpty(resumeSessionId))
        {
            FileLog.Write($"[MainWindow] ShowNewSessionDialog: resume path - looking up history for claude={resumeSessionId}");
            var historyEntry = app.SessionHistoryStore.FindByClaudeSessionId(resumeSessionId);
            if (historyEntry != null)
            {
                vm.Session.CustomName = historyEntry.CustomName;
                vm.Session.CustomColor = historyEntry.CustomColor;
                vm.Session.HistoryEntryId = historyEntry.Id;
                vm.NotifyDisplayChanged();
                historyEntry.LastUsedAt = DateTimeOffset.UtcNow;
                app.SessionHistoryStore.Save(historyEntry);
                FileLog.Write($"[MainWindow] ShowNewSessionDialog: resumed with history entry {historyEntry.Id}, name={historyEntry.CustomName}");
            }
            else
            {
                FileLog.Write("[MainWindow] ShowNewSessionDialog: no history entry found, showing rename dialog");
                ShowRenameDialog(vm);
                SaveSessionToHistory(vm);
            }
        }
        else if (dialog.SelectedNamedSession is { } named)
        {
            // Named session (issue #508): the name (and optional colour) were chosen when the item
            // was saved, so apply them directly and skip the rename prompt - launching is one click.
            FileLog.Write($"[MainWindow] ShowNewSessionDialog: named session launch - name={named.Name}, color={named.Color ?? "(none)"}");
            vm.Session.CustomName = named.Name;
            if (!string.IsNullOrWhiteSpace(named.Color))
                vm.Session.CustomColor = named.Color;
            vm.NotifyDisplayChanged();
            SaveSessionToHistory(vm);
            _ = CaptureStartupTextAsync(vm.Session);
        }
        else
        {
            // New session: show rename dialog, create history entry, capture startup text
            FileLog.Write("[MainWindow] ShowNewSessionDialog: new session - showing rename dialog");
            ShowRenameDialog(vm);
            SaveSessionToHistory(vm);
            _ = CaptureStartupTextAsync(vm.Session);
        }

        // If started from a handover, inject the handover prompt after session is ready
        if (!string.IsNullOrEmpty(dialog.SelectedHandoverPath))
        {
            _ = InjectHandoverPromptAsync(vm.Session, dialog.SelectedHandoverPath);
        }

        PersistSessionState();
        FileLog.Write("[MainWindow] ShowNewSessionDialog: complete");
    }

    // Builds the window menu bar (File / Session / View / Tools / Help). Rendered
    // in-window by the NativeMenuBar on Windows/Linux and lifted into the system
    // menu bar on macOS. Replaces the old scattered entry points (sidebar hamburger,
    // New Session caret, top-bar More/Settings/? cluster). Each leaf reuses the
    // existing click handlers / dialog logic so behavior is unchanged.
    private void BuildNativeMenu()
    {
        FileLog.Write("[MainWindow] BuildNativeMenu");

        NativeMenuItem Item(string header, Action onClick, KeyGesture? gesture = null)
        {
            var mi = new NativeMenuItem(header);
            mi.Click += (_, _) => onClick();
            if (gesture != null) mi.Gesture = gesture;
            return mi;
        }

        App AppRef() => global::Avalonia.Application.Current as App
            ?? throw new InvalidOperationException("Application.Current is not the CC Director App");

        // Ruthless default menu (2026-06-15): only items known to work are visible by
        // default. Anything experimental or not yet verified is gated behind alpha mode
        // (AlphaMode.IsEnabled) rather than deleted, so it stays recoverable and can be
        // un-gated once proven. The menu is rebuilt on AlphaMode.Changed.
        var alpha = AlphaMode.IsEnabled;
        var menu = new NativeMenu();

        // ===== File =====
        var file = new NativeMenuItem("File") { Menu = new NativeMenu() };
        file.Menu.Items.Add(Item("New Session", () => BtnNewSession_Click(this, new RoutedEventArgs()),
            new KeyGesture(Key.N, KeyModifiers.Control)));
        // Settings lives here so it is reachable from the Home (empty-state) page, where
        // the session toolbar (which also has a Settings button) is hidden.
        file.Menu.Items.Add(Item("Settings...", () => BtnSettings_Click(this, new RoutedEventArgs()),
            new KeyGesture(Key.OemComma, KeyModifiers.Control)));
        file.Menu.Items.Add(new NativeMenuItemSeparator());
        file.Menu.Items.Add(Item("Save Workspace...", async () =>
        {
            var app = AppRef();
            var sessionData = _sessions.Select(vm => new SessionData(
                vm.DisplayName, vm.Session.RepoPath, vm.Session.CustomName,
                vm.Session.CustomColor, vm.Session.ClaudeArgs));
            var dialog = new SaveWorkspaceDialog(app.WorkspaceStore, sessionData);
            await dialog.ShowDialog<bool?>(this);
        }));
        file.Menu.Items.Add(Item("Load Workspace...", async () =>
        {
            var dialog = new LoadWorkspaceDialog(AppRef().WorkspaceStore);
            var result = await dialog.ShowDialog<bool?>(this);
            if (result == true && dialog.SelectedWorkspace != null)
            {
                if (_sessions.Count > 0) await CloseAllSessionsAsync();
                await LoadWorkspaceAsync(dialog.SelectedWorkspace);
            }
        }));
        file.Menu.Items.Add(Item("Clear Workspace", async () =>
        {
            if (_sessions.Count == 0) return;
            await CloseAllSessionsAsync();
        }));
        file.Menu.Items.Add(new NativeMenuItemSeparator());
        file.Menu.Items.Add(Item("Open Logs", () =>
        {
            var logDir = Path.GetDirectoryName(FileLog.CurrentLogPath);
            if (logDir != null && Directory.Exists(logDir))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    { FileName = logDir, UseShellExecute = true });
        }));
        if (alpha)
        {
            // Debug/utility file openers - useful for development, noise for users.
            file.Menu.Items.Add(Item("Open Sessions File", () =>
            {
                var filePath = AppRef().SessionStateStore.FilePath;
                if (File.Exists(filePath))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(filePath)
                        { UseShellExecute = true });
                else
                    ShowNotification($"Sessions file not found: {filePath}");
            }));
            file.Menu.Items.Add(Item("Open History Folder", () =>
            {
                var folder = AppRef().SessionHistoryStore.FolderPath;
                if (Directory.Exists(folder))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        { FileName = folder, UseShellExecute = true });
                else
                    ShowNotification($"History folder not found: {folder}");
            }));
            file.Menu.Items.Add(Item("History in VS Code", () =>
            {
                var folder = AppRef().SessionHistoryStore.FolderPath;
                if (Directory.Exists(folder))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("code", $"\"{folder}\"")
                        { UseShellExecute = true });
                else
                    ShowNotification($"History folder not found: {folder}");
            }));
        }
        file.Menu.Items.Add(new NativeMenuItemSeparator());
        file.Menu.Items.Add(Item("Exit", Close));
        menu.Items.Add(file);

        // ===== Session =====
        var session = new NativeMenuItem("Session") { Menu = new NativeMenu() };
        session.Menu.Items.Add(Item("Repositories...", async () =>
        {
            FileLog.Write("[MainWindow] Menu: Repositories");
            var dialog = new RepositoryManagerDialog(AppRef().RootDirectoryStore);
            var result = await dialog.ShowDialog<bool?>(this);
            if (result == true && dialog.LaunchSessionPath != null)
            {
                var vm = CreateSession(dialog.LaunchSessionPath);
                if (vm != null)
                {
                    ShowRenameDialog(vm);
                    SaveSessionToHistory(vm);
                    SwitchLeftTab("Terminal");
                }
            }
        }));
        if (alpha)
        {
            session.Menu.Items.Add(new NativeMenuItemSeparator());
            session.Menu.Items.Add(Item("Start FIFO", () => BtnFifo_Click(this, new RoutedEventArgs())));
            session.Menu.Items.Add(Item("Accounts...", async () =>
            {
                FileLog.Write("[MainWindow] Menu: Accounts");
                var dialog = new AccountsDialog(AppRef().ClaudeAccountStore);
                await dialog.ShowDialog<bool?>(this);
            }));
            session.Menu.Items.Add(Item("Show Reviews", async () =>
            {
                FileLog.Write("[MainWindow] Menu: Show Reviews");
                var dialog = new TurnReviewDialog();
                await dialog.ShowDialog(this);
            }));
        }
        menu.Items.Add(session);

        // ===== View =====
        var view = new NativeMenuItem("View") { Menu = new NativeMenu() };
        view.Menu.Items.Add(Item("Status", ShowStatusView));
        view.Menu.Items.Add(new NativeMenuItemSeparator());
        view.Menu.Items.Add(Item("Toggle Right Panel", () => RightPanelToggle_Click(this, new RoutedEventArgs())));
        view.Menu.Items.Add(Item("Reset Terminal View", () => TabBarRefreshButton_Click(this, new RoutedEventArgs())));
        // TEMPORARY (Gateway Connection mission, Phase 1): a test entry into the new unified panel.
        // Removed in Phase 4 when the panel is embedded in Settings, the status box, and onboarding.
        view.Menu.Items.Add(new NativeMenuItemSeparator());
        view.Menu.Items.Add(Item("Gateway Connection (preview)...", OpenGatewayConnectionPreview));
        menu.Items.Add(view);

        // ===== Tools (alpha only - none of these are verified working yet) =====
        if (alpha)
        {
            var tools = new NativeMenuItem("Tools") { Menu = new NativeMenu() };
            // Communications, Connections (Browser Connections), and Scheduler are the three
            // v1-excluded overlays (issue 570, part of the #357 MVP cutdown). They are gated
            // behind the alpha flag explicitly here so they stay hidden in a default install
            // even if the broader Tools menu is later un-gated for v1. They open the
            // CommsOverlay / ConnectionsOverlay / SchedulerOverlay respectively.
            if (alpha)
            {
                tools.Menu.Items.Add(Item("Communications", () => BtnComms_Click(this, new RoutedEventArgs())));
                tools.Menu.Items.Add(Item("Connections", () => BtnConnections_Click(this, new RoutedEventArgs())));
                tools.Menu.Items.Add(Item("Scheduler", () => BtnScheduler_Click(this, new RoutedEventArgs())));
                tools.Menu.Items.Add(new NativeMenuItemSeparator());
            }
            tools.Menu.Items.Add(Item("Claude View...", () => BtnClaudeView_Click(this, new RoutedEventArgs())));
            tools.Menu.Items.Add(Item("MCP Servers...", () => BtnMcpServers_Click(this, new RoutedEventArgs())));
            tools.Menu.Items.Add(Item("Agent Templates...", () => BtnAgentTemplates_Click(this, new RoutedEventArgs())));
            tools.Menu.Items.Add(Item("Claude Code Settings...", () => BtnClaudeConfig_Click(this, new RoutedEventArgs())));
            tools.Menu.Items.Add(new NativeMenuItemSeparator());
            tools.Menu.Items.Add(Item("Transcription Component Preview...", () => BtnTranscriptionPreview_Click(this, new RoutedEventArgs())));
            menu.Items.Add(tools);
        }

        // ===== Help =====
        var help = new NativeMenuItem("Help") { Menu = new NativeMenu() };
        help.Menu.Items.Add(Item("Documentation", () =>
        {
            FileLog.Write("[MainWindow] Menu: Documentation -> https://devthrottle.com/docs");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://devthrottle.com/docs")
                { UseShellExecute = true });
        }));
        help.Menu.Items.Add(Item("Send Feedback...", () => BtnFeedback_Click(this, new RoutedEventArgs())));
        help.Menu.Items.Add(new NativeMenuItemSeparator());
        help.Menu.Items.Add(Item("About Director", () => BtnHelp_Click(this, new RoutedEventArgs())));
        menu.Items.Add(help);

        NativeMenu.SetMenu(this, menu);
    }

    // ==================== TOP APP BAR ====================

    private async void BtnFeedback_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnFeedback_Click: opening feedback dialog");
        var dialog = new FeedbackDialog(this);
        var result = await dialog.ShowDialog<bool?>(this);
        if (result == true)
            ShowNotification("Thank you. Your feedback has been submitted.");
    }

    private async void BtnTranscriptionPreview_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnTranscriptionPreview_Click: opening transcription component preview");
        try
        {
            var dialog = new global::CcDirector.Avalonia.Controls.TranscriptionComponentPreviewDialog();
            await dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnTranscriptionPreview_Click FAILED: {ex}");
        }
    }

    private async void BtnClaudeConfig_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnClaudeConfig_Click: opening Claude Code config dialog");
        var repoPath = _activeSession?.Session.RepoPath;
        var dialog = new ClaudeConfigDialog(repoPath);
        await dialog.ShowDialog<bool?>(this);
    }

    private async void BtnClaudeView_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnClaudeView_Click");
        var repoPath = _activeSession?.Session.RepoPath;
        var dialog = new ClaudeViewDialog(repoPath);
        await dialog.ShowDialog<bool?>(this);
    }

    private async void BtnMcpServers_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnMcpServers_Click");
        try
        {
            var manager = new McpConfigManager();
            var projectDir = _activeSession?.Session.RepoPath;
            var dialog = new McpServersDialog(manager, projectDir);
            await dialog.ShowDialog<bool?>(this);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnMcpServers_Click FAILED: {ex.Message}");
        }
    }

    private async void BtnAgentTemplates_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnAgentTemplates_Click");
        try
        {
            var store = new AgentTemplateStore();
            store.Load();
            var dialog = new AgentTemplatesDialog(store);
            dialog.LaunchRequested += (template, repoPath) =>
            {
                FileLog.Write($"[MainWindow] AgentTemplates LaunchRequested: template={template.Name}, repo={repoPath}");
                var args = template.BuildCliArgs();
                var vm = CreateSession(repoPath, claudeArgs: string.IsNullOrWhiteSpace(args) ? null : args);
                if (vm != null)
                    vm.Rename(template.Name, null);
            };
            await dialog.ShowDialog<bool?>(this);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnAgentTemplates_Click FAILED: {ex.Message}");
        }
    }

    private async void BtnSettings_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnSettings_Click: opening CC Director settings");
        await OpenSettingsAsync(onGatewayTab: false);
    }

    /// <summary>
    /// Open the CC Director Settings dialog. When <paramref name="onGatewayTab"/> is true the
    /// dialog is shown on the Gateway tab (issue #442: the no-Gateway needs-attention indicator
    /// routes here so the user lands on the field they must set).
    /// </summary>
    private async Task OpenSettingsAsync(bool onGatewayTab = false, bool onToolsTab = false)
    {
        FileLog.Write($"[MainWindow] OpenSettingsAsync: onGatewayTab={onGatewayTab}, onToolsTab={onToolsTab}");
        var dialog = new SettingsDialog(ReloadScreenshotsPanelAsync);
        if (onGatewayTab)
            dialog.SelectGatewayTab();
        else if (onToolsTab)
            dialog.SelectToolsTab();
        await dialog.ShowDialog<bool?>(this);

        // The Tools tab can download/repair tools while the dialog is open, so re-run the health
        // check on close - this clears the rail indicator once the toolset is whole again.
        _lastToolHealth = null;
        _ = RefreshToolHealthAsync(force: true);
    }

    private async void BtnHelp_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnHelp_Click");
        var dialog = new HelpDialog();
        await dialog.ShowDialog<bool?>(this);
    }

    // ==================== LEFT TAB SWITCHING ====================

    private string _activeLeftTab = "Terminal";
    private static readonly IBrush TransparentBrush = Brushes.Transparent;
    private static readonly IBrush InactiveTextBrush = new SolidColorBrush(Color.Parse("#888888"));

    private void TerminalTabButton_Click(object? sender, RoutedEventArgs e)
    {
        SwitchLeftTab("Terminal");
    }

    private void SourceControlTabButton_Click(object? sender, RoutedEventArgs e)
    {
        SwitchLeftTab("SourceControl");
    }

    // Per-session subscriptions so the needs-you count updates the instant any session's triage
    // verdict moves, not just on the 15s timer. Keyed by VM so we can unsubscribe on remove.
    //
    // This listens to the VIEW-MODEL's NeedsYou property, NOT to the raw Session.OnStatusColorChanged
    // event. The count is folded from hold + dictation + activity + overlays, and only ONE of those
    // raises OnStatusColorChanged - so hooking that event alone left the header stale until the 15s
    // git timer happened to fire. Snoozing a red session visibly left "1 need you" above a grey
    // "Snoozed" row for up to fifteen seconds. SessionViewModel raises NeedsYou from every handler
    // that can move the verdict, so subscribing to the property is what makes the count prompt.
    private readonly Dictionary<SessionViewModel, global::System.ComponentModel.PropertyChangedEventHandler> _needsYouHandlers = new();

    private void OnSessionsCollectionChanged(object? sender, global::System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == global::System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            foreach (var kv in _needsYouHandlers) kv.Key.PropertyChanged -= kv.Value;
            _needsYouHandlers.Clear();
            foreach (var vm in _sessions) SubscribeNeedsYou(vm);
        }
        else
        {
            if (e.OldItems != null)
                foreach (SessionViewModel vm in e.OldItems)
                    if (_needsYouHandlers.TryGetValue(vm, out var h)) { vm.PropertyChanged -= h; _needsYouHandlers.Remove(vm); }
            if (e.NewItems != null)
                foreach (SessionViewModel vm in e.NewItems) SubscribeNeedsYou(vm);
        }
        UpdateNeedsYouCount();
        // The home page is the zero-sessions screen: show/hide it as the count crosses zero.
        UpdateHomeVisibility();
    }

    private void SubscribeNeedsYou(SessionViewModel vm)
    {
        if (_needsYouHandlers.ContainsKey(vm)) return;
        global::System.ComponentModel.PropertyChangedEventHandler h = (_, args) =>
        {
            if (args.PropertyName is not (null or nameof(SessionViewModel.NeedsYou))) return;
            Dispatcher.UIThread.Post(UpdateNeedsYouCount);
        };
        _needsYouHandlers[vm] = h;
        vm.PropertyChanged += h;
    }

    // Count of sessions that need you, shown beside the SESSIONS header, so you get a top-level
    // "is anything waiting on me?" signal without scanning the list. Hidden at zero.
    //
    // Counts the SHARED FOLD's triage verdict (SessionViewModel.NeedsYou -> SessionOrdering.Classify),
    // which is the same rule the phone's web-push badge counts by (WebPushNeedsYouNotifier) - so the
    // header and the phone cannot disagree about how many sessions want you.
    //
    // This used to count the RAW cooked colour, `s.Session.StatusColor == "red"`, with no hold check,
    // no role, and no overlays. A snoozed session is still genuinely at a turn end, so its raw colour
    // stays "red" - which is why a session that rendered a grey dot labelled "Snoozed" was counted
    // under a header reading "1 need you". Do not reach past the fold to the raw colour again.
    private void UpdateNeedsYouCount()
    {
        var n = _sessions.Count(s => s.NeedsYou);
        SessionsNeedYouText.Text = n > 0 ? $"{n} need you" : "";
        SessionsNeedYouText.IsVisible = n > 0;
    }

    private bool _commsInitialized;

    // Launch the full-screen FIFO takeover: step through every session that needs the
    // user, one at a time, with the live terminal + wingman briefing. Modal over the main
    // window so there is nothing else to look at while stepping through.
    private async void BtnFifo_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var sm = (global::Avalonia.Application.Current as App)?.SessionManager;
            if (sm is null)
            {
                FileLog.Write("[MainWindow] BtnFifo_Click: SessionManager not available");
                return;
            }
            FileLog.Write("[MainWindow] BtnFifo_Click: opening FIFO window");
            await new FifoWindow(sm).ShowDialog(this);

            // The FIFO window is full-screen, so attaching a session there resized that
            // session's PTY to full-screen dimensions. Re-attach the main window's active
            // session so it re-sends ITS dimensions and redraws cleanly, instead of leaving
            // the session rendering at the FIFO window's size.
            if (_activeSession is not null)
                TerminalHost.Attach(_activeSession.Session);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnFifo_Click FAILED: {ex.Message}");
        }
    }

    private async void BtnComms_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnComms_Click: opening Comms overlay");

        // Close other overlays first
        if (ConnectionsOverlay.IsVisible)
        {
            ConnectionsOverlay.IsVisible = false;
            if (_connectionsInitialized)
                ConnectionsView.StopPolling();
        }
        if (SchedulerOverlay.IsVisible)
        {
            SchedulerOverlay.IsVisible = false;
            if (_schedulerInitialized)
                SchedulerView.StopPolling();
        }

        CommsOverlay.IsVisible = true;
        UpdateHomeVisibility(); // hide Home so the overlay is not buried behind it (#447)

        if (!_commsInitialized)
        {
            _commsInitialized = true;
            await CommManagerView.InitializeAsync();
        }
        CommManagerView.StartPolling();
    }

    private void BtnCommsClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnCommsClose_Click: closing Comms overlay");
        CommsOverlay.IsVisible = false;
        if (_commsInitialized)
            CommManagerView.StopPolling();
        UpdateHomeVisibility(); // restore Home if still at zero sessions (#447)
    }

    private bool _connectionsInitialized;
    private bool _schedulerInitialized;

    private void BtnConnections_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnConnections_Click: opening Connections overlay");

        // Close other overlays first
        if (CommsOverlay.IsVisible)
        {
            CommsOverlay.IsVisible = false;
            if (_commsInitialized)
                CommManagerView.StopPolling();
        }
        if (SchedulerOverlay.IsVisible)
        {
            SchedulerOverlay.IsVisible = false;
            if (_schedulerInitialized)
                SchedulerView.StopPolling();
        }

        ConnectionsOverlay.IsVisible = true;
        _connectionsInitialized = true;
        ConnectionsView.StartPolling();
        UpdateHomeVisibility(); // hide Home so the overlay is not buried behind it (#447)
    }

    private void BtnConnectionsClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnConnectionsClose_Click: closing Connections overlay");
        ConnectionsOverlay.IsVisible = false;
        if (_connectionsInitialized)
            ConnectionsView.StopPolling();
        UpdateHomeVisibility(); // restore Home if still at zero sessions (#447)
    }

    private void BtnScheduler_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnScheduler_Click: opening Scheduler overlay");

        if (CommsOverlay.IsVisible)
        {
            CommsOverlay.IsVisible = false;
            if (_commsInitialized)
                CommManagerView.StopPolling();
        }
        if (ConnectionsOverlay.IsVisible)
        {
            ConnectionsOverlay.IsVisible = false;
            if (_connectionsInitialized)
                ConnectionsView.StopPolling();
        }

        SchedulerOverlay.IsVisible = true;
        _schedulerInitialized = true;
        SchedulerView.StartPolling();
        UpdateHomeVisibility(); // hide Home so the overlay is not buried behind it (#447)
    }

    private void BtnSchedulerClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnSchedulerClose_Click: closing Scheduler overlay");
        SchedulerOverlay.IsVisible = false;
        if (_schedulerInitialized)
            SchedulerView.StopPolling();
        UpdateHomeVisibility(); // restore Home if still at zero sessions (#447)
    }

    private void SwitchLeftTab(string tab)
    {
        if (_activeLeftTab == tab) return;
        _activeLeftTab = tab;
        FileLog.Write($"[MainWindow] SwitchLeftTab: {tab}");

        var accentBrush = (IBrush)(this.FindResource("AccentBrush") ?? Brushes.DodgerBlue);
        var whiteBrush = Brushes.White;
        bool isDocTab = tab.StartsWith("Doc:", StringComparison.Ordinal);

        // Update fixed tab button styles
        TerminalTabButton.Background = tab == "Terminal" ? accentBrush : TransparentBrush;
        TerminalTabButton.Foreground = tab == "Terminal" ? whiteBrush : InactiveTextBrush;
        SourceControlTabButton.Background = tab == "SourceControl" ? accentBrush : TransparentBrush;
        SourceControlTabButton.Foreground = tab == "SourceControl" ? whiteBrush : InactiveTextBrush;
        // Update document tab button styles
        foreach (var docTab in _documentTabs)
        {
            bool isActive = isDocTab && docTab.TabId == tab;
            docTab.TabButton.Background = isActive ? accentBrush : TransparentBrush;
            docTab.TabButton.Foreground = isActive ? whiteBrush : InactiveTextBrush;
        }

        // Show/hide panels
        TerminalPanel.IsVisible = tab == "Terminal";
        SourceControlPanel.IsVisible = tab == "SourceControl";
        DocumentPanel.IsVisible = isDocTab;

        // The shared prompt bar belongs to the terminal-style tabs.
        if (_activeSession != null)
            PromptBarBorder.IsVisible = true;

        // Show refresh button only when Terminal tab is active and a session exists
        TabBarRefreshButton.IsVisible = tab == "Terminal" && _activeSession != null;
        TabBarCaptureButton.IsVisible = tab == "Terminal" && _activeSession != null;

        // Swap document panel content
        if (isDocTab)
        {
            DocumentPanel.Children.Clear();
            var activeDocTab = _documentTabs.FirstOrDefault(d => d.TabId == tab);
            if (activeDocTab != null)
                DocumentPanel.Children.Add(activeDocTab.ViewerControl);
        }

        // Force terminal refresh when switching back to Terminal tab.
        // The terminal display corrupts while hidden (Bounds=0) and needs
        // a full buffer re-parse + ConPTY resize to render correctly.
        if (tab == "Terminal" && _activeSession != null)
        {
            Dispatcher.UIThread.Post(() => TerminalHost.ForceRefresh(), DispatcherPriority.Render);
        }
    }

    private void UpdateSourceControlTabVisibility(string repoPath)
    {
        var gitDir = Path.Combine(repoPath, ".git");
        var hasGit = Directory.Exists(gitDir) || File.Exists(gitDir);
        SourceControlTabButton.IsVisible = hasGit;

        // If Source Control tab was selected but is now hidden, switch to Terminal
        if (!hasGit && _activeLeftTab == "SourceControl")
            SwitchLeftTab("Terminal");

        FileLog.Write($"[MainWindow] UpdateSourceControlTabVisibility: hasGit={hasGit}");
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

        // Ctrl+Enter = Send prompt (Enter inserts newline via AcceptsReturn)
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            SendPrompt();
            return;
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
        var agentKind = _activeSession?.Session.AgentKind ?? AgentKind.ClaudeCode;
        var available = _slashCommandProvider.GetCommands(agentKind, repoPath);

        _filteredSlashCommands = string.IsNullOrEmpty(filter)
            ? available
            : available.Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

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
        SlashCommandDocSource.Text = selected.Source switch
        {
            "project" => "Project",
            "global" => "Global",
            "builtin" => selected.DriverKind?.ToString() ?? "Built in",
            _ => selected.Source
        };
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

    private static readonly HashSet<string> TerminalOnlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "context", "copy", "diff", "rewind", "checkpoint", "export", "mcp", "agents",
    };

    private bool TryHandleSlashCommand(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.StartsWith("/"))
            return false;

        if (_activeSession?.Session.AgentKind != AgentKind.ClaudeCode)
            return false;

        var commandName = text.ToLowerInvariant().TrimStart('/');

        // Terminal-only commands: show redirect message, keep text in prompt
        if (TerminalOnlyCommands.Contains(commandName))
        {
            FileLog.Write($"[MainWindow] TryHandleSlashCommand: terminal-only command blocked: /{commandName}");
            ShowNotification($"Use the Terminal tab for {text}");
            PromptInput.Text = text;
            PromptInput.CaretIndex = text.Length;
            return true;
        }

        // Commands handled by ClaudeConfigDialog (with tab selection)
        var configTab = commandName switch
        {
            "config" or "settings" => "general",
            "permissions" or "allowed-tools" => "permissions",
            "model" => "general",
            "hooks" => "hooks",
            "plugin" => "plugins",
            _ => (string?)null
        };

        if (configTab != null)
        {
            FileLog.Write($"[MainWindow] TryHandleSlashCommand: opening ClaudeConfigDialog tab={configTab} for /{commandName}");
            var dialog = new ClaudeConfigDialog(_activeSession?.Session.RepoPath, configTab);
            _ = dialog.ShowDialog<bool?>(this);
            return true;
        }

        // Commands with their own native dialogs
        switch (commandName)
        {
            case "status":
                FileLog.Write("[MainWindow] TryHandleSlashCommand: opening StatusDialog");
                _ = new StatusDialog().ShowDialog<bool?>(this);
                return true;

            case "help":
                FileLog.Write("[MainWindow] TryHandleSlashCommand: opening HelpDialog");
                _ = new HelpDialog().ShowDialog<bool?>(this);
                return true;

            case "theme":
                FileLog.Write("[MainWindow] TryHandleSlashCommand: opening ThemeDialog");
                _ = new ThemeDialog().ShowDialog<bool?>(this);
                return true;

            case "memory":
                FileLog.Write("[MainWindow] TryHandleSlashCommand: opening MemoryDialog");
                _ = new MemoryDialog(_activeSession?.Session.RepoPath).ShowDialog<bool?>(this);
                return true;

            case "stats":
                FileLog.Write("[MainWindow] TryHandleSlashCommand: opening StatsDialog");
                _ = new StatsDialog().ShowDialog<bool?>(this);
                return true;

            case "output-style":
                FileLog.Write("[MainWindow] TryHandleSlashCommand: opening OutputStyleDialog");
                _ = new OutputStyleDialog().ShowDialog<bool?>(this);
                return true;

            case "resume" or "continue":
                FileLog.Write("[MainWindow] TryHandleSlashCommand: opening ResumeDialog");
                _ = HandleResumeCommand();
                return true;
        }

        return false;
    }

    private async Task HandleResumeCommand()
    {
        var dialog = new ResumeDialog(_activeSession?.Session.RepoPath);
        var result = await dialog.ShowDialog<bool?>(this);
        if (result == true && dialog.SelectedSessionId != null)
        {
            SwitchLeftTab("Terminal");
            PromptInput.Text = $"claude --resume {dialog.SelectedSessionId}";
            ShowNotification("Session selected -- press Enter to resume in Terminal");
        }
    }

    private async void SendPrompt()
    {
        if (_activeSession == null || string.IsNullOrWhiteSpace(PromptInput.Text)) return;
        // Locked out while the session transcribes a dictated utterance in the background, so a
        // typed Send cannot race the incoming dictated prompt (guards the button AND Ctrl+Enter).
        if (IsActiveSessionTranscribing())
        {
            FileLog.Write("[MainWindow] SendPrompt ignored: session transcribing");
            return;
        }

        // Strip newlines -- Claude Code prompt expects single-line input
        var text = PromptInput.Text.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrEmpty(text)) return;

        // Intercept slash commands and show native dialogs
        if (TryHandleSlashCommand(text))
        {
            PromptInput.Text = "";
            return;
        }

        FileLog.Write($"[MainWindow] SendPrompt: {text.Length} chars to session {_activeSession.Session.Id}");

        PromptInput.Text = "";

        // Clear saved prompt text so switching away and back shows empty box
        _activeSession.Session.PendingPromptText = string.Empty;

        // Snapshot the JSONL before sending so we can rewind to this point
        _activeSession.Session.InitializeHistory();
        _activeSession.Session.History?.TakeSnapshot();

        // Notify user when large input is redirected to a temp file. Name the
        // active session's actual agent, not a hardcoded "Claude Code".
        if (CcDirector.Core.Input.LargeInputHandler.IsLargeInput(text))
        {
            var agentName = AgentPluginRegistry.Get(_activeSession.Session.AgentKind).DisplayName;
            ShowNotification(CcDirector.Core.Input.LargeInputHandler.FormatRedirectNotice(agentName, text.Length));
        }
        else
        {
            ClearNotification();
        }

        // Check if this is an interactive TUI command
        var isInteractiveCommand = _activeSession.Session.AgentKind == AgentKind.ClaudeCode
            && text.StartsWith("/")
            && InteractiveTuiCommands.Contains(text.TrimStart('/'));

        // Backends send Enter (CR/LF) explicitly after the text -- don't append a submit
        // newline here. Appending one used to trip LargeInputHandler's multi-line check
        // and route short single-line prompts through a temp file.
        // DevThrottle Stats: the desktop composer is typed input from the local machine, by construction.
        await _activeSession.Session.SendTextAsync(text, origin: InputOrigin.DesktopTyped);

        if (isInteractiveCommand)
        {
            EnterInteractiveTuiMode(_activeSession.Session);
        }
        else
        {
            PromptInput.Focus();
        }
    }

    // ==================== INTERACTIVE TUI MODE ====================

    /// <summary>
    /// Enters interactive TUI mode: focuses the terminal so keystrokes go directly
    /// to the ConPTY process instead of PromptInput. Auto-exits when TUI closes.
    /// </summary>
    private void EnterInteractiveTuiMode(Session session)
    {
        FileLog.Write("[MainWindow] EnterInteractiveTuiMode: focusing terminal for interactive TUI");
        _isInteractiveTuiMode = true;

        // Focus the terminal so keystrokes go to the ConPTY process
        SwitchLeftTab("Terminal");
        Dispatcher.UIThread.Post(() => TerminalHost.Focus());

        ShowNotification("Interactive mode -- keys go to terminal. Click prompt input to exit.");

        // Auto-exit when the session transitions back to idle (TUI closed)
        void OnStateChanged(ActivityState oldState, ActivityState newState)
        {
            if (newState is ActivityState.Idle or ActivityState.WaitingForInput)
            {
                session.OnActivityStateChanged -= OnStateChanged;
                Dispatcher.UIThread.Post(ExitInteractiveTuiMode);
            }
        }

        session.OnActivityStateChanged += OnStateChanged;
    }

    private void ExitInteractiveTuiMode()
    {
        if (!_isInteractiveTuiMode) return;

        FileLog.Write("[MainWindow] ExitInteractiveTuiMode: returning focus to PromptInput");
        _isInteractiveTuiMode = false;
        ClearNotification();
        PromptInput.Focus();
    }

    private void PromptInput_GotFocus(object? sender, GotFocusEventArgs e)
    {
        // Exit interactive TUI mode if the user clicks back to the prompt input
        if (_isInteractiveTuiMode)
            ExitInteractiveTuiMode();
    }

    private void PromptInput_DragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Text) || e.Data.Contains(DataFormats.Files))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void PromptInput_Drop(object? sender, DragEventArgs e)
    {
        string? path = null;

        if (e.Data.Contains(DataFormats.Text))
        {
            path = e.Data.GetText();
        }
        else if (e.Data.Contains(DataFormats.Files))
        {
            var files = e.Data.GetFiles();
            if (files != null)
            {
                var first = files.FirstOrDefault();
                if (first != null)
                    path = first.Path.LocalPath;
            }
        }

        if (!string.IsNullOrEmpty(path))
        {
            FileLog.Write($"[MainWindow] PromptInput_Drop: inserting path={path}");
            var insertion = path + "\n";
            var idx = PromptInput.CaretIndex;
            var text = PromptInput.Text ?? "";
            PromptInput.Text = text.Insert(idx, insertion);
            PromptInput.CaretIndex = idx + insertion.Length;
            PromptInput.Focus();
        }

        e.Handled = true;
    }

    private void BtnQueuePrompt_Click(object? sender, RoutedEventArgs e)
    {
        QueueCurrentPrompt();
    }

    private async void PromptExpand_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] PromptExpand_Click");
        try
        {
            var dialog = new ExpandedEditorDialog("Edit prompt", PromptInput.Text ?? "");
            var applied = await dialog.ShowDialog<bool?>(this);
            if (applied == true)
            {
                PromptInput.Text = dialog.EditedText;
                PromptInput.CaretIndex = PromptInput.Text.Length;
                PromptInput.Focus();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] PromptExpand_Click FAILED: {ex.Message}");
        }
    }

    private async void QueuePreview_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] QueuePreview_Click");
        try
        {
            if (sender is not Control control || control.DataContext is not SessionViewModel vm)
            {
                FileLog.Write("[MainWindow] QueuePreview_Click: no session view model");
                return;
            }

            var queue = vm.Session.PromptQueue;
            if (queue == null || queue.Count == 0)
                return;

            var dialog = new ExpandedEditorDialog($"Queue - {vm.DisplayName}", queue);
            await dialog.ShowDialog<bool?>(this);

            // Edits mutate the queue in memory; persist and refresh the visible panel.
            PersistSessionState();
            if (_activeSession?.Session.Id == vm.Session.Id)
                RefreshQueuePanel();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] QueuePreview_Click FAILED: {ex.Message}");
        }
    }

    private void QueueCurrentPrompt()
    {
        if (_activeSession == null || string.IsNullOrWhiteSpace(PromptInput.Text))
            return;
        // Locked out while the session transcribes a dictated utterance in the background (guards the
        // Queue button AND Ctrl+Shift+Enter).
        if (IsActiveSessionTranscribing())
        {
            FileLog.Write("[MainWindow] QueueCurrentPrompt ignored: session transcribing");
            return;
        }

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

    private async void BtnHandover_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnHandover_Click");
        if (_activeSession == null)
        {
            FileLog.Write("[MainWindow] BtnHandover_Click: no active session");
            return;
        }

        await _activeSession.Session.SendTextAsync("/handover", SendSource.Internal);
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
        // A plain notification never carries a progress bar; hide any left over from a download.
        HideDownloadProgress();
        NotificationBar.IsVisible = true;
    }

    private void ClearNotification()
    {
        NotificationText.Text = string.Empty;
        NotificationIcon.IsVisible = false;
        HideDownloadProgress();
        NotificationBar.IsVisible = false;
    }

    // ==================== PASTE-REMINDER CARD ====================

    private DispatcherTimer? _pasteCardTimer;
    private const int PasteCardAutoHideSeconds = 15;

    /// <summary>
    /// Shows the floating bottom-right card (e.g. "Browser opened -- press Ctrl+V").
    /// Unlike the thin notification bar, this is meant to still be on screen when
    /// the user comes back from the browser. Auto-hides after
    /// <see cref="PasteCardAutoHideSeconds"/>; the X dismisses it immediately.
    /// </summary>
    private void ShowPasteCard(string title, string body)
    {
        FileLog.Write($"[MainWindow] ShowPasteCard: {title}");
        PasteCardTitle.Text = title;
        PasteCardBody.Text = body;
        PasteCard.IsVisible = true;

        _pasteCardTimer?.Stop();
        _pasteCardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(PasteCardAutoHideSeconds) };
        _pasteCardTimer.Tick += (_, _) =>
        {
            _pasteCardTimer?.Stop();
            PasteCard.IsVisible = false;
        };
        _pasteCardTimer.Start();
    }

    private void PasteCardClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] PasteCardClose_Click");
        _pasteCardTimer?.Stop();
        PasteCard.IsVisible = false;
    }

    // ==================== AUTO-UPDATE NOTICE ====================

    /// <summary>True once a verified build is staged, so the sidebar indicator stays green.</summary>
    private bool _updateStaged;

    // Icons reused from the Gateway indicator: a ring while busy, a check when ready,
    // a cross on failure.
    private const string UpdateIconRing = GatewayIconRing;
    private const string UpdateIconCheck = GatewayIconCheck;
    private const string UpdateIconCross = GatewayIconCross;

    /// <summary>
    /// Passively note that an update has been downloaded. It installs
    /// automatically the next time CC Director is launched -- the running app is
    /// never interrupted, so no active sessions are lost. Called by App after
    /// UpdateService stages a verified build (marshalled to the UI thread).
    /// </summary>
    public void ShowUpdateReady(string version)
    {
        FileLog.Write($"[MainWindow] ShowUpdateReady: {version}");
        ShowNotification($"Director {version} downloaded -- installs next time you open the app.");
    }

    /// <summary>
    /// Drive the sidebar update indicator and the notification-bar progress bar from
    /// UpdateService phase/byte events (already marshalled to the UI thread by App).
    /// Makes the otherwise-silent check + download visible.
    /// </summary>
    public void OnUpdateProgress(CcDirector.Core.Update.UpdateProgress p)
    {
        try
        {
            switch (p.Phase)
            {
                case CcDirector.Core.Update.UpdatePhase.Checking:
                    SetUpdateIndicator(UpdateIconRing, "#3B82F6", "#1B2A3A", "#3B82F6",
                        "CHECKING FOR UPDATES", "contacting GitHub...");
                    HideDownloadProgress();
                    break;

                case CcDirector.Core.Update.UpdatePhase.Downloading:
                    var pct = p.Fraction is { } f ? (int)Math.Round(f * 100) : 0;
                    SetUpdateIndicator(UpdateIconRing, "#3B82F6", "#1B2A3A", "#3B82F6",
                        "DOWNLOADING UPDATE",
                        p.Fraction is null ? $"{p.Version}" : $"{p.Version} - {pct}%");
                    ShowDownloadProgress(p, pct);
                    break;

                case CcDirector.Core.Update.UpdatePhase.Verifying:
                    SetUpdateIndicator(UpdateIconRing, "#3B82F6", "#1B2A3A", "#3B82F6",
                        "VERIFYING UPDATE", $"{p.Version} - checking integrity");
                    NotificationProgress.IsVisible = true;
                    NotificationProgress.IsIndeterminate = false;
                    NotificationProgress.Value = 100;
                    NotificationProgressMeta.IsVisible = true;
                    NotificationProgressMeta.Text = "verifying...";
                    break;

                case CcDirector.Core.Update.UpdatePhase.Staged:
                    _updateStaged = true;
                    SetUpdateIndicator(UpdateIconCheck, "#22C55E", "#1B3A2A", "#22C55E",
                        "UPDATE READY", $"{p.Version} - installs on restart",
                        "Restarting Director will install this update.");
                    HideDownloadProgress();
                    break;

                case CcDirector.Core.Update.UpdatePhase.UpToDate:
                    // Nothing to do: keep the indicator hidden unless an update is already staged.
                    if (!_updateStaged) UpdateIndicator.IsVisible = false;
                    HideDownloadProgress();
                    break;

                case CcDirector.Core.Update.UpdatePhase.Failed:
                    SetUpdateIndicator(UpdateIconCross, "#F59E0B", "#3A2A1B", "#F59E0B",
                        "UPDATE CHECK FAILED", "click to retry");
                    HideDownloadProgress();
                    break;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OnUpdateProgress FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Update the sidebar indicator. When <paramref name="hint"/> is null the indicator is
    /// clickable to run a manual check now; pass a hint to make it purely informational (used
    /// once an update is staged, where there is nothing left to check for).
    /// </summary>
    private void SetUpdateIndicator(string icon, string accent, string bg, string border, string label, string sub, string? hint = null)
    {
        UpdateIndicatorIcon.Data = Geometry.Parse(icon);
        UpdateIndicatorIcon.Fill = Brush.Parse(accent);
        UpdateIndicator.Background = Brush.Parse(bg);
        UpdateIndicator.BorderBrush = Brush.Parse(border);
        UpdateIndicatorLabel.Text = label;
        UpdateIndicatorLabel.Foreground = Brush.Parse(accent);
        UpdateIndicatorSub.Text = sub;
        ToolTip.SetTip(UpdateIndicator, $"{label}\n{sub}\n{hint ?? "Click to check for updates now."}");
        // A staged update is informational only (hint set), so drop the clickable hand cursor.
        UpdateIndicator.Cursor = new Cursor(hint is null ? StandardCursorType.Hand : StandardCursorType.Arrow);
        UpdateIndicator.IsVisible = true;
    }

    private void ShowDownloadProgress(CcDirector.Core.Update.UpdateProgress p, int pct)
    {
        NotificationIcon.IsVisible = false;
        NotificationText.Text = $"Downloading Director {p.Version}...";
        NotificationProgress.IsVisible = true;
        if (p.Fraction is not null)
        {
            NotificationProgress.IsIndeterminate = false;
            NotificationProgress.Value = pct;
            NotificationProgressMeta.Text = $"{pct}%   {FormatMb(p.Downloaded)} / {FormatMb(p.Total)}";
        }
        else
        {
            NotificationProgress.IsIndeterminate = true;
            NotificationProgressMeta.Text = $"{FormatMb(p.Downloaded)} downloaded";
        }
        NotificationProgressMeta.IsVisible = true;
        NotificationBar.IsVisible = true;
    }

    private void HideDownloadProgress()
    {
        NotificationProgress.IsVisible = false;
        NotificationProgress.IsIndeterminate = false;
        NotificationProgressMeta.IsVisible = false;
    }

    private static string FormatMb(long bytes) => $"{bytes / 1048576.0:0.0} MB";

    /// <summary>Click the sidebar indicator to run a check now (off the UI thread).</summary>
    private void UpdateIndicator_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            // Once an update is staged there is nothing left to check for -- the indicator is
            // purely informational and a click must not kick off another check.
            if (_updateStaged)
            {
                FileLog.Write("[MainWindow] UpdateIndicator clicked while update staged - ignoring (informational only)");
                return;
            }
            var updater = (global::Avalonia.Application.Current as App)?.Updater;
            if (updater is null)
            {
                FileLog.Write("[MainWindow] UpdateIndicator_PointerPressed: no updater available");
                return;
            }
            FileLog.Write("[MainWindow] UpdateIndicator clicked - manual update check");
            _ = Task.Run(() => updater.CheckAndStageAsync());
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] UpdateIndicator_PointerPressed FAILED: {ex.Message}");
        }
    }

    // ==================== RIGHT PANEL TOGGLE ====================

    private void RightPanelToggle_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] RightPanelToggle_Click");
        _rightPanelExpanded = !_rightPanelExpanded;

        // Full panel and the slim strip share the Auto column; swap which one is visible.
        // The collapse chevron lives in the panel header; the expand chevron in the strip.
        RightPanel.IsVisible = _rightPanelExpanded;
        RightPanelCollapsedStrip.IsVisible = !_rightPanelExpanded;
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

    private async void QueueItemEdit_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Guid itemId)
            return;

        FileLog.Write($"[MainWindow] QueueItemEdit_Click: {itemId}");
        try
        {
            var queue = _activeSession?.Session.PromptQueue;
            if (queue == null || queue.Count == 0)
                return;

            var title = _activeSession != null ? $"Queue - {_activeSession.DisplayName}" : "Queue";
            var dialog = new ExpandedEditorDialog(title, queue, itemId);
            await dialog.ShowDialog<bool?>(this);

            // Edits mutate the queue in memory; persist and refresh the visible panel.
            PersistSessionState();
            RefreshQueuePanel();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] QueueItemEdit_Click FAILED: {ex.Message}");
        }
    }

    private void QueueItemMoveUp_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Guid itemId)
            return;

        FileLog.Write($"[MainWindow] QueueItemMoveUp_Click: {itemId}");
        _activeSession?.Session.PromptQueue?.MoveUp(itemId);
        RefreshQueuePanel();
    }

    private void QueueItemMoveDown_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Guid itemId)
            return;

        FileLog.Write($"[MainWindow] QueueItemMoveDown_Click: {itemId}");
        _activeSession?.Session.PromptQueue?.MoveDown(itemId);
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

    private void QueueItemsList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_activeSession == null) return;

        var selected = QueueItemsList.SelectedItem as QueueItemViewModel;
        if (selected == null) return;

        FileLog.Write($"[MainWindow] QueueItemsList_DoubleTapped: {selected.Id}");

        // Pop item and insert into prompt input
        PromptInput.Text = (PromptInput.Text ?? "") + selected.FullText;
        PromptInput.CaretIndex = PromptInput.Text.Length;
        _activeSession.Session.PromptQueue?.Remove(selected.Id);
        RefreshQueuePanel();
        PromptInput.Focus();
    }

    // ==================== SCREENSHOTS ====================

    /// <summary>
    /// Re-point the screenshots tab after the configured folder changes (Settings save), so
    /// the new folder takes effect without restarting the app. Idempotent - safe to call
    /// repeatedly; it tears down the previous watcher first.
    /// </summary>
    public Task ReloadScreenshotsPanelAsync() => InitializeScreenshotsPanelAsync();

    private async Task InitializeScreenshotsPanelAsync()
    {
        FileLog.Write("[MainWindow] InitializeScreenshotsPanelAsync: starting");

        try
        {
            // Idempotent: tear down any previous watcher/timer and clear the list so a reload
            // after a folder change doesn't double-watch or stack stale thumbnails.
            if (_screenshotWatcher is not null)
            {
                _screenshotWatcher.EnableRaisingEvents = false;
                _screenshotWatcher.Created -= OnScreenshotFileChanged;
                _screenshotWatcher.Deleted -= OnScreenshotFileChanged;
                _screenshotWatcher.Renamed -= OnScreenshotFileChanged;
                _screenshotWatcher.Dispose();
                _screenshotWatcher = null;
            }
            _screenshotDebounceTimer?.Stop();
            _screenshotDebounceTimer = null;
            _screenshots.Clear();

            // Single source of truth: the same resolver the phone-upload endpoint writes to
            // (CcStorage.Screenshots()), so the tab always watches where images actually land.
            // It honors the configured folder, falls back to the platform default, and creates
            // the directory if needed - so it always returns a real, existing path.
            _screenshotsDirectory = await Task.Run(() => CcDirector.Core.Storage.CcStorage.Screenshots());

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

    private static List<ScreenshotViewModel> LoadScreenshotViewModels(string directory)
    {
        return Directory.GetFiles(directory)
            .Where(f => ScreenshotExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .Take(50)
            .Select(f => new ScreenshotViewModel(f))
            .ToList();
    }

    // Deletes every screenshot image file in the folder from disk and returns how many were removed.
    // Clear All must delete the files, not just empty the in-memory list: the folder watcher re-reads
    // the directory on the next change and any surviving file reappears in the panel (issue #1494).
    private static int DeleteAllScreenshots(string directory)
    {
        var files = Directory.GetFiles(directory)
            .Where(f => ScreenshotExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        var deleted = 0;
        foreach (var file in files)
        {
            File.Delete(file);
            deleted++;
        }
        return deleted;
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

    private async void BtnClearScreenshots_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] BtnClearScreenshots_Click");
        try
        {
            if (_screenshotsDirectory == null)
                return;

            // Deleting the files is permanent, so warn before destroying data (issue #1494).
            var confirm = new ConfirmDialog(
                "Clear all screenshots?",
                "This permanently deletes every screenshot in this Director's screenshots folder from disk. This cannot be undone.",
                confirmLabel: "Delete All");
            if (await confirm.ShowDialog<bool>(this) != true)
                return;

            var deleted = await Task.Run(() => DeleteAllScreenshots(_screenshotsDirectory));
            FileLog.Write($"[MainWindow] BtnClearScreenshots_Click: deleted {deleted} file(s)");

            // Re-read from disk so the panel reflects the now-empty folder even if the watcher's
            // debounced refresh has not fired yet.
            await RefreshScreenshots();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] BtnClearScreenshots_Click FAILED: {ex.Message}");
            await new MessageDialog("Cannot Clear Screenshots", ex.Message).ShowDialog<bool?>(this);
        }
    }

    private async void ScreenshotItem_PointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not ScreenshotViewModel vm)
            return;

        FileLog.Write($"[MainWindow] ScreenshotItem_PointerPressed: {vm.FilePath}");

        // Only start drag on left button press
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;

        var dataObject = new DataObject();
        dataObject.Set(DataFormats.Text, vm.FilePath);
        await DragDrop.DoDragDrop(e, dataObject, global::Avalonia.Input.DragDropEffects.Copy);
    }

    private void ScreenshotView_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string filePath)
            return;

        FileLog.Write($"[MainWindow] ScreenshotView_Click: {filePath}");
        try
        {
            // Open images in document tab if a session is active
            if (_activeSession != null && FileExtensions.IsViewable(filePath) && File.Exists(filePath))
            {
                OpenDocumentFile(filePath);
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] ScreenshotView_Click FAILED: {ex.Message}");
        }
    }

    private async void ScreenshotCopy_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string filePath)
            return;

        FileLog.Write($"[MainWindow] ScreenshotCopy_Click: {filePath}");
        try
        {
            // Copy the actual image (not the path) so it pastes into GitHub,
            // Claude Code, Paint, etc. Dragging the thumbnail still gives the path.
            await Task.Run(() => WindowsClipboardImage.CopyImageFile(filePath));
            ShowNotification("Screenshot copied -- paste with Ctrl+V");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] ScreenshotCopy_Click FAILED: {ex.Message}");
            ShowNotification($"Copy failed: {ex.Message}");
        }
    }

    /// <summary>
    /// One-click "file a bug from this screenshot": copies the image onto the
    /// clipboard, then opens GitHub's new-issue form for the active session's repo
    /// so the screenshot can be pasted straight into the issue body with Ctrl+V.
    /// Hard failures (no session, no GitHub origin) surface as a modal dialog --
    /// the bottom notification bar is too easy to miss for a refused action.
    /// </summary>
    private async void ScreenshotCreateIssue_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string filePath)
            return;

        FileLog.Write($"[MainWindow] ScreenshotCreateIssue_Click: {filePath}");
        try
        {
            // Modal dialogs already block this button; this guards the one
            // non-modal owned window (the startup restore-progress dialog).
            if (OwnedWindows.Count > 0)
            {
                FileLog.Write("[MainWindow] ScreenshotCreateIssue_Click: refused, owned window open");
                return;
            }

            var session = _activeSession;
            if (session == null)
            {
                await new MessageDialog(
                    "Select a Session First",
                    "The GitHub issue is created in the repository of the active session. " +
                    "Select or create a session, then click Issue again.")
                    .ShowDialog<bool?>(this);
                return;
            }

            var repoPath = session.Session.RepoPath;
            var url = await Task.Run(() =>
            {
                WindowsClipboardImage.CopyImageFile(filePath);
                return GitHubUrls.BuildNewIssueUrl(repoPath);
            });

            FileLog.Write($"[MainWindow] ScreenshotCreateIssue_Click: opening {url}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            ShowPasteCard(
                "Browser opened -- one step left",
                "The screenshot is on the clipboard. Click into the issue body and press Ctrl+V to attach it.");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] ScreenshotCreateIssue_Click FAILED: {ex.Message}");
            await new MessageDialog("Cannot Create GitHub Issue", ex.Message).ShowDialog<bool?>(this);
        }
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
            // Open viewable files in document tabs; everything else externally
            if (FileExtensions.IsViewable(fullPath) && File.Exists(fullPath))
            {
                OpenDocumentFile(fullPath);
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fullPath,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] OnGitViewFileRequested FAILED: {ex.Message}");
        }
    }

    // ==================== RIGHT PANEL TAB SWITCHING ====================

    // ==================== WINDOW CLOSING ====================

    private bool _closeConfirmed;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        // Log WHY the window is closing (issue #212 L1). The 2026-06-06 post-mortem
        // could not tell an OS shutdown from a user "End task" from a programmatic close
        // because OnClosing logged nothing but the bare event. CloseReason answers it:
        // WindowClosing (user X/Alt+F4), OSShutdown, ApplicationShutdown, OwnerWindowClosing.
        FileLog.Write($"[MainWindow] OnClosing: reason={e.CloseReason}, programmatic={e.IsProgrammatic}, sessions={_sessions.Count}");

        // Check for working sessions and show close dialog
        if (!_closeConfirmed)
        {
            var workingSessions = _sessions
                .Where(vm => vm.Session.ActivityState is ActivityState.Working or ActivityState.WaitingForInput)
                .ToList();

            if (workingSessions.Count > 0)
            {
                e.Cancel = true;

                var sessionNames = workingSessions
                    .Select(vm => vm.DisplayName)
                    .ToList();

                var dialog = new CloseDialog(_sessionManager, sessionNames);
                var result = await dialog.ShowDialog<bool?>(this);

                if (result == true)
                {
                    _closeConfirmed = true;
                    Close();
                }

                return;
            }
        }

        // Unsubscribe from active session events
        if (_activeSession != null)
        {
            _activeSession.Session.OnClaudeMetadataChanged -= OnActiveSessionMetadataChanged;
            _activeSession.Session.OnActivityStateChanged -= OnActiveSessionActivityChanged;
        }

        // Update LastUsedAt for all active sessions in history
        UpdateAllSessionHistoryTimestamps();

        // Cancel any pending debounced persist and flush immediately
        _persistDebounceCts?.Cancel();
        SyncPromptTextToSessions();
        PersistSessionStateCore();

        // Detach terminal and source control
        TerminalHost.Detach();
        GitChangesView.Detach();
        _activeSession = null;

        // Stop git status polling
        _sessionGitTimer?.Stop();

        // Cleanup screenshot watcher
        _screenshotDebounceTimer?.Stop();
        _screenshotWatcher?.Dispose();

        // Unwire session registration
        try
        {
            _sessionManager.OnClaudeSessionRegistered -= OnClaudeSessionRegistered;
            _sessionManager.OnSessionCreated -= OnExternalSessionCreated;
            _sessionManager.OnSessionRenamed -= OnExternalSessionRenamed;
            _sessionManager.OnSessionRemoved -= OnExternalSessionRemoved;
        }
        catch { /* App may be shutting down */ }

        // Call shutdown directly instead of relying on ShutdownRequested event
        // which may never fire depending on Avalonia lifetime state.
        // OnShutdown kills sessions, disposes services, and calls Environment.Exit(0).
        var appRef = (App)global::Avalonia.Application.Current!;
        appRef.OnShutdown(msg => FileLog.Write($"[CcDirector] {msg}"));

        // Environment.Exit(0) inside OnShutdown means we never reach here,
        // but keep base.OnClosing as a safety net.
        base.OnClosing(e);
    }

    private void OnClaudeSessionRegistered(Session session, string claudeSessionId)
    {
        FileLog.Write($"[MainWindow] Claude session registered: {claudeSessionId} for {session.RepoPath}");
        Dispatcher.UIThread.Post(() =>
        {
            PersistSessionState();

            // Update session history entry with the new ClaudeSessionId
            var sessionVm = _sessions.FirstOrDefault(s => s.Session.Id == session.Id);
            if (sessionVm != null)
                UpdateSessionHistory(sessionVm);

            // Update header if this is the active session
            if (_activeSession?.Session.Id == session.Id)
                UpdateSessionHeader();
        });
    }

    // ==================== SESSION HISTORY ====================

    /// <summary>
    /// Create a new history entry for a session that was just created and renamed.
    /// </summary>
    private void SaveSessionToHistory(SessionViewModel vm)
    {
        FileLog.Write($"[MainWindow] SaveSessionToHistory: session={vm.Session.Id}, name={vm.Session.CustomName}, repo={vm.Session.RepoPath}");
        var app = (App)global::Avalonia.Application.Current!;
        var entry = new SessionHistoryEntry
        {
            Id = vm.Session.HistoryEntryId ?? Guid.NewGuid(),
            CustomName = vm.Session.CustomName,
            CustomColor = vm.Session.CustomColor,
            RepoPath = vm.Session.RepoPath,
            ClaudeSessionId = vm.Session.ClaudeSessionId,
            CreatedAt = vm.Session.CreatedAt,
            LastUsedAt = DateTimeOffset.UtcNow,
        };
        vm.Session.HistoryEntryId = entry.Id;
        app.SessionHistoryStore.Save(entry);
        FileLog.Write($"[MainWindow] SaveSessionToHistory: saved historyEntryId={entry.Id}");
    }

    /// <summary>
    /// Update an existing history entry with the session's current name, color, and ClaudeSessionId.
    /// </summary>
    private void UpdateSessionHistory(SessionViewModel vm)
    {
        if (vm.Session.HistoryEntryId == null)
        {
            SaveSessionToHistory(vm);
            return;
        }

        var app = (App)global::Avalonia.Application.Current!;
        var entry = app.SessionHistoryStore.Load(vm.Session.HistoryEntryId.Value);
        if (entry == null)
        {
            SaveSessionToHistory(vm);
            return;
        }

        entry.CustomName = vm.Session.CustomName;
        entry.CustomColor = vm.Session.CustomColor;
        entry.ClaudeSessionId = vm.Session.ClaudeSessionId;
        entry.LastUsedAt = DateTimeOffset.UtcNow;
        entry.FirstPromptSnippet = vm.Session.ClaudeMetadata?.FirstPrompt ?? entry.FirstPromptSnippet;
        app.SessionHistoryStore.Save(entry);
    }

    /// <summary>
    /// Update LastUsedAt for all active sessions in history. Called on app close.
    /// </summary>
    private void UpdateAllSessionHistoryTimestamps()
    {
        var app = (App)global::Avalonia.Application.Current!;
        foreach (var vm in _sessions)
        {
            if (vm.Session.HistoryEntryId == null)
                continue;

            var entry = app.SessionHistoryStore.Load(vm.Session.HistoryEntryId.Value);
            if (entry == null)
                continue;

            entry.LastUsedAt = DateTimeOffset.UtcNow;
            entry.ClaudeSessionId = vm.Session.ClaudeSessionId ?? entry.ClaudeSessionId;
            app.SessionHistoryStore.Save(entry);
        }
    }

    // ==================== HANDOVER INJECTION ====================

    /// <summary>
    /// After a new session starts from a handover, wait for Claude Code to be ready
    /// and then send the handover file as a prompt asking it to review and plan next steps.
    /// </summary>
    private async Task InjectHandoverPromptAsync(Session session, string handoverPath)
    {
        FileLog.Write($"[MainWindow] InjectHandoverPromptAsync: waiting for session {session.Id}, handover={handoverPath}");

        // Wait for Claude Code to finish starting up
        await Task.Delay(TimeSpan.FromSeconds(5));

        var prompt = $"@{handoverPath} This is a handover document from a previous session. "
            + "Please read it carefully, then give me a high-level summary of what was done "
            + "and what you think we should work on next. Show the scope of remaining work "
            + "and suggest priorities.";

        await session.SendTextAsync(prompt, SendSource.Internal);
        FileLog.Write($"[MainWindow] InjectHandoverPromptAsync: sent handover prompt for session {session.Id}");
    }

    // ==================== STARTUP TEXT CAPTURE ====================

    /// <summary>
    /// Capture terminal startup text after a brief delay and persist it to the session.
    /// Also writes a debug dump to %LOCALAPPDATA%\CcDirector\debug\.
    /// </summary>
    private async Task CaptureStartupTextAsync(Session session)
    {
        try
        {
            FileLog.Write($"[MainWindow] CaptureStartupTextAsync: waiting 3s for session {session.Id}");
            await Task.Delay(TimeSpan.FromSeconds(3));

            if (session.Buffer == null)
            {
                FileLog.Write($"[MainWindow] CaptureStartupTextAsync: no buffer for session {session.Id}");
                return;
            }

            var startupInfo = TerminalOutputParser.Parse(session.Buffer);
            session.RawStartupText = startupInfo.RawText;
            FileLog.Write($"[MainWindow] CaptureStartupTextAsync: captured {startupInfo.RawText.Length} bytes, {startupInfo.Urls.Count} URLs for session {session.Id}");

            var debugDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CcDirector", "debug");
            Directory.CreateDirectory(debugDir);
            var debugPath = Path.Combine(debugDir, $"startup-{session.Id}.txt");
            TerminalOutputParser.WriteDump(debugPath, startupInfo, session.Id, session.RepoPath, session.ProcessId);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] CaptureStartupTextAsync FAILED: {ex.Message}");
        }
    }

    // ==================== SESSION GIT STATUS POLLING ====================

    private async Task RefreshSessionGitStatusAsync()
    {
        if (_sessionGitRefreshRunning) return;
        _sessionGitRefreshRunning = true;

        try
        {
            var sessions = _sessions.ToList();
            using var semaphore = new SemaphoreSlim(4);

            var tasks = sessions.Select(async vm =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var repoPath = vm.Session.RepoPath;
                    if (!Directory.Exists(repoPath)) return;

                    int count = await _gitStatusProvider.GetCountAsync(repoPath);
                    global::Avalonia.Threading.Dispatcher.UIThread.Post(() => vm.UncommittedCount = count);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }
        finally
        {
            _sessionGitRefreshRunning = false;
        }
    }

    // ==================== DOCUMENT TABS ====================

    private record DocumentTabInfo(
        Guid SessionId,
        string FilePath,
        string TabId,
        Button TabButton,
        UserControl ViewerControl,
        FileViewerControls.IFileViewer Viewer);

    private readonly List<DocumentTabInfo> _documentTabs = new();

    /// <summary>
    /// Opens a file in a document tab, or switches to it if already open.
    /// </summary>
    public void OpenDocumentFile(string filePath)
    {
        FileLog.Write($"[MainWindow] OpenDocumentFile: {filePath}");

        if (_activeSession == null) return;

        var sessionId = _activeSession.Session.Id;

        // Check if already open for this session
        var existing = _documentTabs.FirstOrDefault(d =>
            d.SessionId == sessionId &&
            string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            SwitchLeftTab(existing.TabId);
            return;
        }

        // Create the appropriate viewer
        var category = FileExtensions.GetViewerCategory(filePath);
        var (viewer, control) = CreateViewer(category);

        var tabId = $"Doc:{Guid.NewGuid():N}";

        // Create tab button with close button
        var tabPanel = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        var nameText = new TextBlock
        {
            Text = Path.GetFileName(filePath),
            FontSize = 12,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
        };
        var closeBtn = new Button
        {
            Content = "x",
            FontSize = 9,
            Padding = new global::Avalonia.Thickness(4, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse("#666666")),
            BorderThickness = new global::Avalonia.Thickness(0),
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
        };
        tabPanel.Children.Add(nameText);
        tabPanel.Children.Add(closeBtn);

        var tabButton = new Button
        {
            Content = tabPanel,
            Background = Brushes.Transparent,
            Foreground = InactiveTextBrush,
            Padding = new global::Avalonia.Thickness(12, 4),
            BorderThickness = new global::Avalonia.Thickness(0),
            Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
        };

        var docTab = new DocumentTabInfo(sessionId, filePath, tabId, tabButton, control, viewer);

        // Wire tab button click
        var capturedTabId = tabId;
        tabButton.Click += (_, _) => SwitchLeftTab(capturedTabId);

        // Wire close button
        closeBtn.Click += (_, _) =>
        {
            CloseDocumentTab(docTab);
            // Prevent the tab button click from also firing
        };

        // Wire display name changes (dirty indicator)
        viewer.DisplayNameChanged += () =>
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                nameText.Text = viewer.GetDisplayName();
            });
        };

        _documentTabs.Add(docTab);

        // Add button to the tab bar
        DocumentTabBar.Items.Add(tabButton);
        DocTabSeparator.IsVisible = true;
        DocumentTabBar.IsVisible = true;
        CloseAllDocsButton.IsVisible = true;

        // Switch to the new tab
        SwitchLeftTab(tabId);

        // Load file content asynchronously
        LoadDocumentContentInBackground(viewer, filePath);
    }

    private static (FileViewerControls.IFileViewer viewer, UserControl control) CreateViewer(FileViewerCategory category)
    {
        switch (category)
        {
            case FileViewerCategory.Image:
                var img = new FileViewerControls.ImageViewerControl();
                return (img, img);
            case FileViewerCategory.Code:
                var code = new FileViewerControls.CodeViewerControl();
                return (code, code);
            case FileViewerCategory.Markdown:
                var md = new FileViewerControls.MarkdownViewerControl();
                return (md, md);
            case FileViewerCategory.Pdf:
                var pdf = new FileViewerControls.PdfViewerControl();
                return (pdf, pdf);
            case FileViewerCategory.Html:
                var html = new FileViewerControls.HtmlViewerControl();
                return (html, html);
            case FileViewerCategory.Text:
            default:
                var text = new FileViewerControls.TextViewerControl();
                return (text, text);
        }
    }

    private async void LoadDocumentContentInBackground(FileViewerControls.IFileViewer viewer, string filePath)
    {
        try
        {
            await viewer.LoadFileAsync(filePath);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MainWindow] LoadDocumentContentInBackground FAILED: {ex.Message}");
            viewer.ShowLoadError(ex.Message);
        }
    }

    private void CloseDocumentTab(DocumentTabInfo docTab)
    {
        FileLog.Write($"[MainWindow] CloseDocumentTab: {docTab.FilePath}");

        // Remove from tracking
        _documentTabs.Remove(docTab);

        // Remove button from tab bar
        DocumentTabBar.Items.Remove(docTab.TabButton);

        // Remove from document panel if currently shown
        if (DocumentPanel.Children.Contains(docTab.ViewerControl))
            DocumentPanel.Children.Remove(docTab.ViewerControl);

        // Update visibility of doc tab UI
        var hasDocTabs = _documentTabs.Any(d => d.SessionId == (_activeSession?.Session.Id ?? Guid.Empty));
        DocTabSeparator.IsVisible = hasDocTabs;
        DocumentTabBar.IsVisible = _documentTabs.Count > 0;
        CloseAllDocsButton.IsVisible = hasDocTabs;

        // If the closed tab was active, switch to Terminal
        if (_activeLeftTab == docTab.TabId)
            SwitchLeftTab("Terminal");
    }

    private void CloseAllDocsButton_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MainWindow] CloseAllDocsButton_Click");

        if (_activeSession == null) return;

        var sessionId = _activeSession.Session.Id;
        var toClose = _documentTabs.Where(d => d.SessionId == sessionId).ToList();

        foreach (var docTab in toClose)
        {
            _documentTabs.Remove(docTab);
            DocumentTabBar.Items.Remove(docTab.TabButton);
            if (DocumentPanel.Children.Contains(docTab.ViewerControl))
                DocumentPanel.Children.Remove(docTab.ViewerControl);
        }

        DocTabSeparator.IsVisible = false;
        DocumentTabBar.IsVisible = _documentTabs.Count > 0;
        CloseAllDocsButton.IsVisible = false;

        if (_activeLeftTab.StartsWith("Doc:", StringComparison.Ordinal))
            SwitchLeftTab("Terminal");
    }

    /// <summary>
    /// Shows/hides document tab buttons based on the active session.
    /// Called when switching sessions.
    /// </summary>
    private void SwitchDocumentTabsToSession(Guid sessionId)
    {
        FileLog.Write($"[MainWindow] SwitchDocumentTabsToSession: {sessionId}");

        // Rebuild the tab bar items for the new session
        DocumentTabBar.Items.Clear();

        var sessionDocTabs = _documentTabs.Where(d => d.SessionId == sessionId).ToList();

        foreach (var docTab in sessionDocTabs)
            DocumentTabBar.Items.Add(docTab.TabButton);

        var hasTabs = sessionDocTabs.Count > 0;
        DocTabSeparator.IsVisible = hasTabs;
        DocumentTabBar.IsVisible = _documentTabs.Count > 0;
        CloseAllDocsButton.IsVisible = hasTabs;

        // If the active tab was a document from a different session, switch to Terminal
        if (_activeLeftTab.StartsWith("Doc:", StringComparison.Ordinal))
        {
            var isStillValid = sessionDocTabs.Any(d => d.TabId == _activeLeftTab);
            if (!isStillValid)
            {
                // Force tab switch by resetting _activeLeftTab
                _activeLeftTab = "";
                SwitchLeftTab("Terminal");
            }
        }
    }
}
