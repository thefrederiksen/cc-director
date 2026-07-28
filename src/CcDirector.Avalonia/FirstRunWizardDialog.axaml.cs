using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.GatewayConnection;
using System.Text.Json.Nodes;
using Avalonia.Platform.Storage;
using CcDirector.Core.Onboarding;
using CcDirector.Core.Settings;
using CcDirector.Core.Tools;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

namespace CcDirector.Avalonia;

/// <summary>
/// First-run setup wizard shell (issue #2101, epic #2100). One dialog hosts the whole guided flow,
/// replacing the retired chain of two dialogs (<see cref="OnboardingWizardDialog"/> then
/// <see cref="ToolDetectionWizardDialog"/>). This shell ships the frame - a step-dot progress
/// indicator, one primary action per screen, a bottom-left Back link, and per-step skip where
/// allowed - plus the two bookend screens (Welcome, Done). The middle steps land as their own issues
/// and slot in; until then the shell presents the existing equivalents inline (the tool-detection
/// scan for Agents, <see cref="Controls.GatewayConnectionPanel"/> for Gateway).
///
/// All step order, navigation, dot state and skip rules live UI-free in
/// <see cref="FirstRunWizardModel"/>; this dialog is the thin Avalonia shell over it. On any exit -
/// finishing on Done, the whole-wizard skip on Welcome, or closing the window - the completion marker
/// is written so the wizard never auto-opens again.
/// </summary>
public partial class FirstRunWizardDialog : Window
{
    private readonly AgentOptions _options;
    private readonly ToolDetectionWizardModel _toolModel = new(new ToolDetectionService());
    private readonly ToolDetectionService _detectionService = new();
    private CancellationTokenSource? _claudeInstallCts;
    private readonly FirstRunWizardModel _model;
    private readonly List<Ellipse> _dots = new();

    // Agents-step scan results, cached so accept and the Done receipt can read them without re-scanning.
    private IReadOnlyList<ToolDetectionSuggestion> _agentSuggestions = Array.Empty<ToolDetectionSuggestion>();
    private HashSet<AgentKind> _existingAgentTypes = new();
    private bool _agentScanRan;

    // The gateway step's three-way choice. Hosted is the recommended default, pre-selected per the
    // mockup: most users should sign in and be done. Self-host and Not-now are the quiet minority paths.
    private enum GatewayChoice { Hosted, SelfHost, NotNow }

    private GatewayChoice _gatewayChoice = GatewayChoice.Hosted;
    private bool _gatewayConnected;
    private CancellationTokenSource? _hostedEnrollCts;

    // The existing join-an-existing-gateway flow, embedded only behind the self-hosted advanced path.
    private Controls.GatewayConnectionPanel? _gatewayPanel;

    // Screenshots step: the folder the user is about to confirm, its plain-English provenance, and
    // the live take-a-screenshot watch.
    private string? _shotsSelectedPath;
    private bool _shotsDetectRan;
    private CancellationTokenSource? _shotsWatchCts;

    // Re-points the live screenshots panel after the Screenshots step writes the folder. Null when
    // no panel is listening (the wizard opened from a context that owns none).
    private readonly Func<Task>? _reloadScreenshots;

    // Tools step: the shipped cc-* toolbelt, read from the embedded manifest catalog. The screen
    // explains that DevThrottle maintains these itself; while any are still installing it polls so
    // rows flip to Ready live - the same state the main window's corner indicator tracks.
    private global::Avalonia.Threading.DispatcherTimer? _toolsPollTimer;
    private int _toolsReadyCount;
    private int _toolsTotalCount;

    // Code step: the roots store the board and New Session read, the folders added this run
    // (path -> repo count, drives the proof number), and the one-shot suggestion scan.
    private readonly RootDirectoryStore _rootStore = new();
    private bool _rootStoreLoaded;
    private readonly Dictionary<string, int> _codeAddedRoots = new(StringComparer.OrdinalIgnoreCase);
    private bool _codeScanRan;
    private CancellationTokenSource? _codeScanCts;

    // Set when the user says there is no code on this machine yet (a new laptop). It changes nothing
    // on disk - it only stops the step, and the Done receipt, from reading as a failure.
    private bool _codeNoneOnThisMachine;

    // True once the completion marker has been written, so OnClosed does not write it twice.
    private bool _marked;

    /// <summary>
    /// True when the user chose "Start my first agent" on the Done screen, so the caller opens the
    /// New Session dialog after this wizard closes. False on the board link, whole-wizard skip, or a
    /// plain window close.
    /// </summary>
    public bool WantsNewSession { get; private set; }

    public FirstRunWizardDialog() : this(new AgentOptions()) { }

    /// <param name="options">The agent options the Agents and Tools steps read.</param>
    /// <param name="reloadScreenshots">
    /// Re-points the main window's screenshots panel at the folder just confirmed. Passed in by
    /// whoever owns that panel (MainWindow, directly or through Settings) so the Screenshots step
    /// pays off while the wizard is still open instead of only after the next restart.
    /// </param>
    public FirstRunWizardDialog(AgentOptions options, Func<Task>? reloadScreenshots = null)
    {
        FileLog.Write("[FirstRunWizardDialog] Constructor: initializing");
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _reloadScreenshots = reloadScreenshots;

        // All seven canonical steps are present: the onboarding is one JOURNEY (your workforce ->
        // what we watch over -> show don't type -> with you everywhere -> the payoff -> done), and
        // the Morning report screen is the promise the rest of the story builds to.
        _model = new FirstRunWizardModel(FirstRunWizardModel.CanonicalOrder);

        InitializeComponent();
        BuildDots();
        ShowStep(_model.Current);
    }

    /// <summary>Create one progress dot per present step. Colours are refreshed on every step change.</summary>
    private void BuildDots()
    {
        DotsPanel.Children.Clear();
        _dots.Clear();
        for (var i = 0; i < _model.Count; i++)
        {
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _dots.Add(dot);
            DotsPanel.Children.Add(dot);
        }
        RefreshDots();
    }

    /// <summary>Paint each dot done / current / upcoming from the model's verdict (never re-derived here).</summary>
    private void RefreshDots()
    {
        for (var i = 0; i < _dots.Count; i++)
        {
            _dots[i].Fill = _model.DotStateAt(i) switch
            {
                WizardDotState.Current => Brush("#0066B8"),
                WizardDotState.Past => Brush("#9FC4E3"),
                _ => Brush("#E6E8EC"),
            };
        }
    }

    /// <summary>
    /// Switch to the given step: show its panel, refresh the dots, and configure the footer (Back,
    /// skip, primary CTA, note) from the model's rules. Triggers each step's one-time side effect
    /// (the agent scan, the gateway panel, the Done receipt).
    /// </summary>
    private void ShowStep(WizardStep step)
    {
        FileLog.Write($"[FirstRunWizardDialog] ShowStep: {step}");

        WelcomePanel.IsVisible = step == WizardStep.Welcome;
        AgentsPanel.IsVisible = step == WizardStep.Agents;
        ToolsPanel.IsVisible = step == WizardStep.Tools;
        CodePanel.IsVisible = step == WizardStep.Code;
        ScreenshotsPanel.IsVisible = step == WizardStep.Screenshots;
        GatewayPanel.IsVisible = step == WizardStep.Gateway;
        DonePanel.IsVisible = step == WizardStep.Done;

        // Leaving the Screenshots step ends any take-a-screenshot watch; leaving Tools stops the
        // install-progress poll.
        if (step != WizardStep.Screenshots)
            CancelScreenshotWatch();
        if (step != WizardStep.Tools)
            StopToolsPoll();

        RefreshDots();

        // Back link everywhere except the first step.
        BackButton.IsVisible = !_model.IsFirst;

        // Defaults: the per-step configuration below overrides what it needs.
        PrimaryButton.IsVisible = false;
        FooterNote.IsVisible = false;

        // The wizard is an OFFER, not a gauntlet: on every step the primary button moves you
        // forward whether or not you took the offer (doing nothing writes nothing), so there is no
        // separate skip link. The two deliberate exceptions: zero agents blocks Continue (the
        // product cannot work without one - "I'll do this later" is the honest way past), and the
        // whole-wizard skip lives on Welcome.
        switch (step)
        {
            case WizardStep.Welcome:
                // Primary ("Set me up") and the quiet whole-wizard skip live in the content panel.
                FooterNote.Text = "Takes about 3 minutes. Every step is optional, and everything can be changed later in Settings.";
                FooterNote.IsVisible = true;
                break;

            case WizardStep.Agents:
                PrimaryButton.Content = "Use these agents";
                PrimaryButton.IsVisible = true;
                if (!_agentScanRan)
                    _ = ScanAgentsAsync();
                break;

            case WizardStep.Tools:
                PrimaryButton.Content = "Continue";
                PrimaryButton.IsVisible = true;
                PrimaryButton.IsEnabled = true;
                _ = RefreshToolsScreenAsync();
                break;

            case WizardStep.Code:
                PrimaryButton.Content = _codeAddedRoots.Count > 0 ? "Looks right" : "Continue";
                PrimaryButton.IsVisible = true;
                if (!_codeScanRan)
                    _ = ScanCodeFoldersAsync();
                break;

            case WizardStep.Screenshots:
                // Always live: with a folder chosen it confirms; without one it simply moves on
                // and saves nothing.
                PrimaryButton.Content = _shotsSelectedPath is not null ? "Use this folder" : "Continue";
                PrimaryButton.IsVisible = true;
                PrimaryButton.IsEnabled = true;
                if (!_shotsDetectRan)
                    _ = DetectScreenshotsForWizardAsync();
                break;

            case WizardStep.Gateway:
                PrimaryButton.IsVisible = true;
                RefreshGatewayChoiceUi();
                break;

            case WizardStep.Done:
                // Primary and the board link live in the content panel. With no agent on the machine
                // the carried to-do leads: the button routes back to the Agents step and its installer
                // instead of promising a session that cannot start.
                DoneStartButton.Content = _model.AgentsFound ? "Start my first agent" : "Install an agent";
                // Never close with an instruction the same screen says cannot be followed: with no code
                // folder there is nothing to start an agent on, so adding one is the closing step.
                DoneSubText.Text = _codeAddedRoots.Values.Sum() > 0
                    ? "Start an agent on one of your repositories - give it a small task and watch the card, not the terminal. Tomorrow morning, DevThrottle reports back on how it went."
                    : "Add a code folder first - the Repositories view in the left rail - and DevThrottle can start an agent on it. Everything else here is already set up.";
                FooterNote.Text = "Everything here can be changed in Settings.";
                FooterNote.IsVisible = true;
                BuildDoneReceipt();
                break;
        }
    }

    // ---- Navigation --------------------------------------------------------------------------------

    /// <summary>The primary action for the current step. Welcome/Gateway advance; Agents accepts then
    /// advances; the Done board-link and Welcome content route here too and do the right thing.</summary>
    private async void BtnPrimary_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write($"[FirstRunWizardDialog] BtnPrimary_Click: step={_model.Current}");
        try
        {
            switch (_model.Current)
            {
                case WizardStep.Agents:
                    await AcceptAgentsAsync();
                    Advance();
                    break;

                case WizardStep.Screenshots:
                    await SaveScreenshotsFolderAsync();
                    Advance();
                    break;

                case WizardStep.Gateway:
                    if (_gatewayConnected || _gatewayChoice == GatewayChoice.NotNow)
                        Advance();
                    else if (_gatewayChoice == GatewayChoice.SelfHost)
                        ShowGatewayAdvanced();
                    else
                        await StartHostedEnrollAsync();
                    break;

                case WizardStep.Done:
                    // The Done "Take me to the board" quiet link routes here: finish without a session.
                    await FinishAsync(wantsNewSession: false);
                    break;

                default:
                    Advance();
                    break;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] BtnPrimary_Click FAILED: {ex.Message}");
        }
    }

    /// <summary>Advance to the next present step, or finish when already on the last step.</summary>
    private void Advance()
    {
        if (_model.MoveNext())
            ShowStep(_model.Current);
        else
            _ = FinishAsync(wantsNewSession: false);
    }

    private void BtnBack_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write($"[FirstRunWizardDialog] BtnBack_Click: step={_model.Current}");
        if (_model.MoveBack())
            ShowStep(_model.Current);
    }

    /// <summary>Individual per-step skip: advance past this step without acting on it.</summary>
    /// <summary>The quiet whole-wizard skip on Welcome: drop straight to the board and write the marker.</summary>
    private async void BtnWholeWizardSkip_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FirstRunWizardDialog] BtnWholeWizardSkip_Click");
        try
        {
            await FinishAsync(wantsNewSession: false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] BtnWholeWizardSkip_Click FAILED: {ex.Message}");
            Close(false);
        }
    }

    private async void BtnStartFirstAgent_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write($"[FirstRunWizardDialog] BtnStartFirstAgent_Click: agentsFound={_model.AgentsFound}");
        try
        {
            if (!_model.AgentsFound)
            {
                // "Install an agent": jump back to the Agents step, whose empty state carries the
                // in-place installer. Nothing is finished yet - the wizard stays open.
                _model.GoTo(WizardStep.Agents);
                ShowStep(_model.Current);
                return;
            }
            await FinishAsync(wantsNewSession: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] BtnStartFirstAgent_Click FAILED: {ex.Message}");
        }
    }

    // ---- Agents step -------------------------------------------------------------------------------

    private async Task ScanAgentsAsync()
    {
        FileLog.Write("[FirstRunWizardDialog] ScanAgentsAsync");
        AgentsTitle.Text = "Your agents";
        AgentsStatusText.Text = "Scanning this machine for coding agents...";
        AgentsEmptyActions.IsVisible = false;
        try
        {
            var (suggestions, existing) = await Task.Run(() =>
            {
                var scanned = _toolModel.ScanSuggestions(_options);
                var present = new HashSet<AgentKind>(AgentEntryStore.ReadCurrentEntries().Select(en => en.Type));
                return (scanned, present);
            });

            _agentSuggestions = suggestions;
            _existingAgentTypes = existing;
            _agentScanRan = true;

            var anyFound = suggestions.Any(s => s.Found) || existing.Count > 0;
            _model.SetAgentsFound(anyFound);

            // Probe each found agent's version so the rows read "v2.1.4 - path": the version is the
            // proof the detection is real, not a guess. Best-effort - a probe that fails or times
            // out just leaves the row without a version.
            var versions = await ProbeVersionsAsync(suggestions);
            BuildAgentRows(suggestions, existing, versions);

            var foundCount = suggestions.Count(s => s.Found);
            if (anyFound)
            {
                AgentsTitle.Text = $"We found {foundCount} coding {(foundCount == 1 ? "agent" : "agents")}";
                AgentsStatusText.Text = "These are ready to use. You can add more or change paths later in Settings.";
            }
            else
            {
                AgentsTitle.Text = "You need a coding agent";
                AgentsStatusText.Text = "DevThrottle runs and supervises command-line coding agents, and we did not find any on this machine - so let's install one now.";
            }

            // Zero agents: the one step Continue does not pass. The product cannot work without an
            // agent, so the in-place install (or the honest deferral) is the way forward.
            AgentsEmptyActions.IsVisible = !anyFound;
            PrimaryButton.IsEnabled = anyFound;

            FileLog.Write($"[FirstRunWizardDialog] ScanAgentsAsync: found={foundCount}, anyFound={anyFound}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] ScanAgentsAsync FAILED: {ex.Message}");
            AgentsStatusText.Text = $"Agent scan failed: {ex.Message}";
        }
    }

    /// <summary>Version-probe every found agent (bounded per-tool by the plugin's validation timeout).</summary>
    private async Task<Dictionary<AgentKind, string>> ProbeVersionsAsync(IReadOnlyList<ToolDetectionSuggestion> suggestions)
    {
        var versions = new Dictionary<AgentKind, string>();
        foreach (var s in suggestions.Where(s => s.Found))
        {
            try
            {
                var test = await _detectionService.TestToolAsync(s.Tool, s.ResolvedPath);
                if (test.Ok && !string.IsNullOrWhiteSpace(test.Version))
                    versions[s.Tool] = test.Version!;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[FirstRunWizardDialog] ProbeVersionsAsync: {s.Tool} probe failed: {ex.Message}");
            }
        }
        return versions;
    }

    private void BuildAgentRows(
        IReadOnlyList<ToolDetectionSuggestion> suggestions,
        ISet<AgentKind> existing,
        IReadOnlyDictionary<AgentKind, string> versions)
    {
        AgentsListPanel.Children.Clear();

        // Found (or already-added) agents first, each as its own Ready row; then a single summary row
        // for everything not installed, matching the mockup's found-state layout.
        foreach (var s in suggestions.Where(s => s.Found))
        {
            var alreadyAdded = existing.Contains(s.Tool);
            var detail = versions.TryGetValue(s.Tool, out var v)
                ? $"{(v.StartsWith('v') || v.StartsWith('V') ? v : "v" + v)} - {s.ResolvedPath}"
                : s.ResolvedPath;
            AgentsListPanel.Children.Add(AgentRow(
                s.DisplayName,
                alreadyAdded ? $"Already in your Agents list - {detail}" : detail,
                alreadyAdded ? "In list" : "Ready",
                ready: true));
        }

        var notFound = suggestions.Where(s => !s.Found).Select(s => s.DisplayName).ToList();
        if (notFound.Count > 0)
        {
            AgentsListPanel.Children.Add(AgentRow(
                string.Join(", ", notFound),
                "Not installed - you can add any of these later in Settings.",
                "Not found",
                ready: false));
        }
    }

    private static Border AgentRow(string name, string sub, string pillText, bool ready)
    {
        var pill = new Border
        {
            Background = Brush(ready ? "#E5F3E9" : "#F5F6F8"),
            CornerRadius = new global::Avalonia.CornerRadius(999),
            Padding = new global::Avalonia.Thickness(10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = pillText,
                Foreground = Brush(ready ? "#1A7F37" : "#8A909A"),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
            },
        };

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = Brush(ready ? "#16181D" : "#8A909A"),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = sub,
            Foreground = Brush("#8A909A"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        global::Avalonia.Controls.Grid.SetColumn(text, 0);
        global::Avalonia.Controls.Grid.SetColumn(pill, 1);
        grid.Children.Add(text);
        grid.Children.Add(pill);

        return new Border
        {
            Background = Brush("#FFFFFF"),
            BorderBrush = Brush("#E6E8EC"),
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius = new global::Avalonia.CornerRadius(10),
            Padding = new global::Avalonia.Thickness(16, 13),
            Child = grid,
        };
    }

    /// <summary>Write the found, not-yet-added agents to the live agent list (same seam the tool wizard uses).</summary>
    private async Task AcceptAgentsAsync()
    {
        var selections = _agentSuggestions
            .Where(s => s.Found && !_existingAgentTypes.Contains(s.Tool))
            .Select(s => new AcceptedToolSelection(s.Tool, s.ResolvedPath))
            .ToList();

        if (selections.Count == 0)
        {
            FileLog.Write("[FirstRunWizardDialog] AcceptAgentsAsync: nothing new to add");
            return;
        }

        var result = await Task.Run(() => ToolDetectionWizardModel.AcceptSelected(selections));
        foreach (var added in result.AddedTools)
            _existingAgentTypes.Add(added);
        FileLog.Write($"[FirstRunWizardDialog] AcceptAgentsAsync: added={result.AddedTools.Count}");
    }

    private void BtnInstallAgent_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FirstRunWizardDialog] BtnInstallAgent_Click");
        try
        {
            Process.Start(new ProcessStartInfo(OnboardingModel.ClaudeInstallUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] BtnInstallAgent_Click FAILED: {ex.Message}");
            AgentsStatusText.Text = $"Could not open the browser. Visit {OnboardingModel.ClaudeInstallUrl} manually.";
        }
    }

    private void BtnRecheckAgents_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FirstRunWizardDialog] BtnRecheckAgents_Click");
        _agentScanRan = false;
        _ = ScanAgentsAsync();
    }

    /// <summary>
    /// The zero-agents primary action: run the official Claude Code installer right here, stream its
    /// progress into the screen, and re-scan when it finishes - the user never leaves the wizard.
    /// </summary>
    private async void BtnInstallClaude_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FirstRunWizardDialog] BtnInstallClaude_Click");
        try
        {
            AgentsInstallButton.IsEnabled = false;
            AgentsRecheckButton.IsEnabled = false;
            AgentsDeferButton.IsEnabled = false;
            AgentsInstallErrorPanel.IsVisible = false;
            AgentsInstallProgressText.IsVisible = true;
            AgentsInstallProgressText.Text = "Starting the official Claude Code installer...";

            _claudeInstallCts?.Cancel();
            _claudeInstallCts = new CancellationTokenSource();

            // Progress<T> posts to the UI context it was created on, so the report lands on the UI thread.
            var progress = new Progress<string>(line => AgentsInstallProgressText.Text = line);
            var result = await new ClaudeCodeInstaller().InstallAsync(progress, _claudeInstallCts.Token);

            if (result.Success)
            {
                AgentsInstallProgressText.Text = "Installed. Checking this machine again...";
                _agentScanRan = false;
                await ScanAgentsAsync();

                if (!_model.AgentsFound)
                {
                    // The script said success but the re-scan still sees nothing - never leave the
                    // user with a silent no-op. Name the state and hand them the guide.
                    AgentsInstallErrorText.Text =
                        "The installer finished, but Claude Code was not found afterwards. Restart the Director and re-check, or use the install guide.";
                    AgentsInstallErrorPanel.IsVisible = true;
                }
            }
            else
            {
                AgentsInstallErrorText.Text = result.Message;
                AgentsInstallErrorPanel.IsVisible = true;
            }
        }
        catch (OperationCanceledException)
        {
            FileLog.Write("[FirstRunWizardDialog] BtnInstallClaude_Click: cancelled");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] BtnInstallClaude_Click FAILED: {ex.Message}");
            AgentsInstallErrorText.Text = $"Could not run the installer: {ex.Message}";
            AgentsInstallErrorPanel.IsVisible = true;
        }
        finally
        {
            AgentsInstallProgressText.IsVisible = false;
            AgentsInstallButton.IsEnabled = true;
            AgentsRecheckButton.IsEnabled = true;
            AgentsDeferButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// "I'll do this later": the honest deferral on the zero-agents state. The wizard proceeds, and
    /// the missing agent stays a carried to-do - the Done screen leads with installing an agent.
    /// </summary>
    private void BtnDeferAgents_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FirstRunWizardDialog] BtnDeferAgents_Click");
        _model.DeferAgents();
        Advance();
    }

    // ---- Tools step (the toolbelt, maintained automatically) ---------------------------------------

    /// <summary>
    /// Render the toolbelt from the embedded manifest catalog: every shipped tool with its one-line
    /// description and a Ready / Installing pill. All ready ends the story in one glance; anything
    /// still installing starts a light poll so the rows flip to Ready live while the user watches -
    /// the promise ("maintenance is our job now") demonstrated, not described.
    /// </summary>
    private async Task RefreshToolsScreenAsync()
    {
        try
        {
            var catalog = await Task.Run(() => new ToolCatalogService().GetCatalog());
            _toolsTotalCount = catalog.Count;
            _toolsReadyCount = catalog.Count(t => t.IsAvailable);

            ToolsTitle.Text = $"{_toolsTotalCount} tools, maintained for you";

            ToolsListPanel.Children.Clear();
            foreach (var tool in catalog)
            {
                ToolsListPanel.Children.Add(AgentRow(
                    tool.Name,
                    tool.Description,
                    tool.IsAvailable ? "Ready" : "Installing...",
                    ready: tool.IsAvailable));
            }

            if (_toolsReadyCount == _toolsTotalCount)
            {
                ToolsStatusText.Text = $"All {_toolsTotalCount} tools are installed and up to date.";
                ToolsStatusText.Foreground = Brush("#1A7F37");
                StopToolsPoll();
            }
            else
            {
                ToolsStatusText.Text =
                    $"{_toolsReadyCount} of {_toolsTotalCount} ready - DevThrottle is installing the rest now. It finishes on its own; you don't have to wait.";
                ToolsStatusText.Foreground = Brush("#0066B8");
                StartToolsPoll();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] RefreshToolsScreenAsync FAILED: {ex.Message}");
            ToolsStatusText.Text = $"Could not read the tool catalog: {ex.Message}";
            ToolsStatusText.Foreground = Brush("#DC2626");
        }
    }

    private void StartToolsPoll()
    {
        if (_toolsPollTimer is not null) return;
        FileLog.Write("[FirstRunWizardDialog] StartToolsPoll");
        _toolsPollTimer = new global::Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _toolsPollTimer.Tick += (_, _) => _ = RefreshToolsScreenAsync();
        _toolsPollTimer.Start();
    }

    private void StopToolsPoll()
    {
        if (_toolsPollTimer is null) return;
        FileLog.Write("[FirstRunWizardDialog] StopToolsPoll");
        _toolsPollTimer.Stop();
        _toolsPollTimer = null;
    }

    private void BtnToolsDocs_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FirstRunWizardDialog] BtnToolsDocs_Click");
        try
        {
            Process.Start(new ProcessStartInfo("https://devthrottle.com/docs/tools") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] BtnToolsDocs_Click FAILED: {ex.Message}");
            ToolsStatusText.Text = "Could not open the browser. Visit devthrottle.com/docs/tools manually.";
        }
    }

    // ---- Code step (where your repositories live) --------------------------------------------------

    /// <summary>
    /// Populate the Code step: show any roots already registered (a re-run wizard tells the truth),
    /// then stream in verified suggestions from the shallow scout scan - never a full drive walk.
    /// </summary>
    private async Task ScanCodeFoldersAsync()
    {
        FileLog.Write("[FirstRunWizardDialog] ScanCodeFoldersAsync");
        _codeScanRan = true;
        try
        {
            await EnsureRootStoreLoadedAsync();

            // Roots that already exist (Settings, a previous run) appear first, marked Added.
            foreach (var root in _rootStore.Roots)
            {
                if (_codeAddedRoots.ContainsKey(root.Path)) continue;
                var count = await Task.Run(() => CodeFolderScout.CountRepos(root.Path));
                _codeAddedRoots[root.Path] = count;
                CodeSuggestionsPanel.Children.Add(CodeRow(root.Path, count, alreadyAdded: true));
            }
            UpdateCodeTotal();

            _codeScanCts?.Cancel();
            _codeScanCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // Progress<T> posts each suggestion onto the UI thread as the background sweep finds it.
            var progress = new Progress<CodeFolderSuggestion>(s =>
            {
                if (_codeAddedRoots.ContainsKey(s.Path)) return;
                if (CodeSuggestionsPanel.Children.OfType<Border>().Any(b => Equals(b.Tag as string, s.Path))) return;
                CodeSuggestionsPanel.Children.Add(CodeRow(s.Path, s.RepoCount, alreadyAdded: false));
            });

            await CodeFolderScout.ScanAsync(progress, _codeScanCts.Token);

            CodeScanStatusText.Text = CodeSuggestionsPanel.Children.Count > 0
                ? "That is everywhere we checked - add anything else below."
                : "No repositories found in the usual places. Browse to where your code lives.";
        }
        catch (OperationCanceledException)
        {
            FileLog.Write("[FirstRunWizardDialog] ScanCodeFoldersAsync: scan cancelled/timed out");
            CodeScanStatusText.Text = "That is everywhere we checked - add anything else below.";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] ScanCodeFoldersAsync FAILED: {ex.Message}");
            CodeScanStatusText.Text = $"Folder scan failed: {ex.Message}";
        }
    }

    private async Task EnsureRootStoreLoadedAsync()
    {
        if (_rootStoreLoaded) return;
        await Task.Run(_rootStore.Load);
        _rootStoreLoaded = true;
    }

    /// <summary>One suggestion row: the folder, its verified repo count, and Add (or the Added pill).</summary>
    private Border CodeRow(string path, int repoCount, bool alreadyAdded)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = path,
            Foreground = Brush("#16181D"),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = repoCount == 1 ? "1 git repository found" : $"{repoCount} git repositories found",
            Foreground = Brush("#8A909A"),
            FontSize = 11,
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        global::Avalonia.Controls.Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        Control action;
        if (alreadyAdded)
        {
            action = AddedPill();
        }
        else
        {
            var addButton = new Button
            {
                Content = "Add",
                Classes = { "dialogButton" },
                VerticalAlignment = VerticalAlignment.Center,
            };
            addButton.Click += async (_, _) => await AddCodeRootAsync(path, repoCount, addButton);
            action = addButton;
        }
        global::Avalonia.Controls.Grid.SetColumn(action, 1);
        grid.Children.Add(action);

        return new Border
        {
            Tag = path,
            Background = Brush("#FFFFFF"),
            BorderBrush = Brush("#E6E8EC"),
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius = new global::Avalonia.CornerRadius(10),
            Padding = new global::Avalonia.Thickness(16, 13),
            Child = grid,
        };
    }

    private static Border AddedPill() => new()
    {
        Background = Brush("#E5F3E9"),
        CornerRadius = new global::Avalonia.CornerRadius(999),
        Padding = new global::Avalonia.Thickness(10, 3),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = "Added",
            Foreground = Brush("#1A7F37"),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
        },
    };

    /// <summary>Register a base folder in the same roots store the board and New Session read.</summary>
    private async Task AddCodeRootAsync(string path, int repoCount, Button addButton)
    {
        FileLog.Write($"[FirstRunWizardDialog] AddCodeRootAsync: {path}");
        try
        {
            addButton.IsEnabled = false;
            await EnsureRootStoreLoadedAsync();

            var duplicate = _rootStore.Roots.Any(r =>
                string.Equals(System.IO.Path.GetFullPath(r.Path), System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            if (!duplicate)
            {
                await Task.Run(() => _rootStore.Add(new RootDirectoryConfig
                {
                    Label = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar)),
                    Path = path,
                }));
            }

            _codeAddedRoots[path] = repoCount;
            SwapCodeRowActionToAdded(path);
            UpdateCodeTotal();
            if (_model.Current == WizardStep.Code)
                PrimaryButton.Content = "Looks right";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] AddCodeRootAsync FAILED: {ex.Message}");
            addButton.IsEnabled = true;
        }
    }

    /// <summary>Replace a row's Add button with the Added pill after a successful add.</summary>
    private void SwapCodeRowActionToAdded(string path)
    {
        var row = CodeSuggestionsPanel.Children.OfType<Border>().FirstOrDefault(b => Equals(b.Tag as string, path));
        if (row?.Child is not Grid grid) return;
        var button = grid.Children.OfType<Button>().FirstOrDefault();
        if (button is null) return;
        grid.Children.Remove(button);
        var pill = AddedPill();
        global::Avalonia.Controls.Grid.SetColumn(pill, 1);
        grid.Children.Add(pill);
    }

    /// <summary>The proof number: repositories across every added folder.</summary>
    private void UpdateCodeTotal()
    {
        var total = _codeAddedRoots.Values.Sum();
        CodeTotalPanel.IsVisible = total > 0;
        CodeTotalCount.Text = total.ToString();
    }

    private async void BtnBrowseCodeFolder_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FirstRunWizardDialog] BtnBrowseCodeFolder_Click");
        try
        {
            var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the folder where you keep your repositories",
                AllowMultiple = false,
            });
            if (result.Count == 0) return;

            // A picked SINGLE repository resolves to its parent (the roots list holds base folders;
            // the monitor lists a root's children).
            var picked = result[0].Path.LocalPath;
            var (path, count) = await Task.Run(() =>
            {
                var resolved = CodeFolderScout.ResolveBrowsedFolder(picked);
                return (resolved, CodeFolderScout.CountRepos(resolved));
            });

            if (_codeAddedRoots.ContainsKey(path)) return;

            var existingRow = CodeSuggestionsPanel.Children.OfType<Border>().FirstOrDefault(b => Equals(b.Tag as string, path));
            if (existingRow is not null)
            {
                // It was already suggested - adding it is what the user meant.
                var button = (existingRow.Child as Grid)?.Children.OfType<Button>().FirstOrDefault();
                if (button is not null)
                    await AddCodeRootAsync(path, count, button);
                return;
            }

            CodeSuggestionsPanel.Children.Add(CodeRow(path, count, alreadyAdded: true));
            await AddCodeRootAsync(path, count, new Button());
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] BtnBrowseCodeFolder_Click FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// "I don't have code on this machine yet": acknowledge the clean-machine case and move on. It
    /// writes nothing and creates nothing - a genuinely empty machine is a normal state, not a step
    /// the user failed.
    /// </summary>
    private void BtnNoCodeYet_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FirstRunWizardDialog] BtnNoCodeYet_Click");
        _codeNoneOnThisMachine = true;
        CodeNoneLink.IsVisible = false;
        CodeNoneAckText.IsVisible = true;
        Advance();
    }

    // ---- Screenshots step --------------------------------------------------------------------------

    /// <summary>
    /// Detect where this machine's screenshots land (Windows known folder / OneDrive / Pictures on
    /// Windows 10 and 11; the screencapture setting or Desktop on macOS) and present the best
    /// answer with its provenance and image count as proof.
    /// </summary>
    private async Task DetectScreenshotsForWizardAsync()
    {
        FileLog.Write("[FirstRunWizardDialog] DetectScreenshotsForWizardAsync");
        _shotsDetectRan = true;
        try
        {
            var best = await Task.Run(() => ScreenshotLocator.DetectCandidates().FirstOrDefault());
            if (best is not null)
            {
                var count = await Task.Run(() => ScreenshotLocator.CountImages(best.Path));
                await SetScreenshotsFolderAsync(best.Path, best.Provenance, count);
            }
            else
            {
                ShotsPathText.Text = "No screenshots folder found";
                ShotsProvenanceText.Text =
                    "We could not detect where your screenshots go. Browse to the folder, take a screenshot and we'll find where it lands - or just continue; you can set this any time in Settings.";
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] DetectScreenshotsForWizardAsync FAILED: {ex.Message}");
            ShotsPathText.Text = "No screenshots folder found";
            ShotsProvenanceText.Text = $"Detection failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Adopt a folder as the screenshots choice and render its provenance, proof line, and thumbnails.
    /// Returns once the thumbnails are drawn (internal so the strip is provable without driving the UI).
    /// </summary>
    internal Task SetScreenshotsFolderAsync(string path, string provenance, int imageCount)
    {
        _shotsSelectedPath = path;
        ShotsPathText.Text = path;
        var proof = imageCount switch
        {
            0 => "no images in it yet",
            1 => "1 image in it",
            _ => $"{imageCount} images in it",
        };
        ShotsProvenanceText.Text = $"{provenance} - {proof}.";
        if (_model.Current == WizardStep.Screenshots)
            PrimaryButton.Content = "Use this folder";

        // A count is a claim; the thumbnails are the evidence. Drawn off the UI thread, so a folder
        // with hundreds of images does not stall the step.
        ShotsPreviewStrip.IsVisible = false;
        return LoadScreenshotPreviewsAsync(path);
    }

    /// <summary>
    /// Draw the newest few images from <paramref name="path"/> under the proof line, so confirming the
    /// folder is a matter of recognising your own screenshots rather than trusting a number. A folder
    /// with no images simply shows no strip. Late results for a folder the user has already changed
    /// away from are dropped.
    /// </summary>
    private async Task LoadScreenshotPreviewsAsync(string path)
    {
        const int MaxPreviews = 4;
        try
        {
            var bitmaps = await Task.Run(() => Directory.EnumerateFiles(path)
                .Where(ScreenshotLocator.IsImageFile)
                .OrderByDescending(File.GetLastWriteTime)
                .Take(MaxPreviews)
                .Select(LoadPreviewThumbnail)
                .Where(b => b is not null)
                .Select(b => b!)
                .ToList());

            // The user may have browsed to another folder while this was decoding - that folder's own
            // load owns the strip now.
            if (!string.Equals(_shotsSelectedPath, path, StringComparison.OrdinalIgnoreCase))
                return;

            ShotsPreviewRow.Children.Clear();
            foreach (var bitmap in bitmaps)
            {
                ShotsPreviewRow.Children.Add(new Border
                {
                    Background = Brush("#F5F6F8"),
                    BorderBrush = Brush("#E6E8EC"),
                    BorderThickness = new global::Avalonia.Thickness(1),
                    CornerRadius = new global::Avalonia.CornerRadius(8),
                    ClipToBounds = true,
                    Child = new Image
                    {
                        Source = bitmap,
                        Height = 78,
                        Stretch = Stretch.Uniform,
                    },
                });
            }

            ShotsPreviewStrip.IsVisible = ShotsPreviewRow.Children.Count > 0;
            FileLog.Write($"[FirstRunWizardDialog] LoadScreenshotPreviewsAsync: {ShotsPreviewRow.Children.Count} preview(s) from {path}");
        }
        catch (Exception ex)
        {
            // The folder is the point of this step, not the preview - a folder we cannot read still
            // reports its path and provenance above, so the step stays usable.
            FileLog.Write($"[FirstRunWizardDialog] LoadScreenshotPreviewsAsync FAILED for {path}: {ex.Message}");
        }
    }

    /// <summary>Decode one thumbnail at preview height. Null for a file that is not a readable image.</summary>
    private static global::Avalonia.Media.Imaging.Bitmap? LoadPreviewThumbnail(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            return global::Avalonia.Media.Imaging.Bitmap.DecodeToHeight(stream, 156);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] LoadPreviewThumbnail skipped {file}: {ex.Message}");
            return null;
        }
    }

    private async void BtnBrowseScreenshots_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FirstRunWizardDialog] BtnBrowseScreenshots_Click");
        try
        {
            var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select your screenshots folder",
                AllowMultiple = false,
            });
            if (result.Count > 0)
            {
                CancelScreenshotWatch();
                var path = result[0].Path.LocalPath;
                var count = await Task.Run(() => ScreenshotLocator.CountImages(path));
                await SetScreenshotsFolderAsync(path, "Chosen by you", count);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] BtnBrowseScreenshots_Click FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// The certainty trick: watch every plausible screenshot location, ask the user to press their
    /// normal shortcut, and adopt whichever folder the new image lands in. Works the same on
    /// Windows 10, Windows 11, and macOS because it observes the OS instead of guessing.
    /// </summary>
    private async void BtnWatchScreenshot_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FirstRunWizardDialog] BtnWatchScreenshot_Click");
        try
        {
            ShotsWatchIdle.IsVisible = false;
            ShotsWatchActive.IsVisible = true;
            ShotsWatchText.Text = OperatingSystem.IsMacOS()
                ? "Press Shift+Cmd+3 (or your usual screenshot shortcut) now - we are watching for the new file..."
                : "Press Win+PrtScn (or your usual screenshot shortcut) now - we are watching for the new file...";

            _shotsWatchCts?.Cancel();
            _shotsWatchCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

            var roots = await Task.Run(() => ScreenshotCaptureWatcher.WatchRoots());
            if (roots.Count == 0)
            {
                ShotsWatchText.Text = "No folders to watch on this machine - browse to the folder instead.";
                return;
            }

            var landed = await new ScreenshotCaptureWatcher().WaitForNewScreenshotAsync(roots, _shotsWatchCts.Token);
            if (landed is not null)
            {
                var count = await Task.Run(() => ScreenshotLocator.CountImages(landed));
                await SetScreenshotsFolderAsync(landed, "Detected from the screenshot you just took", count);
            }
            CancelScreenshotWatch();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] BtnWatchScreenshot_Click FAILED: {ex.Message}");
            CancelScreenshotWatch();
        }
    }

    private void BtnCancelWatchScreenshot_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FirstRunWizardDialog] BtnCancelWatchScreenshot_Click");
        CancelScreenshotWatch();
    }

    /// <summary>End any live watch and restore the idle affordance.</summary>
    private void CancelScreenshotWatch()
    {
        _shotsWatchCts?.Cancel();
        _shotsWatchCts = null;
        if (ShotsWatchActive.IsVisible)
        {
            ShotsWatchActive.IsVisible = false;
            ShotsWatchIdle.IsVisible = true;
        }
    }

    /// <summary>Persist the confirmed folder as the screenshots source (same key Settings writes).</summary>
    private async Task SaveScreenshotsFolderAsync()
    {
        if (_shotsSelectedPath is null)
        {
            FileLog.Write("[FirstRunWizardDialog] SaveScreenshotsFolderAsync: nothing selected");
            return;
        }

        await ConfirmScreenshotsFolderAsync(_shotsSelectedPath);
    }

    /// <summary>
    /// Write the folder to config AND re-point the live screenshots panel at it, in that order.
    /// Writing alone is not enough: the panel resolved its folder once at startup, so without this
    /// the user finishes the Screenshots step and the panel still shows the old (on a fresh install,
    /// empty) folder until the next restart - the step promises immediate value and delivered none.
    ///
    /// Internal so the write-then-reload seam is provable without driving the wizard's UI.
    /// </summary>
    internal async Task ConfirmScreenshotsFolderAsync(string path)
    {
        FileLog.Write($"[FirstRunWizardDialog] SaveScreenshotsFolderAsync: {path}");
        await Task.Run(() => CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["screenshots"] = new JsonObject { ["source_directory"] = path },
        }));

        if (_reloadScreenshots is not null)
        {
            await _reloadScreenshots();
            FileLog.Write("[FirstRunWizardDialog] SaveScreenshotsFolderAsync: screenshots panel reloaded");
        }
    }

    // ---- Welcome: founder note ----------------------------------------------------------------------

    private void BtnFounderMore_Click(object? sender, RoutedEventArgs e)
    {
        FounderMoreText.IsVisible = !FounderMoreText.IsVisible;
        FounderMoreLink.Content = FounderMoreText.IsVisible ? "Show less" : "Read more";
    }

    // ---- Gateway step (native, hosted-first) -------------------------------------------------------

    /// <summary>Paint the three choice cards and the primary CTA from the current selection.</summary>
    private void RefreshGatewayChoiceUi()
    {
        // Card selection visuals: the chosen card carries the accent border + tint; the others rest.
        StyleGatewayCard(GatewayHostedCard, _gatewayChoice == GatewayChoice.Hosted, emphasized: true);
        StyleGatewayCard(GatewaySelfHostCard, _gatewayChoice == GatewayChoice.SelfHost, emphasized: false);
        StyleGatewayCard(GatewayNotNowCard, _gatewayChoice == GatewayChoice.NotNow, emphasized: false);

        PrimaryButton.Content = _gatewayConnected
            ? "Continue"
            : _gatewayChoice switch
            {
                GatewayChoice.Hosted => "Sign in and connect",
                GatewayChoice.SelfHost => "Set up self-hosted",
                _ => "Continue without a gateway",
            };
        PrimaryButton.IsEnabled = true;
    }

    private static void StyleGatewayCard(Border card, bool selected, bool emphasized)
    {
        card.BorderBrush = Brush(selected ? "#0066B8" : "#E6E8EC");
        card.BorderThickness = new global::Avalonia.Thickness(selected && emphasized ? 2 : selected ? 1.5 : 1);
        card.Background = Brush(selected ? "#F2F8FD" : "#FFFFFF");
    }

    private void SelectGatewayChoice(GatewayChoice choice)
    {
        FileLog.Write($"[FirstRunWizardDialog] SelectGatewayChoice: {choice}");
        _gatewayChoice = choice;
        RefreshGatewayChoiceUi();
    }

    private void GatewayHostedCard_Pressed(object? sender, PointerPressedEventArgs e) => SelectGatewayChoice(GatewayChoice.Hosted);
    private void GatewaySelfHostCard_Pressed(object? sender, PointerPressedEventArgs e) => SelectGatewayChoice(GatewayChoice.SelfHost);
    private void GatewayNotNowCard_Pressed(object? sender, PointerPressedEventArgs e) => SelectGatewayChoice(GatewayChoice.NotNow);

    /// <summary>Show exactly one of the gateway step's sub-views (choice / connecting / connected / failed / advanced).</summary>
    private void ShowGatewayView(Control view)
    {
        GatewayChoiceView.IsVisible = view == GatewayChoiceView;
        GatewayConnectingView.IsVisible = view == GatewayConnectingView;
        GatewayConnectedView.IsVisible = view == GatewayConnectedView;
        GatewayFailedView.IsVisible = view == GatewayFailedView;
        GatewayAdvancedView.IsVisible = view == GatewayAdvancedView;
    }

    /// <summary>
    /// The hosted sign-in + enroll: the SAME transaction the shared gateway panel and the CLI's
    /// hosted enroll run (browser account sign-in; the hosted Gateway mints this machine's device key;
    /// url + key persist on verified success ONLY). The wizard renders its own light-weight progress,
    /// success, and failure states - it never embeds the old panel for this path.
    /// </summary>
    private async Task StartHostedEnrollAsync()
    {
        FileLog.Write("[FirstRunWizardDialog] StartHostedEnrollAsync");
        ShowGatewayView(GatewayConnectingView);
        PrimaryButton.IsEnabled = false;

        var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
        var directorId = host?.DirectorId;
        if (host is null || directorId is null)
        {
            ShowGatewayFailure("The Director is still starting, so it cannot connect yet. Give it a moment, then try again.");
            return;
        }

        _hostedEnrollCts?.Cancel();
        _hostedEnrollCts = new CancellationTokenSource();
        var ct = _hostedEnrollCts.Token;

        try
        {
            var result = await new GatewayAccountEnrollRunner()
                .SignInAndEnrollHostedAsync(directorId, Environment.MachineName, ct);

            if (!result.Success)
            {
                FileLog.Write($"[FirstRunWizardDialog] hosted enroll failed: {result.ErrorMessage}");
                ShowGatewayFailure(result.ErrorMessage ?? "Could not sign in and join the hosted gateway.");
                return;
            }

            // The verified hosted url + device key are persisted; re-apply so THIS run authenticates
            // with the new credential immediately (not just after a restart).
            await host.ReapplyGatewayAsync();

            _gatewayConnected = true;
            GatewayConnectedHostText.Text = $"This machine is enrolled with {GatewayConfig.Load().Url}";
            ShowGatewayView(GatewayConnectedView);
            FileLog.Write("[FirstRunWizardDialog] hosted enroll succeeded");
        }
        catch (OperationCanceledException)
        {
            FileLog.Write("[FirstRunWizardDialog] hosted enroll cancelled");
            ShowGatewayView(GatewayChoiceView);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] hosted enroll error: {ex.Message}");
            ShowGatewayFailure($"Could not sign in and join the hosted gateway: {ex.Message}");
        }
        finally
        {
            RefreshGatewayChoiceUi();
        }
    }

    private void ShowGatewayFailure(string message)
    {
        GatewayFailText.Text = message;
        ShowGatewayView(GatewayFailedView);
        PrimaryButton.IsEnabled = true;
    }

    private void GatewayCancelSignIn_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FirstRunWizardDialog] GatewayCancelSignIn_Click");
        _hostedEnrollCts?.Cancel();
    }

    private void GatewayTryAgain_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FirstRunWizardDialog] GatewayTryAgain_Click");
        ShowGatewayView(GatewayChoiceView);
        RefreshGatewayChoiceUi();
    }

    private void GatewayBackToOptions_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FirstRunWizardDialog] GatewayBackToOptions_Click");
        ShowGatewayView(GatewayChoiceView);
        RefreshGatewayChoiceUi();
    }

    /// <summary>The advanced self-hosted path: embed the existing join-an-existing-gateway flow.</summary>
    private void ShowGatewayAdvanced()
    {
        FileLog.Write("[FirstRunWizardDialog] ShowGatewayAdvanced");
        if (_gatewayPanel is null)
        {
            _gatewayPanel = new Controls.GatewayConnectionPanel(GatewayPanelStep.Connect);
            _gatewayPanel.SkipRequested += (_, behavior) =>
            {
                // "Not now" inside the panel advances the wizard to the next step (the shell owns the
                // whole-wizard completion, so a local-only choice here is just "move on").
                if (behavior == GatewaySkipBehavior.CompleteOnboardingLocalOnly)
                {
                    FileLog.Write("[FirstRunWizardDialog] gateway panel requested local-only; advancing");
                    if (_model.Current == WizardStep.Gateway)
                        Advance();
                }
            };
            GatewayHost.Child = _gatewayPanel;
        }
        ShowGatewayView(GatewayAdvancedView);
        PrimaryButton.Content = "Continue";
    }

    // ---- Done receipt ------------------------------------------------------------------------------

    private void BuildDoneReceipt()
    {
        DoneReceiptPanel.Children.Clear();

        // Agents row.
        var addedNames = _agentSuggestions
            .Where(s => s.Found)
            .Select(s => s.DisplayName)
            .ToList();
        if (addedNames.Count > 0)
            DoneReceiptPanel.Children.Add(ReceiptRow(
                $"{addedNames.Count} {(addedNames.Count == 1 ? "agent" : "agents")} ready",
                string.Join(", ", addedNames), done: true));
        else
            DoneReceiptPanel.Children.Add(ReceiptRow(
                "No agent yet",
                _model.AgentsDeferred
                    ? "You chose to do this later - the button below installs one now"
                    : "Add one from Settings > Agents",
                done: false));

        // Code row.
        var repoTotal = _codeAddedRoots.Values.Sum();
        if (repoTotal > 0)
            DoneReceiptPanel.Children.Add(ReceiptRow(
                repoTotal == 1 ? "1 repository" : $"{repoTotal} repositories",
                string.Join(", ", _codeAddedRoots.Keys), done: true));
        else
            DoneReceiptPanel.Children.Add(ReceiptRow(
                "No code folders yet",
                _codeNoneOnThisMachine
                    ? "Add a folder from the Repositories view once you have code on this machine"
                    : "Add them from the Repositories view to get repository suggestions",
                done: false));

        // Screenshots row.
        if (_shotsSelectedPath is not null)
            DoneReceiptPanel.Children.Add(ReceiptRow("Screenshots folder set", _shotsSelectedPath, done: true));
        else
            DoneReceiptPanel.Children.Add(ReceiptRow(
                "Screenshots folder", "Set it any time in Settings to drag screenshots straight to agents", done: false));

        // Gateway row.
        var gatewayUrl = GatewayConfig.Load().Url;
        if (!string.IsNullOrWhiteSpace(gatewayUrl))
            DoneReceiptPanel.Children.Add(ReceiptRow("Gateway connected", gatewayUrl, done: true));
        else
            DoneReceiptPanel.Children.Add(ReceiptRow(
                "No gateway", "Connect one from Settings for phone access and your morning report", done: false));

        // Tools row (only when the Tools screen was seen, so the numbers are real).
        if (_toolsTotalCount > 0)
        {
            DoneReceiptPanel.Children.Add(_toolsReadyCount == _toolsTotalCount
                ? ReceiptRow($"{_toolsTotalCount} tools ready", "Installed and kept current automatically", done: true)
                : ReceiptRow("Tools installing", "Finishes on its own in the background", done: false));
        }

        // Browsers row: a pointer, not a task. Browser setup lives in the left rail (the Browsers
        // group above Repositories) where it is always one click away - the wizard just plants it.
        DoneReceiptPanel.Children.Add(ReceiptRow(
            "Browsers", "Give agents a signed-in browser any time - the Browsers group in the left rail", done: false));

        // Morning report row. There is no longer a frequency question in the wizard - the report is
        // one person, one email, and asking about it once per machine could never reconcile (issue
        // #996) - so this row states the ONE default rather than reporting a choice back. It is now
        // also the only place onboarding says the email is coming at all, which is why the row stays.
        //
        // Two claims here have to be true and stay true. The report travels through the gateway, so
        // with none connected it must not read Done two rows under "No gateway". And the time is
        // 7:00 EASTERN, not "your time" - that is when the sender runs, and a receipt that invents a
        // local hour is wrong for everyone outside one timezone.
        DoneReceiptPanel.Children.Add(!string.IsNullOrWhiteSpace(gatewayUrl)
            ? ReceiptRow("Morning report", "Every morning at 7:00 Eastern - the email says how to change or stop it", done: true)
            : ReceiptRow(
                "Morning report", "Every morning at 7:00 Eastern - once a gateway is connected",
                done: false, pillText: "Waiting for a gateway"));
    }

    private static Border ReceiptRow(string name, string sub, bool done, string? pillText = null)
    {
        var pill = new Border
        {
            Background = Brush(done ? "#E5F3E9" : "#F5F6F8"),
            CornerRadius = new global::Avalonia.CornerRadius(999),
            Padding = new global::Avalonia.Thickness(10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = pillText ?? (done ? "Done" : "Later"),
                Foreground = Brush(done ? "#1A7F37" : "#8A909A"),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
            },
        };

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = Brush("#16181D"),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = sub,
            Foreground = Brush("#8A909A"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        global::Avalonia.Controls.Grid.SetColumn(text, 0);
        global::Avalonia.Controls.Grid.SetColumn(pill, 1);
        grid.Children.Add(text);
        grid.Children.Add(pill);

        return new Border
        {
            Padding = new global::Avalonia.Thickness(16, 12),
            BorderBrush = Brush("#E6E8EC"),
            BorderThickness = new global::Avalonia.Thickness(0, 0, 0, 1),
            Child = grid,
        };
    }

    // ---- Finish + marker ---------------------------------------------------------------------------

    /// <summary>
    /// Write the completion marker and close. Internal so a UI test can drive the real finish seam and
    /// assert the marker was written.
    /// </summary>
    internal async Task FinishAsync(bool wantsNewSession)
    {
        FileLog.Write($"[FirstRunWizardDialog] FinishAsync: wantsNewSession={wantsNewSession}");
        await Task.Run(FirstRunWizardModel.MarkComplete);
        _marked = true;
        WantsNewSession = wantsNewSession;
        Close(true);
    }

    /// <summary>
    /// A plain window close (the title-bar X) still counts as leaving the wizard, so write the marker
    /// if a finish path did not already - the wizard must never nag twice.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _hostedEnrollCts?.Cancel();
        _claudeInstallCts?.Cancel();
        _shotsWatchCts?.Cancel();
        _codeScanCts?.Cancel();
        StopToolsPoll();
        if (!_marked)
        {
            FileLog.Write("[FirstRunWizardDialog] OnClosed: writing completion marker (window closed without finishing)");
            try
            {
                FirstRunWizardModel.MarkComplete();
                _marked = true;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[FirstRunWizardDialog] OnClosed marker write FAILED: {ex.Message}");
            }
        }
        base.OnClosed(e);
    }

    // ---- Test hooks --------------------------------------------------------------------------------

    /// <summary>The wizard's current step, so a UI test can assert navigation moved as expected.</summary>
    internal WizardStep CurrentStepForTests => _model.Current;

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}
