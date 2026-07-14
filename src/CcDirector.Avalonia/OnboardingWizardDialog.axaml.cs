using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Configuration;
using CcDirector.Core.Onboarding;
using CcDirector.Core.Settings;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// First-run onboarding wizard (issue #370, lean v1). Takes a fresh user from launch to a working
/// agent in three steps: 1) detect, enter and test the Gateway URL, 2) confirm a Claude Code agent
/// is available on PATH (with install guidance when it is not), 3) confirm the Director is ready and
/// route to the New Session dialog.
///
/// The logic lives UI-free in <see cref="OnboardingModel"/>; this dialog is the thin Avalonia shell.
/// The three steps are panels in one cell toggled by IsVisible, navigated with Back/Next. The gateway
/// step auto-scans for a gateway when it first appears and offers a Detect button to re-scan, reusing
/// the same <see cref="SettingsDetectionService"/> the Settings gateway tab uses (DetectGatewayAsync
/// for the scan, TestGatewayAsync for a typed URL). All gateway work runs async with a spinner so the
/// UI never blocks. On finish (or skip) the onboarding-complete marker is written so the wizard never
/// auto-opens again.
/// </summary>
public partial class OnboardingWizardDialog : Window
{
    private const int StepGateway = 0;
    private const int StepAgent = 1;
    private const int StepDone = 2;
    private const int TotalSteps = 3;

    private readonly AgentOptions _options;
    private readonly OnboardingModel _model = new(new ToolDetectionService());

    private int _currentStep = StepGateway;
    private bool _gatewayConnected;
    private bool _agentAvailable;

    /// <summary>
    /// True when the user chose "Create first session" on the final step, so the caller should open
    /// the New Session dialog after this wizard closes. False when the user clicked Finish or Skip.
    /// </summary>
    public bool WantsNewSession { get; private set; }

    public OnboardingWizardDialog() : this(new AgentOptions()) { }

    public OnboardingWizardDialog(AgentOptions options)
    {
        FileLog.Write("[OnboardingWizardDialog] Constructor: initializing");
        _options = options ?? throw new ArgumentNullException(nameof(options));
        InitializeComponent();

        // The Gateway step IS the one reusable connection panel now (Phase 4, design spec section 8). It
        // auto-scans on show, connects (which IS the test), and signs in - so the wizard no longer has its
        // own URL box / Detect / Test. Next advances once the panel reports Connected.
        var panel = Controls.GatewayConnectionPanel.CreateForCurrentState();
        panel.ConnectionVerified += (_, _) => OnGatewayConnected();
        GatewayStep.Child = panel;

        ShowStep(StepGateway);
    }

    /// <summary>The embedded panel proved the two-way connection. Record it and let the user proceed
    /// (they can finish signing in in the panel first, then click Next).</summary>
    private void OnGatewayConnected()
    {
        _gatewayConnected = true;
        FileLog.Write("[OnboardingWizardDialog] gateway connected (panel handshake verified)");
        ShowStepStatus("Connected to your Gateway. Click Next to continue.", error: false);
    }

    /// <summary>Switch the visible step panel and update the title, indicator, and navigation buttons.</summary>
    private void ShowStep(int step)
    {
        FileLog.Write($"[OnboardingWizardDialog] ShowStep: step={step}");
        _currentStep = step;

        GatewayStep.IsVisible = step == StepGateway;
        AgentStep.IsVisible = step == StepAgent;
        DoneStep.IsVisible = step == StepDone;

        StepIndicator.Text = $"Step {step + 1} of {TotalSteps}";
        StepStatusText.IsVisible = false;
        StepStatusText.Text = "";

        BackButton.IsVisible = step != StepGateway;

        switch (step)
        {
            case StepGateway:
                StepTitle.Text = "Connect to a Gateway";
                NextButton.Content = "Next";
                NextButton.IsVisible = true;
                break;

            case StepAgent:
                StepTitle.Text = "Check your agent";
                NextButton.Content = "Next";
                NextButton.IsVisible = true;
                _ = RunAgentCheckAsync();
                break;

            case StepDone:
                StepTitle.Text = "You are ready";
                NextButton.Content = "Finish";
                NextButton.IsVisible = true;
                BuildDoneSummary();
                break;
        }
    }

    private async void BtnNext_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write($"[OnboardingWizardDialog] BtnNext_Click: step={_currentStep}");
        try
        {
            switch (_currentStep)
            {
                case StepGateway:
                    AdvanceFromGateway();
                    break;

                case StepAgent:
                    ShowStep(StepDone);
                    break;

                case StepDone:
                    await FinishAsync(wantsNewSession: false);
                    break;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OnboardingWizardDialog] BtnNext_Click FAILED: {ex.Message}");
            ShowStepStatus($"Something went wrong: {ex.Message}", error: true);
        }
    }

    /// <summary>
    /// Gateway step Next (Phase 4): the embedded panel connects and writes gateway.url itself, so the
    /// wizard only checks that the two-way connection is proven before advancing. If it is not connected
    /// yet, nudge the user to connect in the panel (they can still leave via "Skip for now" without
    /// bricking first-run).
    /// </summary>
    private void AdvanceFromGateway()
    {
        var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
        var connected = _gatewayConnected
            || host?.GatewayMonitor?.Status == CcDirector.ControlApi.GatewayConnectionStatus.Connected;
        if (!connected)
        {
            FileLog.Write("[OnboardingWizardDialog] AdvanceFromGateway: not connected yet, nudging");
            ShowStepStatus("Connect to your Gateway above to continue, or use \"Skip for now\" to set it up later in Settings.", error: true);
            return;
        }

        FileLog.Write("[OnboardingWizardDialog] AdvanceFromGateway: connected; advancing to the agent step");
        ShowStep(StepAgent);
    }

    private void BtnBack_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write($"[OnboardingWizardDialog] BtnBack_Click: step={_currentStep}");
        if (_currentStep == StepAgent)
            ShowStep(StepGateway);
        else if (_currentStep == StepDone)
            ShowStep(StepAgent);
    }

    private async void BtnSkip_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[OnboardingWizardDialog] BtnSkip_Click");
        try
        {
            await FinishAsync(wantsNewSession: false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OnboardingWizardDialog] BtnSkip_Click FAILED: {ex.Message}");
            Close(false);
        }
    }

    /// <summary>Run the Claude Code availability check off the UI thread, then render the verdict.</summary>
    private async Task RunAgentCheckAsync()
    {
        FileLog.Write("[OnboardingWizardDialog] RunAgentCheckAsync");
        AgentCheckSpinner.IsVisible = true;
        SetAgentBadge("CHECKING", "#3A2A1B", "#F59E0B");
        AgentStatusMessage.Text = "Checking for Claude Code...";
        AgentInstallGuidance.IsVisible = false;
        try
        {
            var availability = await Task.Run(() => _model.CheckClaudeAvailable(_options));
            _agentAvailable = availability.IsAvailable;
            AgentStatusMessage.Text = availability.Message;

            if (availability.IsAvailable)
            {
                SetAgentBadge("AVAILABLE", "#1B3A2A", "#22C55E");
                AgentInstallGuidance.IsVisible = false;
            }
            else
            {
                SetAgentBadge("NOT FOUND", "#3A2A1B", "#F59E0B");
                AgentInstallGuidance.IsVisible = true;
            }

            FileLog.Write($"[OnboardingWizardDialog] RunAgentCheckAsync: available={availability.IsAvailable}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OnboardingWizardDialog] RunAgentCheckAsync FAILED: {ex.Message}");
            SetAgentBadge("ERROR", "#3A2A1B", "#F59E0B");
            AgentStatusMessage.Text = $"Could not check for Claude Code: {ex.Message}";
            AgentInstallGuidance.IsVisible = true;
            _agentAvailable = false;
        }
        finally
        {
            AgentCheckSpinner.IsVisible = false;
        }
    }

    private async void BtnCreateSession_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[OnboardingWizardDialog] BtnCreateSession_Click");
        try
        {
            await FinishAsync(wantsNewSession: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OnboardingWizardDialog] BtnCreateSession_Click FAILED: {ex.Message}");
            ShowStepStatus($"Something went wrong: {ex.Message}", error: true);
        }
    }

    private void BtnRecheckAgent_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[OnboardingWizardDialog] BtnRecheckAgent_Click");
        _ = RunAgentCheckAsync();
    }

    private void BtnInstallClaude_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[OnboardingWizardDialog] BtnInstallClaude_Click");
        try
        {
            Process.Start(new ProcessStartInfo(OnboardingModel.ClaudeInstallUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OnboardingWizardDialog] BtnInstallClaude_Click FAILED: {ex.Message}");
            ShowStepStatus($"Could not open the browser. Visit {OnboardingModel.ClaudeInstallUrl} manually.", error: true);
        }
    }

    private void BuildDoneSummary()
    {
        // The panel wrote gateway.url on connect (Phase 4); read it back for the summary line.
        var gatewayUrl = GatewayConfig.Load().Url;
        var gatewayPart = string.IsNullOrWhiteSpace(gatewayUrl)
            ? "No gateway is connected yet - connect one from Settings so this Director shows up there."
            : $"Connected to gateway {gatewayUrl}.";
        var agentPart = _agentAvailable
            ? "Claude Code is available."
            : "No agent was detected yet - install Claude Code, then add it from Settings.";
        DoneSummary.Text = $"{gatewayPart} {agentPart}";
    }

    /// <summary>Mark onboarding complete and close. Sets WantsNewSession so the caller can route to New Session.</summary>
    private async Task FinishAsync(bool wantsNewSession)
    {
        FileLog.Write($"[OnboardingWizardDialog] FinishAsync: wantsNewSession={wantsNewSession}");
        await Task.Run(OnboardingModel.MarkComplete);
        WantsNewSession = wantsNewSession;
        Close(true);
    }

    private void SetAgentBadge(string text, string background, string foreground)
    {
        AgentStatusBadgeText.Text = text;
        AgentStatusBadge.Background = new SolidColorBrush(Color.Parse(background));
        AgentStatusBadgeText.Foreground = new SolidColorBrush(Color.Parse(foreground));
    }

    private void ShowStepStatus(string text, bool error)
    {
        StepStatusText.Text = text;
        StepStatusText.IsVisible = true;
        StepStatusText.Foreground = error ? Brushes.IndianRed : Brushes.MediumSeaGreen;
    }
}
