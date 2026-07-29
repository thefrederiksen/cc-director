using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
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

    // A gateway that is ALREADY configured when the step opens. Read once, from the saved config, so
    // a re-run does not ask the user to enroll a machine that is already enrolled. Before this,
    // _gatewayConnected was a this-run-only flag and the step always opened on the choice cards
    // offering "Sign in and connect" - while the Done receipt, which does read the saved config,
    // printed "Gateway connected" two screens later. One wizard, two answers, same run.
    private bool _gatewayExistingChecked;
    private bool _gatewayWasAlreadyConnected;

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

    // When the wait for a still-missing tool began, and how long it may run before the screen stops
    // calling it "Installing" and calls it what it is. "Installing..." was the ONLY thing this screen
    // could say about an absent tool, so a tool that would never arrive kept that pill for ever while
    // the status line went on promising it would finish on its own.
    private DateTime? _toolsWaitStartedUtc;
    private bool _toolsRepairing;
    private bool _toolsStalled;
    private const int ToolsStallSeconds = 45;

    // Code step: the ROOT DIRECTORY store - the registered base folders the repository model scans.
    // It is NOT what New Session reads; New Session reads the repository registry and, since this
    // change, unions in the repositories the model found under these roots. (Two comments here used to
    // claim this store was "what the board and New Session read". It never was, and that false comment
    // is how a wizard receipt reading "12 repositories - Done" came to be followed by a New Session
    // dialog reading "No repositories yet".) Plus the folders registered this run (path -> repository
    // count, which drives the proof number) and the one-shot suggestion scan.
    private readonly RootDirectoryStore _rootStore = new();
    private bool _rootStoreLoaded;
    private readonly Dictionary<string, int> _codeAddedRoots = new(StringComparer.OrdinalIgnoreCase);
    private bool _codeScanRan;
    private CancellationTokenSource? _codeScanCts;

    // Set whenever this run registers or un-registers a root, so leaving the step can republish them
    // to the running application once instead of once per folder.
    private bool _codeRootsChanged;

    // Serializes writes to the roots file. The sweep publishes its finds in a burst - seven folders
    // within four milliseconds on a real machine - and each registration rewrites the whole
    // root-directories.json. Run concurrently they collide on the file and MOST OF THEM LOSE: an
    // unserialized version of this registered two of seven and logged "the process cannot access the
    // file" for the other five, which on screen looked simply like folders that were never found.
    private readonly SemaphoreSlim _codeStoreGate = new(1, 1);

    // Folders the user has explicitly REMOVED this run. Without this record, removing a folder only
    // deletes it from _codeAddedRoots - so a suggestion for the same path arriving later from the
    // still-running sweep looks brand new and is silently registered again. The user's opt-out has to
    // outlive the scan that is still producing results behind it.
    private readonly HashSet<string> _codeRejectedRoots = new(StringComparer.OrdinalIgnoreCase);

    // Every registration started by the sweep. The sweep's own completion says only that DISCOVERY
    // finished; the writes it queued behind the gate may still be running. The step must not report
    // itself done, and must not publish to the rest of the application, until these have landed.
    private readonly List<Task> _codePendingWrites = new();

    // Set when the user says there is no code on this machine yet (a new laptop). It changes nothing
    // on disk - it only stops the step, and the Done receipt, from reading as a failure.
    private bool _codeNoneOnThisMachine;

    // True once the completion marker has been written, so OnClosed does not write it twice.
    private bool _marked;

    // True from the moment the window closes. Several operations here await slow work - the tool
    // catalog read, a tool repair, a folder registration - and their continuations resume on the user
    // interface thread AFTER the window may have gone. Without this, a continuation could start a
    // fresh polling timer on a closed dialog and keep it alive, updating controls nobody can see.
    private volatile bool _closed;

    // True once the user has reached the end of the wizard by any deliberate route (finishing on Done,
    // or the whole-wizard skip on Welcome). A plain window close BEFORE that is an accident, not a
    // decision, and must not retire the wizard - see OnClosed.
    private bool _leftDeliberately;

    /// <summary>
    /// True when this is a REVIEW run rather than a first run - onboarding was already completed on
    /// this machine, so the user opened the wizard on purpose from Settings. Every screen that has
    /// something already set up should say so rather than asking for it again.
    /// </summary>
    private readonly bool _isReview;

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

        // The completion marker is what separates a first run from a deliberate re-run: it is written
        // when the user leaves the wizard, and it is the same fact that decides whether the wizard
        // auto-opens at launch. Read once, here, so every screen can be told which run this is.
        _isReview = !FirstRunWizardModel.ShouldShow();

        InitializeComponent();
        BuildDots();
        BuildWelcomeScreen();
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

            // On a REVIEW run the dots are navigation: everything is already set up, so there is no
            // order left to protect and the user came here to change one specific thing. Clicking a
            // dot goes straight to that step instead of pressing Continue five times.
            //
            // On a FIRST run they stay indicators. A first-timer skipping to a step whose answer
            // depends on an earlier one is how a machine ends up half configured.
            if (_isReview)
            {
                var target = _model.Steps[i];
                dot.Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand);
                AutomationProperties.SetName(dot, $"Go to step {i + 1}, {StepDisplayName(target)}");
                dot.PointerPressed += (_, _) =>
                {
                    if (_model.Current == target) return;
                    FileLog.Write($"[FirstRunWizardDialog] dot navigation to {target}");
                    _model.GoTo(target);
                    ShowStep(_model.Current);
                };
            }

            _dots.Add(dot);
            DotsPanel.Children.Add(dot);
        }
        RefreshDots();
    }

    /// <summary>The user-facing name of a step, used beside the dots and by the dot navigation.</summary>
    private static string StepDisplayName(WizardStep step) => step switch
    {
        WizardStep.Welcome => "Welcome",
        WizardStep.Gateway => "Your gateway",
        WizardStep.Agents => "Your agents",
        WizardStep.Tools => "Tools",
        WizardStep.Code => "Your code",
        WizardStep.Screenshots => "Screenshots",
        WizardStep.Done => "Done",
        _ => step.ToString(),
    };

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

        DotsStepLabel.Text = $"Step {_model.Index + 1} of {_model.Count} - {StepDisplayName(_model.Current)}";
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
        // Leaving the Code step is the moment the folders chosen there have to become real to the rest
        // of the application - once, not once per folder.
        if (step != WizardStep.Code && _codeRootsChanged)
            _ = PublishCodeRootsAsync();

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
                FooterNote.Text = _isReview
                    ? "Changing something here changes it everywhere. Nothing is reset."
                    : "Takes about 3 minutes. Every step is optional, and everything can be changed later in Settings.";
                FooterNote.IsVisible = true;
                break;

            case WizardStep.Agents:
                PrimaryButton.IsVisible = true;
                if (!_agentScanRan)
                {
                    // ScanAgentsAsync owns the button while it runs - it must NOT be pressable, see
                    // the note there.
                    _ = ScanAgentsAsync();
                }
                else
                {
                    PrimaryButton.Content = "Use these agents";
                    PrimaryButton.IsEnabled = _model.AgentsFound;
                }
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
                AdoptExistingGateway();
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

    /// <summary>
    /// Find this machine's coding agents and show them.
    ///
    /// The primary button is held DISABLED for the whole scan, and that is the point of this method's
    /// shape. It used to stay live: the step made it visible without ever disabling it, and its
    /// enabled state was only set from the result at the very end - so a user who pressed
    /// "Use these agents" while the scan was still running ran <see cref="AcceptAgentsAsync"/> against
    /// the still-empty suggestion list, added nothing, and advanced. The wizard said it was using
    /// their agents and configured none, silently.
    ///
    /// The rows are also built TWICE on purpose: once the moment detection returns, so the user can
    /// see what was found, and again after the version probes land. The probes run one after another
    /// and each is bounded by its plugin's validation timeout, so waiting for all of them before
    /// drawing anything left the screen blank for the sum of every probe.
    /// </summary>
    private async Task ScanAgentsAsync()
    {
        FileLog.Write("[FirstRunWizardDialog] ScanAgentsAsync");
        AgentsTitle.Text = "Looking for your coding agents";
        AgentsScanBar.IsVisible = true;
        AgentsScanLine.IsVisible = true;
        AgentsScanLine.Text = "Checking this machine...";
        AgentsStatusText.Text = "Please wait for this to finish. Your agents are only detected and set up correctly once the check completes.";
        AgentsEmptyActions.IsVisible = false;
        AgentsListPanel.Children.Clear();
        SetAgentsPrimaryBusy(true);
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

            // Draw what we have now, with the version cell still pending, so the list builds in front
            // of the user instead of appearing all at once at the end.
            BuildAgentRows(suggestions, existing, new Dictionary<AgentKind, string>());

            // Probe each found agent's version so the rows read "v2.1.4 - path": the version is the
            // proof the detection is real, not a guess. Best-effort - a probe that fails or times
            // out just leaves the row without a version.
            var versions = await ProbeVersionsAsync(
                suggestions,
                name => AgentsScanLine.Text = $"Checking {name}...");
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
            SetAgentsPrimaryBusy(false, enabled: anyFound);

            FileLog.Write($"[FirstRunWizardDialog] ScanAgentsAsync: found={foundCount}, anyFound={anyFound}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] ScanAgentsAsync FAILED: {ex.Message}");
            AgentsStatusText.Text = $"Agent scan failed: {ex.Message}";
            // A failed scan must not leave the user stranded on a dead button - re-checking and the
            // install path are still open to them.
            AgentsEmptyActions.IsVisible = true;
            SetAgentsPrimaryBusy(false, enabled: false);
        }
    }

    /// <summary>
    /// Hold (or release) the footer button for the agent scan. Guarded on the current step because the
    /// footer button is shared by every screen: a user who presses Back mid-scan must not have the
    /// step they landed on relabelled "Checking..." when this scan finally returns.
    /// </summary>
    private void SetAgentsPrimaryBusy(bool busy, bool enabled = false)
    {
        if (_model.Current != WizardStep.Agents) return;
        AgentsScanBar.IsVisible = busy;
        AgentsScanLine.IsVisible = busy;
        PrimaryButton.Content = busy ? "Checking..." : "Use these agents";
        PrimaryButton.IsEnabled = !busy && enabled;
    }

    /// <summary>
    /// Version-probe every found agent (bounded per-tool by the plugin's validation timeout), naming
    /// each one to <paramref name="onProbing"/> as it starts so the screen can say which agent it is
    /// working on rather than showing an unchanging line for the whole run.
    /// </summary>
    private async Task<Dictionary<AgentKind, string>> ProbeVersionsAsync(
        IReadOnlyList<ToolDetectionSuggestion> suggestions,
        Action<string>? onProbing = null)
    {
        var versions = new Dictionary<AgentKind, string>();
        foreach (var s in suggestions.Where(s => s.Found))
        {
            onProbing?.Invoke(s.DisplayName);
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
                RowState.Ready));
        }


        var notFound = suggestions.Where(s => !s.Found).Select(s => s.DisplayName).ToList();
        if (notFound.Count > 0)
        {
            AgentsListPanel.Children.Add(AgentRow(
                string.Join(", ", notFound),
                "Not installed - you can add any of these later in Settings.",
                RowState.NotSetUp));
        }
    }

    /// <summary>
    /// The four states a row can be in. One word each, one colour each.
    ///
    /// Before this there was a single boolean and fifteen different labels sharing two colours, so a
    /// tool that FAILED to install wore the same grey pill as an agent that simply is not installed -
    /// one needs the user to act, the other is fine and expected, and they were indistinguishable.
    /// Counts wore status colours too: "3 folders" rendered green, as though having three folders were
    /// a state of health.
    /// </summary>
    private enum RowState
    {
        /// <summary>It works. Nothing to do. Green.</summary>
        Ready,

        /// <summary>Happening now, and it finishes on its own. Blue.</summary>
        Working,

        /// <summary>It will not fix itself. Red - and only ever used where the screen also offers the
        /// action that fixes it, or the colour becomes noise.</summary>
        NeedsYou,

        /// <summary>Absent and optional. No action implied. Grey.</summary>
        NotSetUp,
    }

    private static (string Background, string Foreground) PillColours(RowState state) => state switch
    {
        RowState.Ready => ("#E5F3E9", "#1A7F37"),
        RowState.Working => ("#F2F8FD", "#0066B8"),
        RowState.NeedsYou => ("#FDECEC", "#DC2626"),
        _ => ("#F5F6F8", "#8A909A"),
    };

    private static string PillLabel(RowState state) => state switch
    {
        RowState.Ready => "Ready",
        RowState.Working => "Working",
        RowState.NeedsYou => "Needs you",
        _ => "Not set up",
    };

    private static Border AgentRow(string name, string sub, RowState state)
    {
        var (background, foreground) = PillColours(state);
        var pill = new Border
        {
            Background = Brush(background),
            CornerRadius = new global::Avalonia.CornerRadius(999),
            Padding = new global::Avalonia.Thickness(10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = PillLabel(state),
                Foreground = Brush(foreground),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
            },
        };
        var ready = state == RowState.Ready;

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
        // A repair owns the screen while it runs; the poll must not redraw underneath it.
        if (_toolsRepairing || _closed) return;

        try
        {
            var catalog = await Task.Run(() => new ToolCatalogService().GetCatalog());
            // The catalog read is slow enough that the window can be gone by the time it returns.
            if (_closed) return;
            _toolsTotalCount = catalog.Count;
            _toolsReadyCount = catalog.Count(t => t.IsAvailable);

            ToolsTitle.Text = $"{_toolsTotalCount} tools, maintained for you";

            var missingCount = _toolsTotalCount - _toolsReadyCount;
            if (missingCount > 0)
                _toolsWaitStartedUtc ??= DateTime.UtcNow;

            // A tool still absent after the stall window is not "installing" in any sense the user
            // would recognise, and saying so for ever is a promise the screen cannot keep. Past that
            // point it is named as not installed and the repair is offered.
            var stalled = missingCount > 0
                && _toolsWaitStartedUtc is not null
                && (DateTime.UtcNow - _toolsWaitStartedUtc.Value).TotalSeconds >= ToolsStallSeconds;
            // Recorded so the Done receipt reports the same three states this screen does.
            _toolsStalled = stalled;

            ToolsListPanel.Children.Clear();
            foreach (var tool in catalog)
            {
                var state = tool.IsAvailable ? RowState.Ready
                    : stalled ? RowState.NeedsYou
                    : RowState.Working;
                var detail = tool.IsAvailable || !stalled
                    ? tool.Description
                    : $"{tool.Description} - this one did not install";
                ToolsListPanel.Children.Add(AgentRow(tool.Name, detail, state));
            }

            ToolsFixPanel.IsVisible = stalled;

            if (missingCount == 0)
            {
                ToolsStatusText.Text = $"All {_toolsTotalCount} tools are installed and up to date.";
                ToolsStatusText.Foreground = Brush("#1A7F37");
                _toolsWaitStartedUtc = null;
                StopToolsPoll();
            }
            else if (stalled)
            {
                ToolsStatusText.Text = missingCount == 1
                    ? "1 tool did not install. Repairing takes about a minute - you can continue while it runs."
                    : $"{missingCount} tools did not install. Repairing takes about a minute - you can continue while it runs.";
                ToolsStatusText.Foreground = Brush("#DC2626");
                StopToolsPoll();
            }
            else
            {
                // Both halves of the trade, so continuing is an informed choice rather than a guess.
                ToolsStatusText.Text =
                    $"{_toolsReadyCount} of {_toolsTotalCount} ready. Wait here and all of them will be working before you finish. Continue and DevThrottle finishes the rest in the background on its own.";
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

    /// <summary>
    /// Repair the shipped tools from this screen - the SAME transaction the Home screen's Fix button
    /// runs (<see cref="ToolUpdater.RepairPythonToolsAsync"/>), with its progress streamed here. The
    /// outcome is reported ON THE SCREEN either way: a repair that fails silently reverting to the
    /// same red row is exactly the dead end this step had before.
    /// </summary>
    private async void BtnFixTools_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FirstRunWizardDialog] BtnFixTools_Click");
        if (_toolsRepairing) return;
        _toolsRepairing = true;
        StopToolsPoll();
        ToolsFixButton.IsEnabled = false;
        ToolsFixProgress.IsVisible = true;
        ToolsFixProgress.Text = "Starting the repair...";
        try
        {
            var layout = InstallLayout.Default();
            var progress = new Progress<string>(line => ToolsFixProgress.Text = line);
            var result = await Task.Run(() => new ToolUpdater(layout).RepairPythonToolsAsync(progress));
            FileLog.Write($"[FirstRunWizardDialog] BtnFixTools_Click: success={result.Success}, msg={result.Message}");

            _toolsRepairing = false;
            // A repair takes about a minute. The user may well have closed the wizard in the meantime;
            // the repair still ran and still mattered, but there is no screen left to report it on.
            if (_closed) return;
            if (result.Success)
            {
                // Start the wait clock again so anything still absent gets a fair window before it is
                // called a failure a second time.
                _toolsWaitStartedUtc = null;
                ToolsFixProgress.Text = "Repair finished. Checking the tools again...";
                await RefreshToolsScreenAsync();
                if (_toolsReadyCount == _toolsTotalCount)
                    ToolsFixProgress.IsVisible = false;
                else
                    ToolsFixProgress.Text = "The repair ran but some tools are still missing. Continue - DevThrottle keeps retrying in the background - or see the tools documentation below.";
            }
            else
            {
                ToolsFixProgress.Text = $"The repair did not succeed: {result.Message}";
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] BtnFixTools_Click FAILED: {ex.Message}");
            _toolsRepairing = false;
            ToolsFixProgress.Text = $"The repair could not run: {ex.Message}";
        }
        finally
        {
            _toolsRepairing = false;
            ToolsFixButton.IsEnabled = true;
        }
    }

    private void StartToolsPoll()
    {
        // A catalog read or a repair that returns after the window has gone must not resurrect the
        // poll: the timer would hold the closed dialog alive and keep refreshing controls forever.
        if (_closed || _model.Current != WizardStep.Tools) return;
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
    private async Task ScanCodeFoldersAsync(int budgetSeconds = 10)
    {
        FileLog.Write($"[FirstRunWizardDialog] ScanCodeFoldersAsync: budget={budgetSeconds}s");
        _codeScanRan = true;
        CodeScanActivity.IsVisible = true;
        CodeScanLine.Text = "Looking for repositories in the usual places...";
        CodeScanStatusText.IsVisible = false;
        CodeKeepLookingButton.IsVisible = false;
        try
        {
            await EnsureRootStoreLoadedAsync();

            // Roots that already exist (Settings, a previous run) appear first.
            foreach (var root in _rootStore.Roots.ToList())
            {
                if (_codeAddedRoots.ContainsKey(root.Path)) continue;
                var count = await Task.Run(() => CodeFolderScout.CountRepos(root.Path));
                _codeAddedRoots[root.Path] = count;
                CodeSuggestionsPanel.Children.Add(CodeRow(root.Path));
            }
            UpdateCodeTotal();

            _codeScanCts?.Cancel();
            _codeScanCts = new CancellationTokenSource(TimeSpan.FromSeconds(budgetSeconds));

            // Progress<T> posts each suggestion onto the UI thread as the background sweep finds it.
            // Each one is REGISTERED as it arrives, not offered: a folder we know about is a folder we
            // can keep the books on and make recommendations against, and an Add button on every row
            // was missed even by the person who wrote the screen - four folders holding seventeen
            // repositories went past unregistered. Opting OUT is the deliberate act now.
            var progress = new Progress<CodeFolderSuggestion>(s =>
            {
                CodeScanLine.Text = $"Looking for repositories - checking {s.Path}";
                if (_codeAddedRoots.ContainsKey(s.Path)) return;
                // A folder the user has already removed stays removed, however late the sweep reports it.
                if (_codeRejectedRoots.Contains(s.Path)) return;
                if (CodeSuggestionsPanel.Children.OfType<Border>().Any(b => Equals(b.Tag as string, s.Path))) return;
                _codePendingWrites.Add(AutoAddCodeRootAsync(s.Path, s.RepoCount));
            });

            await CodeFolderScout.ScanAsync(progress, _codeScanCts.Token);

            // Discovery is done; the registrations it queued may not be. Waiting here is what makes
            // "that is everywhere we checked" true, and what guarantees the folders are on disk before
            // anything downstream is told to go and read them.
            await WaitForPendingCodeWritesAsync();

            CodeScanActivity.IsVisible = false;
            CodeScanStatusText.IsVisible = true;
            if (CodeSuggestionsPanel.Children.Count > 0)
            {
                CodeTitle.Text = "We found your code";
                CodeScanStatusText.Text = "That is everywhere we checked - add anything else with Browse below.";
            }
            else
            {
                CodeScanStatusText.Text = "No repositories found in the usual places. Browse to where your code lives.";
            }
        }
        catch (OperationCanceledException)
        {
            // The sweep ran out of its time budget. It did NOT finish, and it must not claim it did:
            // this branch used to write the identical "that is everywhere we checked" sentence the
            // completed scan writes, so a scan cut off part way through a large disk told the user it
            // had been exhaustive and there was no way to tell the two apart.
            FileLog.Write($"[FirstRunWizardDialog] ScanCodeFoldersAsync: scan hit its {budgetSeconds}s budget");
            CodeScanActivity.IsVisible = false;
            CodeScanStatusText.IsVisible = true;
            CodeScanStatusText.Text =
                $"We stopped looking after {budgetSeconds} seconds, so this may not be everything. Keep looking, or add a folder with Browse below.";
            CodeKeepLookingButton.IsVisible = true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] ScanCodeFoldersAsync FAILED: {ex.Message}");
            CodeScanActivity.IsVisible = false;
            CodeScanStatusText.IsVisible = true;
            CodeScanStatusText.Text = $"Folder scan failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Wait for every registration the sweep queued. Each one is started from a progress callback and
    /// serialized behind the store gate, so the sweep's own completion proves only that DISCOVERY has
    /// finished - the writes can still be in flight behind it.
    /// </summary>
    private async Task WaitForPendingCodeWritesAsync()
    {
        if (_codePendingWrites.Count == 0) return;
        var pending = _codePendingWrites.ToArray();
        _codePendingWrites.Clear();
        try
        {
            await Task.WhenAll(pending);
        }
        catch (Exception ex)
        {
            // Each registration already logs its own failure; this only stops one bad folder taking
            // the whole wait down.
            FileLog.Write($"[FirstRunWizardDialog] WaitForPendingCodeWritesAsync: a registration failed: {ex.Message}");
        }
    }

    /// <summary>Run the sweep again with a longer budget, after it reported that it stopped early.</summary>
    private void BtnKeepLookingForCode_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FirstRunWizardDialog] BtnKeepLookingForCode_Click");
        _ = ScanCodeFoldersAsync(budgetSeconds: 60);
    }

    private async Task EnsureRootStoreLoadedAsync()
    {
        if (_rootStoreLoaded) return;
        await Task.Run(_rootStore.Load);
        _rootStoreLoaded = true;
    }

    /// <summary>
    /// Make the folders chosen on this step real to the RUNNING application.
    ///
    /// The wizard writes through its own <see cref="RootDirectoryStore"/> instance, so the copy the
    /// application loaded at startup is stale the moment a folder is registered here, and the
    /// repository model behind New Session has never scanned the new root. Without this the wizard
    /// would persist the folders correctly to disk and the user would still see nothing until the
    /// next restart.
    /// </summary>
    private async Task PublishCodeRootsAsync()
    {
        _codeRootsChanged = false;
        // Publishing a half-written set of roots is worse than publishing late: the rescan would run
        // over whatever happened to have landed, and New Session would show a subset. Wait for the
        // writes first. Nothing here touches the user interface, so it is safe after the window closes.
        await WaitForPendingCodeWritesAsync();
        try
        {
            if (global::Avalonia.Application.Current is not App app) return;
            app.RootDirectoryStore.Load();
            app.StartRepositoryRescan();
            FileLog.Write($"[FirstRunWizardDialog] PublishCodeRoots: {app.RootDirectoryStore.Roots.Count} root(s) republished, rescan started");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] PublishCodeRoots FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// One registered folder: the path, its verified repository count, and Remove. Every row on this
    /// screen is already registered - there is no Add, because there is no un-added state to act on.
    ///
    /// The count is READ FROM <see cref="_codeAddedRoots"/> rather than passed in, so the number on the
    /// row and the total underneath the list cannot come from different places. The total is a sum of
    /// that same dictionary, which makes "the rows do not add up to the total" impossible to express
    /// rather than merely absent today. Callers must register the folder before drawing its row.
    /// </summary>
    private Border CodeRow(string path)
    {
        var repoCount = _codeAddedRoots.TryGetValue(path, out var known) ? known : 0;
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
            Text = repoCount == 1 ? "1 git repository" : $"{repoCount} git repositories",
            Foreground = Brush("#8A909A"),
            FontSize = 11,
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        global::Avalonia.Controls.Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var removeButton = new Button
        {
            Content = "Remove",
            Classes = { "dialogButton" },
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(removeButton, $"Remove {path} from the folders DevThrottle watches");
        removeButton.Click += async (_, _) => await RemoveCodeRootAsync(path, removeButton);
        global::Avalonia.Controls.Grid.SetColumn(removeButton, 1);
        grid.Children.Add(removeButton);

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

    /// <summary>
    /// Register a folder the scan found and draw its row, in that order. The row claims the folder is
    /// registered, so it must be true by the time the row appears - a row that says "added" while the
    /// write is still pending becomes a lie the moment the user closes the window.
    /// </summary>
    private async Task AutoAddCodeRootAsync(string path, int repoCount)
    {
        FileLog.Write($"[FirstRunWizardDialog] AutoAddCodeRootAsync: {path} ({repoCount} repositories)");
        await _codeStoreGate.WaitAsync();
        try
        {
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
            _codeRootsChanged = true;
            if (!CodeSuggestionsPanel.Children.OfType<Border>().Any(b => Equals(b.Tag as string, path)))
                CodeSuggestionsPanel.Children.Add(CodeRow(path));
            UpdateCodeTotal();
            if (_model.Current == WizardStep.Code)
                PrimaryButton.Content = "Looks right";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] AutoAddCodeRootAsync FAILED for {path}: {ex.Message}");
        }
        finally
        {
            _codeStoreGate.Release();
        }
    }

    /// <summary>Un-register a folder the user does not want watched, and drop its row.</summary>
    private async Task RemoveCodeRootAsync(string path, Button removeButton)
    {
        FileLog.Write($"[FirstRunWizardDialog] RemoveCodeRootAsync: {path}");
        await _codeStoreGate.WaitAsync();
        try
        {
            removeButton.IsEnabled = false;
            await EnsureRootStoreLoadedAsync();

            var index = _rootStore.Roots
                .Select((r, i) => (r, i))
                .Where(t => string.Equals(
                    System.IO.Path.GetFullPath(t.r.Path),
                    System.IO.Path.GetFullPath(path),
                    StringComparison.OrdinalIgnoreCase))
                .Select(t => (int?)t.i)
                .FirstOrDefault();

            if (index is not null)
                await Task.Run(() => _rootStore.Remove(index.Value));

            _codeAddedRoots.Remove(path);
            // Remember the rejection, so a suggestion for this same folder arriving later from the
            // still-running sweep does not quietly register it again.
            _codeRejectedRoots.Add(path);
            _codeRootsChanged = true;
            var row = CodeSuggestionsPanel.Children.OfType<Border>().FirstOrDefault(b => Equals(b.Tag as string, path));
            if (row is not null)
                CodeSuggestionsPanel.Children.Remove(row);
            UpdateCodeTotal();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] RemoveCodeRootAsync FAILED: {ex.Message}");
            removeButton.IsEnabled = true;
        }
        finally
        {
            _codeStoreGate.Release();
        }
    }

    /// <summary>
    /// The proof number: repositories across every registered folder, WITH the number of folders it is
    /// summed over. The list scrolls, so the total is usually shown beside only two or three of the
    /// rows that make it up; naming the folder count is what lets the user reconcile the two without
    /// scrolling the whole list.
    /// </summary>
    private void UpdateCodeTotal()
    {
        var total = _codeAddedRoots.Values.Sum();
        var folders = _codeAddedRoots.Count;
        CodeTotalPanel.IsVisible = total > 0;
        CodeTotalCount.Text = total.ToString();

        // The breakdown, so the headline number can be checked against its parts from the log alone.
        // The list scrolls, so on a machine with several folders the screen never shows every row that
        // makes up the total at once.
        FileLog.Write(
            $"[FirstRunWizardDialog] UpdateCodeTotal: {total} across {folders} folder(s) = "
            + string.Join(" + ", _codeAddedRoots.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}")));
        CodeTotalCaption.Text = folders == 1
            ? "repositories in this folder, available when you start an agent"
            : $"repositories across these {folders} folders, available when you start an agent";
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

            // Already registered (the sweep found it, or a previous run did) - nothing to do.
            if (_codeAddedRoots.ContainsKey(path)) return;

            await AutoAddCodeRootAsync(path, count);
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
        ShotsBusyBar.IsVisible = true;
        try
        {
            // A folder already chosen on this machine WINS over anything detection would guess.
            // Detection used to run unconditionally, which meant a returning user who had once browsed
            // to a custom folder was shown the detector's guess instead, under a button reading "Use
            // this folder" - and pressing Continue wrote the guess over their deliberate choice.
            var configured = ReadConfiguredScreenshotsFolder();
            if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            {
                FileLog.Write($"[FirstRunWizardDialog] DetectScreenshotsForWizardAsync: keeping the configured folder {configured}");
                ShotsPathText.Text = configured;
                ShotsProvenanceText.Text = "Currently in use - counting the images in it...";
                var configuredCount = await Task.Run(() => ScreenshotLocator.CountImages(configured));
                await SetScreenshotsFolderAsync(configured, "Currently in use", configuredCount);
                return;
            }

            var best = await Task.Run(() => ScreenshotLocator.DetectCandidates().FirstOrDefault());
            if (best is not null)
            {
                // Show the answer BEFORE the slow part. Counting every image in the folder and then
                // decoding the newest few are two full passes over it, and on a large or cloud-backed
                // folder that is a real wait - during which the step used to show nothing but the
                // static "Looking for your screenshots folder..." line and looked dead.
                ShotsPathText.Text = best.Path;
                ShotsProvenanceText.Text = $"{best.Provenance} - counting the images in it...";

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
        finally
        {
            ShotsBusyBar.IsVisible = false;
        }
    }

    /// <summary>The screenshots folder already saved in config, or null when none is set.</summary>
    private static string? ReadConfiguredScreenshotsFolder()
    {
        try
        {
            var path = CcDirectorConfigService.ReadRaw()["screenshots"]?["source_directory"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] ReadConfiguredScreenshotsFolder failed: {ex.Message}");
            return null;
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

    // ---- Welcome ------------------------------------------------------------------------------------

    /// <summary>
    /// Build the Welcome screen for whichever run this is.
    ///
    /// FIRST RUN: state plainly what the next three minutes are for - one row per thing the wizard
    /// covers, each naming what it buys the user. That is the screen's whole job. It used to open with
    /// a note from the founder about why the product exists; somebody who has just installed software
    /// wants to know what is about to be asked of them, not to be talked to.
    ///
    /// REVIEW RUN: the same rows carry the machine's CURRENT state, because a returning user is here
    /// to check or change something, and telling them they are "three minutes from running their first
    /// coding agent" when they already have four agents configured is simply false.
    /// </summary>
    private void BuildWelcomeScreen()
    {
        WelcomeAgendaPanel.Children.Clear();

        if (_isReview)
        {
            WelcomeTitle.Text = "Review your setup";
            // Says what actually happens. The earlier wording promised "nothing is redone unless you
            // ask", which the Code step then broke the moment it was reached: it sweeps for folders and
            // registers what it finds, with no confirmation. Persisting configuration as a side effect
            // of INSPECTION is bad enough without the previous screen having promised it would not.
            WelcomeSubText.Text =
                "Everything below is already set up. Nothing here is reset - but any new code folders we find as you walk through will be added.";
            WelcomeSetupButton.Content = "Review each step";
            WelcomeSkipLink.Content = "Close - everything is already set up";
            FooterNote.Text = "Changing something here changes it everywhere. Nothing is reset.";

            // Cheap reads only: this screen must paint instantly. Anything that needs a scan (the
            // agent probe, the repository sweep) belongs on its own step, not here.
            foreach (var row in ReviewSummaryRows())
                WelcomeAgendaPanel.Children.Add(row);
            return;
        }

        WelcomeTitle.Text = "Let's get you set up";
        WelcomeSubText.Text =
            "Four quick things, so DevThrottle knows this machine. Skip any of them - all of it can be changed later.";
        WelcomeSetupButton.Content = "Set me up";
        WelcomeSkipLink.Content = "Skip setup and figure it out myself";

        // Same order the steps come in - the gateway leads.
        AddAgendaRow("Your gateway", "Your agents on your phone, voice, and the morning report");
        AddAgendaRow("Your coding agents", "Find the ones already installed, or install one now");
        AddAgendaRow("Where your code lives", "So DevThrottle can offer you repositories and keep the books on them");
        AddAgendaRow("Screenshots", "So you can show an agent what you mean instead of describing it");
    }

    /// <summary>The review run's state summary, read from what is already on disk. No scans.</summary>
    private IEnumerable<Control> ReviewSummaryRows()
    {
        var rows = new List<Control>();

        // Same order as the steps: the gateway leads.
        var gateway = GatewayConfig.Load();
        rows.Add(AgentRow(
            "Your gateway",
            gateway.IsEnabled ? gateway.Url : "Not connected - phone access and the morning report need one",
            gateway.IsEnabled ? RowState.Ready : RowState.NotSetUp));

        var agentCount = 0;
        try
        {
            agentCount = AgentEntryStore.ReadCurrentEntries().Count;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] ReviewSummaryRows: agent read failed: {ex.Message}");
        }
        rows.Add(AgentRow(
            "Your coding agents",
            agentCount > 0
                ? (agentCount == 1 ? "1 agent, configured and ready to use" : $"{agentCount} agents, configured and ready to use")
                : "None configured yet",
            agentCount > 0 ? RowState.Ready : RowState.NotSetUp));

        var rootCount = 0;
        try
        {
            var store = new RootDirectoryStore();
            store.Load();
            rootCount = store.Roots.Count;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunWizardDialog] ReviewSummaryRows: roots read failed: {ex.Message}");
        }
        rows.Add(AgentRow(
            "Where your code lives",
            rootCount > 0
                ? (rootCount == 1 ? "1 folder - DevThrottle is keeping the books on it" : $"{rootCount} folders - DevThrottle is keeping the books on them")
                : "No folders added yet",
            rootCount > 0 ? RowState.Ready : RowState.NotSetUp));

        return rows;
    }

    private void AddAgendaRow(string name, string what) =>
        WelcomeAgendaPanel.Children.Add(new Border
        {
            Background = Brush("#FFFFFF"),
            BorderBrush = Brush("#E6E8EC"),
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius = new global::Avalonia.CornerRadius(10),
            Padding = new global::Avalonia.Thickness(15, 9),
            Child = StackOf(
                new TextBlock
                {
                    Text = name,
                    Foreground = Brush("#16181D"),
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = what,
                    Foreground = Brush("#8A909A"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                }),
        });

    private static StackPanel StackOf(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 2 };
        foreach (var child in children)
            panel.Children.Add(child);
        return panel;
    }

    // ---- Gateway step (native, hosted-first) -------------------------------------------------------

    // The step's opening copy, kept so "connect a different gateway" can put it back after the
    // connected state has rewritten it.
    private const string GatewayChoiceTitle = "Connect your gateway";
    private const string GatewayChoiceSub =
        "The gateway is what lets you check on your agents from your phone, use voice, and get your morning report. Signing in takes seconds.";

    /// <summary>
    /// A gateway that is already configured is not a question. Read the saved gateway once, when the
    /// step opens, and if this machine is already enrolled open on the connected state with Continue
    /// as the action - reconnecting stays available, but as a quiet secondary link.
    ///
    /// Without this the step had no idea what the machine's actual state was: its connected flag was
    /// set only by an enrolment performed during THIS run, so re-running the wizard on an enrolled
    /// machine showed the choice cards and offered "Sign in and connect" - while
    /// <see cref="BuildDoneReceipt"/>, which does read the saved config, printed "Gateway connected"
    /// two screens later. The same run said both.
    /// </summary>
    private void AdoptExistingGateway()
    {
        if (_gatewayExistingChecked) return;
        _gatewayExistingChecked = true;

        var config = GatewayConfig.Load();
        if (!config.IsEnabled)
        {
            FileLog.Write("[FirstRunWizardDialog] AdoptExistingGateway: no gateway configured");
            return;
        }

        FileLog.Write($"[FirstRunWizardDialog] AdoptExistingGateway: gateway already configured as {config.Url}");
        _gatewayConnected = true;
        _gatewayWasAlreadyConnected = true;

        // What we KNOW is that a gateway address is saved. We have not reached it, authenticated, or
        // confirmed this machine is still enrolled - IsEnabled is true for any non-blank string. So the
        // screen states the configuration and nothing more. Saying "phone access, voice and the morning
        // report are working" would be a claim about three subsystems on the strength of one saved
        // string, and it would be wrong for exactly the user who most needs to know: someone whose
        // address is stale, mistyped, or whose machine was un-enrolled.
        GatewayTitle.Text = "Your gateway is already set up";
        GatewaySubText.Text =
            "This machine is configured to use the gateway below, so there is nothing to do here. If it is not working, connect it again.";
        GatewayConnectedHostText.Text = config.Url;
        // Not "Connected" - we have not checked. This badge is only earned by an enrolment that
        // succeeded in THIS run, which is the one case we have actually observed working.
        GatewayConnectedBadgeText.Text = "Set up";
        GatewayConnectedBadge.Background = Brush("#F5F6F8");
        GatewayConnectedBadgeText.Foreground = Brush("#5A616B");
        GatewayChangeLink.IsVisible = true;
        ShowGatewayView(GatewayConnectedView);
    }

    /// <summary>Deliberately go back to the choice cards from the already-connected state.</summary>
    private void GatewayChange_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[FirstRunWizardDialog] GatewayChange_Click");
        _gatewayConnected = false;
        _gatewayWasAlreadyConnected = false;
        GatewayTitle.Text = GatewayChoiceTitle;
        GatewaySubText.Text = GatewayChoiceSub;
        GatewayChangeLink.IsVisible = false;
        ShowGatewayView(GatewayChoiceView);
        RefreshGatewayChoiceUi();
    }

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

        // The selection has to be readable without seeing the border. A Border is not a radio button
        // and exposes no selected state, so the state is carried in the accessible help text - the one
        // channel a screen reader will actually read out.
        AutomationProperties.SetHelpText(card, selected ? "Selected" : "Not selected. Press Enter to choose it.");
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

    /// <summary>
    /// Space or Enter picks the focused card - the keyboard equivalent of clicking it. Tab already
    /// reaches the cards now that they are focusable; without this they could be reached and not used.
    /// </summary>
    private void GatewayCard_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter)) return;
        if (!TryChoiceForCard(sender, out var choice)) return;
        SelectGatewayChoice(choice);
        e.Handled = true;
    }

    /// <summary>
    /// Focus alone does NOT change the choice. Tabbing through the options to read them must not
    /// silently move the selection - a keyboard or screen-reader user has to be able to review all
    /// three before committing, exactly as a mouse user can. Space or Enter is the commit.
    /// </summary>
    private void GatewayCard_GotFocus(object? sender, GotFocusEventArgs e)
    {
        // Announce what is focused and whether it is the current choice, since a Border exposes no
        // selection state of its own.
        if (TryChoiceForCard(sender, out var choice) && sender is Border card)
            AutomationProperties.SetHelpText(card, choice == _gatewayChoice ? "Selected" : "Not selected. Press Enter to choose it.");
    }

    private bool TryChoiceForCard(object? sender, out GatewayChoice choice)
    {
        if (ReferenceEquals(sender, GatewayHostedCard)) { choice = GatewayChoice.Hosted; return true; }
        if (ReferenceEquals(sender, GatewaySelfHostCard)) { choice = GatewayChoice.SelfHost; return true; }
        if (ReferenceEquals(sender, GatewayNotNowCard)) { choice = GatewayChoice.NotNow; return true; }
        choice = GatewayChoice.Hosted;
        return false;
    }

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
            // Earned: this enrolment just succeeded against the live gateway in this run.
            GatewayConnectedBadgeText.Text = "Connected";
            GatewayConnectedBadge.Background = Brush("#E5F3E9");
            GatewayConnectedBadgeText.Foreground = Brush("#1A7F37");
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
            // A gateway that was already connected before this run is reported as UNCHANGED, not as
            // something this run achieved - a receipt that claims credit for work it did not do is
            // the same class of untruth as the step that used to ask for it again.
            DoneReceiptPanel.Children.Add(_gatewayWasAlreadyConnected
                ? ReceiptRow("Gateway", $"{gatewayUrl} - already set up before this run", RowState.Ready)
                : ReceiptRow("Gateway connected", gatewayUrl, RowState.Ready));
        else
            DoneReceiptPanel.Children.Add(ReceiptRow(
                "No gateway", "Connect one from Settings for phone access and your morning report", done: false));

        // Tools row (only when the Tools screen was seen, so the numbers are real).
        if (_toolsTotalCount > 0)
        {
            // Three states here, not two, because the Tools screen has three. Reducing a tool that
            // FAILED to install back to "installing - finishes on its own" would repeat on the last
            // screen the exact false promise the third state was added to remove, and would contradict
            // the screen the user saw two steps earlier.
            if (_toolsReadyCount == _toolsTotalCount)
                DoneReceiptPanel.Children.Add(ReceiptRow(
                    $"{_toolsTotalCount} tools ready", "Installed and kept current automatically", done: true));
            else if (_toolsStalled)
                DoneReceiptPanel.Children.Add(ReceiptRow(
                    $"{_toolsTotalCount - _toolsReadyCount} of {_toolsTotalCount} tools did not install",
                    "Repair them from Settings > Tools - they will not finish on their own",
                    RowState.NeedsYou));
            else
                DoneReceiptPanel.Children.Add(ReceiptRow(
                    "Tools installing", "Finishes on its own in the background", RowState.Working));
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
                "Morning report", "Every morning at 7:00 Eastern - waiting for a gateway to be connected",
                RowState.NotSetUp));
    }

    private static Border ReceiptRow(string name, string sub, bool done, string? pillText = null)
        => ReceiptRow(name, sub, done ? RowState.Ready : RowState.NotSetUp, pillText);

    /// <summary>
    /// One receipt line. It takes the SAME four states the steps use, so the last screen of onboarding
    /// cannot mean something different by a colour than the screen the user saw two steps earlier.
    /// </summary>
    private static Border ReceiptRow(string name, string sub, RowState state, string? pillText = null)
    {
        var (background, foreground) = PillColours(state);
        var pill = new Border
        {
            Background = Brush(background),
            CornerRadius = new global::Avalonia.CornerRadius(999),
            Padding = new global::Avalonia.Thickness(10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = pillText ?? PillLabel(state),
                Foreground = Brush(foreground),
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
        _leftDeliberately = true;
        await Task.Run(FirstRunWizardModel.MarkComplete);
        _marked = true;
        WantsNewSession = wantsNewSession;
        Close(true);
    }

    /// <summary>
    /// Closing the window is only a DECISION when the user has already reached the end by a deliberate
    /// route - finishing on Done, or the whole-wizard skip on Welcome. Both of those write the marker
    /// themselves before they close.
    ///
    /// A plain title-bar close part way through is an ACCIDENT, and it no longer retires the wizard.
    /// It used to: the marker was written here unconditionally, so an interrupted first run - which is
    /// exactly when someone closes a window - permanently stopped the wizard ever offering itself
    /// again, and the only way back was a button buried under the Agents tab in Settings. A user who
    /// genuinely does not want it has the skip link on the first screen, which is unambiguous.
    ///
    /// A REVIEW run never writes anything here: the marker is already set, and re-running from
    /// Settings must not change that either way.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _hostedEnrollCts?.Cancel();
        _claudeInstallCts?.Cancel();
        _shotsWatchCts?.Cancel();
        _codeScanCts?.Cancel();
        StopToolsPoll();
        // Closing while still ON the Code step must not strand the folders it registered - and a
        // registration that is STILL IN FLIGHT must not be stranded either, which is why this runs
        // whenever writes are outstanding rather than only when the changed flag is already set.
        if (_codeRootsChanged || _codePendingWrites.Count > 0)
            _ = PublishCodeRootsAsync();
        if (!_marked && _leftDeliberately)
        {
            FileLog.Write("[FirstRunWizardDialog] OnClosed: writing completion marker (left deliberately)");
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
        else if (!_marked)
        {
            FileLog.Write("[FirstRunWizardDialog] OnClosed: window closed part way through - marker NOT written, the wizard will offer itself again");
        }
        base.OnClosed(e);
    }

    // ---- Test hooks --------------------------------------------------------------------------------

    /// <summary>The wizard's current step, so a UI test can assert navigation moved as expected.</summary>
    internal WizardStep CurrentStepForTests => _model.Current;

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}
