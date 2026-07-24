using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using CcDirector.Core.Onboarding;
using CcDirector.Core.Settings;
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

    // True once the completion marker has been written, so OnClosed does not write it twice.
    private bool _marked;

    /// <summary>
    /// True when the user chose "Start my first agent" on the Done screen, so the caller opens the
    /// New Session dialog after this wizard closes. False on the board link, whole-wizard skip, or a
    /// plain window close.
    /// </summary>
    public bool WantsNewSession { get; private set; }

    public FirstRunWizardDialog() : this(new AgentOptions()) { }

    public FirstRunWizardDialog(AgentOptions options)
    {
        FileLog.Write("[FirstRunWizardDialog] Constructor: initializing");
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // The steps present in THIS release: the two bookends the shell ships, plus the interim
        // Agents and Gateway steps that reuse existing controls. Code, Screenshots and Morning report
        // are absent until their own issues land; the model tolerates the shorter list.
        _model = new FirstRunWizardModel(new[]
        {
            WizardStep.Welcome,
            WizardStep.Agents,
            WizardStep.Gateway,
            WizardStep.Done,
        });

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
        GatewayPanel.IsVisible = step == WizardStep.Gateway;
        DonePanel.IsVisible = step == WizardStep.Done;

        RefreshDots();

        // Back link everywhere except the first step.
        BackButton.IsVisible = !_model.IsFirst;

        // Defaults: the per-step configuration below overrides what it needs.
        PrimaryButton.IsVisible = false;
        StepSkipLink.IsVisible = false;
        FooterNote.IsVisible = false;

        switch (step)
        {
            case WizardStep.Welcome:
                // Primary ("Set me up") and the quiet whole-wizard skip live in the content panel.
                FooterNote.Text = "Takes about 3 minutes. You can change everything later in Settings.";
                FooterNote.IsVisible = true;
                break;

            case WizardStep.Agents:
                PrimaryButton.Content = "Use these agents";
                PrimaryButton.IsVisible = true;
                ConfigureStepSkip();
                if (!_agentScanRan)
                    _ = ScanAgentsAsync();
                break;

            case WizardStep.Gateway:
                PrimaryButton.IsVisible = true;
                ConfigureStepSkip();
                RefreshGatewayChoiceUi();
                break;

            case WizardStep.Done:
                // Primary and the board link live in the content panel. With no agent on the machine
                // the carried to-do leads: the button routes back to the Agents step and its installer
                // instead of promising a session that cannot start.
                DoneStartButton.Content = _model.AgentsFound ? "Start my first agent" : "Install an agent";
                FooterNote.Text = "Everything here can be changed in Settings.";
                FooterNote.IsVisible = true;
                BuildDoneReceipt();
                break;
        }
    }

    /// <summary>Show or hide the footer "Skip this step" link per the model's skip rule for the current step.</summary>
    private void ConfigureStepSkip()
    {
        StepSkipLink.IsVisible = _model.CanSkipCurrent;
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
    private void BtnStepSkip_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write($"[FirstRunWizardDialog] BtnStepSkip_Click: step={_model.Current}");
        if (!_model.CanSkipCurrent)
        {
            FileLog.Write("[FirstRunWizardDialog] BtnStepSkip_Click: step is unskippable; ignoring");
            return;
        }
        Advance();
    }

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

            // Zero agents: the one step the user cannot skip. Hide the skip link, block Continue, and
            // offer the in-place install (or the deferral); otherwise allow both and hide the actions.
            AgentsEmptyActions.IsVisible = !anyFound;
            PrimaryButton.IsEnabled = anyFound;
            ConfigureStepSkip();

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

        // Gateway row.
        var gatewayUrl = GatewayConfig.Load().Url;
        if (!string.IsNullOrWhiteSpace(gatewayUrl))
            DoneReceiptPanel.Children.Add(ReceiptRow("Gateway connected", gatewayUrl, done: true));
        else
            DoneReceiptPanel.Children.Add(ReceiptRow(
                "No gateway", "Connect one from Settings for phone access and your morning report", done: false));
    }

    private static Border ReceiptRow(string name, string sub, bool done)
    {
        var pill = new Border
        {
            Background = Brush(done ? "#E5F3E9" : "#F5F6F8"),
            CornerRadius = new global::Avalonia.CornerRadius(999),
            Padding = new global::Avalonia.Thickness(10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = done ? "Done" : "Later",
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
