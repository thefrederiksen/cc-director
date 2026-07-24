using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.GatewayConnection;
using CcDirector.Core.Onboarding;
using CcDirector.Core.Settings;
using CcDirector.Core.Utilities;

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
    private readonly FirstRunWizardModel _model;
    private readonly List<Ellipse> _dots = new();

    // Agents-step scan results, cached so accept and the Done receipt can read them without re-scanning.
    private IReadOnlyList<ToolDetectionSuggestion> _agentSuggestions = Array.Empty<ToolDetectionSuggestion>();
    private HashSet<AgentKind> _existingAgentTypes = new();
    private bool _agentScanRan;

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
                Width = 9,
                Height = 9,
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
                WizardDotState.Current => Brush("#007ACC"),
                WizardDotState.Past => Brush("#3B6EA5"),
                _ => Brush("#3C3C3C"),
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
                PrimaryButton.Content = "Continue";
                PrimaryButton.IsVisible = true;
                ConfigureStepSkip();
                EnsureGatewayPanel();
                break;

            case WizardStep.Done:
                // Primary ("Start my first agent") and the board link live in the content panel.
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
        FileLog.Write("[FirstRunWizardDialog] BtnStartFirstAgent_Click");
        try
        {
            await FinishAsync(wantsNewSession: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] BtnStartFirstAgent_Click FAILED: {ex.Message}");
        }
    }

    // ---- Agents step (interim: reuses the tool-detection scan) -------------------------------------

    private async Task ScanAgentsAsync()
    {
        FileLog.Write("[FirstRunWizardDialog] ScanAgentsAsync");
        AgentsStatusText.Text = "Scanning this machine for coding agents...";
        AgentsInstallGuidance.IsVisible = false;
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

            BuildAgentRows(suggestions, existing);

            var foundCount = suggestions.Count(s => s.Found);
            AgentsStatusText.Text = anyFound
                ? $"We found {foundCount} coding {(foundCount == 1 ? "agent" : "agents")}. These are ready to use - you can add more or change paths later in Settings."
                : "You need a coding agent. DevThrottle runs and supervises command-line coding agents, and we did not find one on this machine.";

            // Zero agents: the one step the user cannot skip. Hide the skip link, block Continue, and
            // show install guidance; otherwise allow both and hide the guidance.
            AgentsInstallGuidance.IsVisible = !anyFound;
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

    private void BuildAgentRows(IReadOnlyList<ToolDetectionSuggestion> suggestions, ISet<AgentKind> existing)
    {
        AgentsListPanel.Children.Clear();

        // Found (or already-added) agents first, each as its own Ready row; then a single summary row
        // for everything not installed, matching the mockup's found-state layout.
        foreach (var s in suggestions.Where(s => s.Found))
        {
            var alreadyAdded = existing.Contains(s.Tool);
            AgentsListPanel.Children.Add(AgentRow(
                s.DisplayName,
                alreadyAdded ? $"Already in your Agents list - {s.ResolvedPath}" : s.ResolvedPath,
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
            Background = Brush(ready ? "#1B3A2A" : "#3A2A1B"),
            CornerRadius = new global::Avalonia.CornerRadius(999),
            Padding = new global::Avalonia.Thickness(10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = pillText,
                Foreground = Brush(ready ? "#22C55E" : "#888888"),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
            },
        };

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = Brush(ready ? "#CCCCCC" : "#888888"),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = sub,
            Foreground = Brush("#888888"),
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
            Background = Brush("#1E1E1E"),
            BorderBrush = Brush("#3C3C3C"),
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius = new global::Avalonia.CornerRadius(6),
            Padding = new global::Avalonia.Thickness(14, 12),
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

    // ---- Gateway step (interim: reuses the shared connection panel) --------------------------------

    private void EnsureGatewayPanel()
    {
        if (_gatewayPanel is not null) return;

        FileLog.Write("[FirstRunWizardDialog] EnsureGatewayPanel: creating panel");
        _gatewayPanel = Controls.GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Onboarding);
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
                "No agent yet", "Add one from Settings > Agents", done: false));

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
            Background = Brush(done ? "#1B3A2A" : "#2A2A2A"),
            CornerRadius = new global::Avalonia.CornerRadius(999),
            Padding = new global::Avalonia.Thickness(10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = done ? "Done" : "Later",
                Foreground = Brush(done ? "#22C55E" : "#888888"),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
            },
        };

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = Brush("#CCCCCC"),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = sub,
            Foreground = Brush("#888888"),
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
            Padding = new global::Avalonia.Thickness(14, 11),
            BorderBrush = Brush("#2D2D2D"),
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
