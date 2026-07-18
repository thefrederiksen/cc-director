using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Models;
using CcDirectorSetup.Services;
using CcDirectorSetup.Steps;

namespace CcDirectorSetup;

public partial class MainWindow : Window
{
    private int _currentStep = 1;
    private List<PrerequisiteInfo> _prerequisites = [];
    private int _installedCount;
    private int _skippedCount;
    private string _installPath = "";
    private string _directorExePath = "";
    // A fresh install always lays down the Director set only - no role choice, no account (issue #1807),
    // so the fresh-install role is always Workstation (Director-only). Update mode overrides this from
    // disk in the constructor via InstalledRoleDetector so an existing Gateway host stays a Gateway host.
    private InstallRole _role = InstallRole.Workstation;
    private string? _gatewayResultMessage;

    private readonly bool _isUpdate;
    private readonly string? _installedVersion;
    private bool _alreadyUpToDate;
    private string? _latestVersion;
    private EngineInstallRunner.Prep? _cachedPrep;

    private WelcomeStep? _welcomeStep;
    private PrerequisitesStep? _prerequisitesStep;
    private PrivacyStep? _privacyStep;
    private SkillsStep? _skillsStep;
    private InstallStep? _installStep;
    private CompleteStep? _completeStep;

    private readonly record struct StepUI(Border Circle, TextBlock Label, TextBlock? Number);

    // Wizard steps: 1 Welcome, 2 Prerequisites, 4 Privacy, 6 Skills, 7 Install, 8 Complete. The Privacy
    // step (issue #659) slots in after the prerequisite checks. There is one linear path for every
    // install and update - the installer always lays down the Director set with no account gate (issue
    // #1807). Ids 3 (the old Gateway-only Sign-in step) and 5 (the old mandatory gateway-join Connect
    // step) were removed with the account gate; the surviving ids keep their old numbers so this switch
    // and the eight-row sidebar are unchanged, and the two removed rows simply never appear.
    private const int StepPrivacy = 4;
    private const int StepInstall = 7;
    private const int StepComplete = 8;

    // Step ordering lives in WizardStepFlow so it is unit-testable without constructing this WPF window.
    // These thin members bind to it - there is no parallel navigation logic in this window.
    private static List<int> VisibleSteps() => WizardStepFlow.VisibleSteps();
    private static int NextStep(int step) => WizardStepFlow.NextStep(step);
    private static int PrevStep(int step) => WizardStepFlow.PrevStep(step);

    public MainWindow()
    {
        InitializeComponent();

        // Version stamped by Directory.Build.props - read at runtime, never hardcoded in XAML.
        var info = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        VersionText.Text = $"v{info.Split('+')[0]}";

        _isUpdate = InstallDetector.IsInstalled();
        _installedVersion = _isUpdate ? InstallDetector.GetInstalledVersion() : null;

        SetupLog.Write($"[MainWindow] Started: isUpdate={_isUpdate}, installedVersion={_installedVersion}");

        // Role is a first-install choice the update wizard does not re-ask. Detect what is already
        // installed so a Gateway host stays a Gateway host on update (refresh Gateway + Cockpit and
        // re-assert the managed tray launch), instead of defaulting to a Director-only Workstation refresh.
        if (_isUpdate)
            _role = InstalledRoleDetector.Detect(InstallLayout.Default());
        SetupLog.Write($"[MainWindow] install role: {_role}");

        if (_isUpdate)
        {
            Title = "DevThrottle Update";
            SubtitleText.Text = "Update";
            Step7Label.Text = "Update";
        }

        Loaded += MainWindow_Loaded;
        ShowStep(1);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isUpdate)
            _ = FetchLatestVersionAsync();
    }

    /// <summary>Build the Welcome step and wire its Uninstall request (issue #257). The step
    /// only shows the button in update mode, so the handler is harmless on a fresh install.</summary>
    private WelcomeStep BuildWelcomeStep()
    {
        var step = new WelcomeStep(_isUpdate, _installedVersion, _role);
        step.UninstallRequested += OnUninstallRequested;
        return step;
    }

    /// <summary>Build the Privacy step (issue #659). There is no in-wizard sign-in anymore (issue
    /// #1807), so there is no account token to pre-fill from; the step reads the server default (ON)
    /// and always mirrors the choice to the local config.json. The Privacy step never gates Next -
    /// the toggle is a choice.</summary>
    private static PrivacyStep BuildPrivacyStep()
    {
        // No account token in a no-account install: the token provider always returns null, so the
        // step falls back to the local telemetry mirror.
        return new PrivacyStep(() => null);
    }

    /// <summary>
    /// Show the in-wizard uninstall flow (confirm -> live progress -> completion) for the detected
    /// role - no raw MessageBox pop-ups (issue: nicer uninstall progress). The Gateway role is a
    /// superset; we pick it only when a Gateway install is actually present so a Workstation box
    /// never tries to stop a tray app it does not have. Data under the per-user root is preserved.
    /// </summary>
    private void OnUninstallRequested(object? sender, EventArgs e)
    {
        var layout = InstallLayout.Default();
        var role = Directory.Exists(layout.GatewayDir) ? InstallRole.Gateway : InstallRole.Workstation;
        SetupLog.Write($"[MainWindow] OnUninstallRequested: showing uninstall step, role={role}");

        var step = new UninstallStep(layout, role);
        step.Cancelled += (_, _) =>
        {
            // Back to the Welcome screen with the normal wizard chrome restored.
            StepIndicators.Visibility = Visibility.Visible;
            NavBar.Visibility = Visibility.Visible;
            ShowStep(1);
        };
        step.CloseRequested += (_, _) => Close();

        // Hand the whole content area to the uninstall flow; it owns its own buttons, so hide the
        // step rail and the Back/Next nav bar while it is shown.
        StepIndicators.Visibility = Visibility.Collapsed;
        NavBar.Visibility = Visibility.Collapsed;
        StepContent.Content = step;
    }

    private async Task FetchLatestVersionAsync()
    {
        SetupLog.Write("[MainWindow] FetchLatestVersionAsync: checking the release for this setup executable");

        try
        {
            // Show the version this setup exe will actually install (its matching pre-release when
            // this is a pre-release build), so the welcome screen is not misleading (issue #1294).
            var release = await new ReleaseSource().FetchReleaseForSetupAsync(CancellationToken.None);
            _latestVersion = release.Manifest.Version;
            SetupLog.Write($"[MainWindow] FetchLatestVersionAsync: latestVersion={_latestVersion}");
            _welcomeStep?.UpdateVersionInfo(_installedVersion, _latestVersion);
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[MainWindow] FetchLatestVersionAsync FAILED: {ex.Message}");
        }
    }

    // The six sidebar rows in display order. Their x:Names keep the historical step ids (1, 2, 4, 6, 7,
    // 8) so the ShowStep switch and the WizardStepFlow ids stay unchanged; the circles are renumbered
    // 1..6 at runtime by UpdateSidebar. This list is aligned with WizardStepFlow.VisibleSteps().
    private List<StepUI> GetStepUIs() =>
    [
        new(Step1Circle, Step1Label, null),
        new(Step2Circle, Step2Label, Step2Num),
        new(Step4Circle, Step4Label, Step4Num),
        new(Step6Circle, Step6Label, Step6Num),
        new(Step7Circle, Step7Label, Step7Num),
        new(Step8Circle, Step8Label, Step8Num),
    ];

    // The five connector lines between the six rows, in order.
    private Border[] GetLines() => [Line12, Line23, Line45, Line67, Line78];

    private void ShowStep(int step)
    {
        SetupLog.Write($"[MainWindow] ShowStep: step={step}");
        _currentStep = step;

        UpdateSidebar();
        UpdateNavButtons();

        StepContent.Content = step switch
        {
            1 => _welcomeStep ??= BuildWelcomeStep(),
            2 => _prerequisitesStep ??= new PrerequisitesStep(OnPrerequisitesChecked, _isUpdate, _role),
            StepPrivacy => _privacyStep ??= BuildPrivacyStep(),
            6 => _skillsStep ??= new SkillsStep(_isUpdate),
            StepInstall => _installStep ??= new InstallStep(),
            StepComplete => _completeStep ??= new CompleteStep(_installedCount, _skippedCount, _installPath, _directorExePath, _isUpdate, _alreadyUpToDate, _cachedPrep?.Version),
            _ => null
        };

        if (step == StepInstall && _isUpdate)
            _installStep?.SetUpdateMode();

        if (step == 2)
            _prerequisitesStep?.RunChecks();

        if (step == StepInstall)
            _ = RunInstallAsync();
    }

    private void UpdateSidebar()
    {
        var stepUIs = GetStepUIs();
        var lines = GetLines();
        var visible = VisibleSteps();
        var accentBrush = (SolidColorBrush)FindResource("AccentBrush");
        var successBrush = (SolidColorBrush)FindResource("SuccessBrush");
        var inactiveBrush = (SolidColorBrush)FindResource("StepInactive");
        var dimBrush = (SolidColorBrush)FindResource("DimText");
        var doneLabelBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));

        // One linear path (issue #1807): every row is always shown, so progress is just the current
        // step's position in the rail. stepUIs is aligned with VisibleSteps(), so row at position i is
        // step id visible[i]; the circle shows i+1.
        var currentPos = visible.IndexOf(_currentStep);

        for (int pos = 0; pos < stepUIs.Count; pos++)
        {
            var ui = stepUIs[pos];
            if (ui.Number != null) ui.Number.Text = (pos + 1).ToString();

            if (pos < currentPos)
            {
                ui.Circle.Background = successBrush;
                ui.Label.Foreground = doneLabelBrush;
                if (ui.Number != null) ui.Number.Foreground = Brushes.White;
            }
            else if (pos == currentPos)
            {
                ui.Circle.Background = accentBrush;
                ui.Label.Foreground = Brushes.White;
                if (ui.Number != null) ui.Number.Foreground = Brushes.White;
            }
            else
            {
                ui.Circle.Background = inactiveBrush;
                ui.Label.Foreground = dimBrush;
                if (ui.Number != null) ui.Number.Foreground = dimBrush;
            }
        }

        // Line i sits between row i and row i+1; it is "done" once the current step is past row i.
        for (int i = 0; i < lines.Length; i++)
            lines[i].Background = i < currentPos ? successBrush : inactiveBrush;
    }

    private void UpdateNavButtons()
    {
        BackButton.Visibility = _currentStep > 1 && _currentStep < StepComplete
            ? Visibility.Visible : Visibility.Collapsed;

        if (_currentStep == StepComplete)
        {
            NextButton.Content = "Close";
        }
        else if (_currentStep == StepInstall)
        {
            NextButton.Content = _isUpdate ? "Updating..." : "Installing...";
            NextButton.IsEnabled = false;
        }
        else if (_currentStep == 2)
        {
            NextButton.Content = "Next";
            UpdateNextButtonForPrereqs();
        }
        else
        {
            // Welcome, Privacy, and every other step: nothing to gate on. The Welcome screen makes no
            // choice anymore (issue #1807), so Next is always enabled here.
            NextButton.Content = "Next";
            NextButton.IsEnabled = true;
        }
    }

    private void OnPrerequisitesChecked(List<PrerequisiteInfo> prerequisites)
    {
        _prerequisites = prerequisites;
        UpdateNextButtonForPrereqs();
    }

    private void UpdateNextButtonForPrereqs()
    {
        if (_currentStep != 2) return;

        var allRequiredMet = _prerequisites.Count == 0 ||
            _prerequisites.Where(p => p.IsRequired).All(p => p.IsFound);
        NextButton.IsEnabled = allRequiredMet;
    }

    private async Task RunInstallAsync()
    {
        SetupLog.Write("[MainWindow] RunInstallAsync: starting");

        var runner = new EngineInstallRunner
        {
            OnProcessBlocking = OnProcessBlockingAsync,
            OnToolsInstalled = c => Dispatcher.BeginInvoke(() => _installStep?.SetToolsInstalledCount(c)),
        };
        _installPath = runner.BinDir;
        _directorExePath = runner.AppExePath;

        _installStep?.SetStatus("Fetching release info...");
        _installStep?.ShowProgress();

        EngineInstallRunner.Prep prep;
        try
        {
            prep = await runner.PrepareAsync();
        }
        catch (GitHubRateLimitException ex)
        {
            SetupLog.Write($"[MainWindow] RunInstallAsync: prepare FAILED (rate limit): {ex.Message}");
            _installStep?.SetStatus(ex.UserMessage());
            NextButton.Content = "Retry";
            NextButton.IsEnabled = true;
            return;
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[MainWindow] RunInstallAsync: prepare FAILED: {ex.Message}");
            _installStep?.SetStatus("ERROR: Could not fetch release info from GitHub.");
            NextButton.Content = "Retry";
            NextButton.IsEnabled = true;
            return;
        }

        _cachedPrep = prep;
        VersionText.Text = prep.Version;
        _installStep?.SetItems(prep.Items);

        // A Gateway machine has the tray app + Cockpit (installed/refreshed by the gateway phase below);
        // show that card up front so the user sees it's part of THIS install/update, not just a
        // Workstation set. On update the role is the one detected from disk, so a Gateway host shows it too.
        if (_role == InstallRole.Gateway)
            _installStep?.ShowGatewaySection();

        if (_isUpdate && prep.IsUpToDate)
        {
            SetupLog.Write($"[MainWindow] Already up to date: {prep.Version}");
            _alreadyUpToDate = true;
            _installStep?.SetUpToDate(prep.Version);
            if (_installStep != null)
                _installStep.OnRepairRequested += OnRepairRequested;
            _installedCount = 0;
            _skippedCount = 0;

            // A Gateway host re-asserts its Gateway + Cockpit even when the Director is already current:
            // the Cockpit can be version-drifted, or the managed tray launch / autostart can be broken
            // (the gateway phase re-extracts the Cockpit, relaunches the tray managed, and re-registers
            // the autostart Run key with --managed). This is what makes re-running the installer reliably
            // heal a Gateway host whose Cockpit is stuck on "Cockpit starting...".
            if (_role == InstallRole.Gateway)
            {
                NextButton.IsEnabled = false;
                _installStep?.ShowGatewaySection();
                await RunGatewayTrayInstallAsync(prep);
            }

            NextButton.Content = "Next";
            NextButton.IsEnabled = true;
            return;
        }

        _installStep?.SetStatus(_isUpdate && _installedVersion != null
            ? $"Updating from v{_installedVersion.Split('+')[0]} to {prep.Version}..."
            : $"Installing {prep.Version}...");

        await RunEngineApplyAsync(runner, prep, repair: false);
    }

    private void OnRepairRequested()
    {
        SetupLog.Write("[MainWindow] OnRepairRequested: user requested repair reinstall");
        _alreadyUpToDate = false;
        _ = RunRepairAsync();
    }

    private async Task RunRepairAsync()
    {
        SetupLog.Write("[MainWindow] RunRepairAsync: starting forced reinstall");

        NextButton.Content = _isUpdate ? "Updating..." : "Installing...";
        NextButton.IsEnabled = false;

        var runner = new EngineInstallRunner
        {
            OnProcessBlocking = OnProcessBlockingAsync,
            OnToolsInstalled = c => Dispatcher.BeginInvoke(() => _installStep?.SetToolsInstalledCount(c)),
        };
        _installPath = runner.BinDir;
        _directorExePath = runner.AppExePath;

        var prep = _cachedPrep ?? await runner.PrepareAsync();
        _cachedPrep = prep;

        _installStep?.SetItems(prep.Items);
        if (_role == InstallRole.Gateway)
            _installStep?.ShowGatewaySection();
        _installStep?.SetStatus($"Repairing {prep.Version}...");
        _installStep?.ShowProgress();

        await RunEngineApplyAsync(runner, prep, repair: true);
    }

    /// <summary>Apply the prepared release via the engine, install skills, and finalize the UI.</summary>
    private async Task RunEngineApplyAsync(EngineInstallRunner runner, EngineInstallRunner.Prep prep, bool repair)
    {
        var (installed, skipped) = await runner.ApplyAsync(prep);
        _installedCount = installed;
        _skippedCount = skipped;

        _installStep?.SetStatus("Installing skills...");
        var skillItems = _installStep?.GetSkillItems() ?? [];
        if (skillItems.Count > 0)
        {
            await new SkillInstaller().InstallSkillsAsync(skillItems);
            _installStep?.UpdateSkillsStatus();
        }

        var verb = repair ? "Repair complete" : "Done";
        _installStep?.SetStatus($"{verb} - {installed} installed, {skipped} skipped");
        SetupLog.Write($"[MainWindow] RunEngineApplyAsync: repair={repair}, installed={installed}, skipped={skipped}");

        // Gateway machine: finish with the Gateway tray app + Cockpit by shelling the CLI (decision D2:
        // the CLI is the single source of truth). Per-user like everything else - no elevation, no UAC.
        // Runs on update too (role detected from disk): it refreshes the Gateway exe + Cockpit and
        // re-asserts the managed tray launch + autostart Run key, so a Gateway host never drifts into a
        // half-updated, unmanaged state where the Cockpit stops coming up.
        if (_role == InstallRole.Gateway)
            await RunGatewayTrayInstallAsync(prep);

        // Start the always-on Launcher tray app (Windows, both roles) AFTER the Gateway phase, so the
        // order matches the CLI. Hard-fail like the CLI: if it does not come up, the install is not
        // "done" - surface the error and offer Retry rather than reporting a clean success while the
        // launcher is dead.
        if (OperatingSystem.IsWindows() && !await StartLauncherAsync())
        {
            NextButton.Content = "Retry";
            NextButton.IsEnabled = true;
            return;
        }

        NextButton.Content = "Next";
        NextButton.IsEnabled = true;
    }

    /// <summary>
    /// Start the installed Launcher tray app in managed mode and verify it is healthy and
    /// autostart-registered (the runner placed cc-launcher.exe but does not start it). Returns false
    /// on any failure so the caller can hard-fail the install with a Retry, mirroring the CLI.
    /// </summary>
    private async Task<bool> StartLauncherAsync()
    {
        SetupLog.Write("[MainWindow] StartLauncherAsync");
        _installStep?.SetStatus("Starting the Launcher tray app...");
        try
        {
            var result = await new LauncherTrayInstaller(InstallLayout.Default()).InstallAsync();
            foreach (var s in result.Steps) SetupLog.Write($"[MainWindow]   launcher: {s}");
            SetupLog.Write($"[MainWindow] launcher start success={result.Success}: {result.Message}");
            if (result.Success) return true;
            _installStep?.SetStatus($"ERROR: Launcher tray app failed to start. {result.Message}");
            return false;
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[MainWindow] StartLauncherAsync FAILED: {ex.Message}");
            _installStep?.SetStatus($"ERROR: Launcher tray app failed to start. {ex.Message}");
            return false;
        }
    }

    private async Task RunGatewayTrayInstallAsync(EngineInstallRunner.Prep prep)
    {
        SetupLog.Write("[MainWindow] RunGatewayTrayInstallAsync: shelling the CLI");
        _installStep?.SetStatus("Installing the Gateway tray app...");
        _installStep?.SetGatewayInstalling();

        try
        {
            var launcher = new GatewayTrayLauncher(new ReleaseSource());
            var result = await launcher.RunAsync(
                prep.Release,
                line => Dispatcher.BeginInvoke(() => _installStep?.SetStatus($"Gateway: {line}")));

            _gatewayResultMessage = result.Message;
            // result.Message already carries the tailnet Cockpit URL (never localhost).
            _installStep?.SetStatus(result.Message);
            if (result.Success) _installStep?.SetGatewayDone();
            else _installStep?.SetGatewayFailed();
            // A failed Gateway refresh is a component that did NOT install: count it so the Complete
            // step reports the honest failure instead of "Everything went perfectly." Painting the row
            // failed without this let the wizard report a clean success while the Gateway never updated.
            _skippedCount = InstallCompletion.SkippedAfterGateway(_skippedCount, result.Success);
            SetupLog.Write($"[MainWindow] Gateway install success={result.Success}: {result.Message} (skipped now {_skippedCount})");
        }
        catch (Exception ex)
        {
            _gatewayResultMessage = $"Gateway install error: {ex.Message}";
            _installStep?.SetStatus(_gatewayResultMessage);
            _installStep?.SetGatewayFailed();
            _skippedCount = InstallCompletion.SkippedAfterGateway(_skippedCount, gatewaySuccess: false);
            SetupLog.Write($"[MainWindow] RunGatewayTrayInstallAsync FAILED: {ex.Message} (skipped now {_skippedCount})");
        }
    }

    private Task<bool> OnProcessBlockingAsync(string processName)
    {
        var result = MessageBox.Show(
            this,
            "DevThrottle is currently running and cannot be updated.\n\n" +
            "Please close DevThrottle and click OK to retry,\n" +
            "or click Cancel to skip updating the main application.",
            "DevThrottle is Running",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        return Task.FromResult(result == MessageBoxResult.OK);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
            ShowStep(PrevStep(_currentStep));
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep == StepComplete)
        {
            Close();
            return;
        }

        if (_currentStep == StepInstall && NextButton.Content?.ToString() == "Retry")
        {
            _installStep = null;
            ShowStep(StepInstall);
            return;
        }

        if (_currentStep < StepComplete)
        {
            // Leaving Privacy (issue #659): apply the telemetry choice. This writes the per-account
            // server flag (best-effort) and always mirrors the choice to the local config.json. It must
            // never block the wizard - the toggle is a choice, not a gate - so we fire it detached and
            // proceed to the next step immediately regardless of the toggle value or the call outcome.
            if (_currentStep == StepPrivacy)
                ApplyPrivacyChoiceBestEffort();

            // Leaving Install: rebuild Complete with the final counts.
            if (_currentStep == StepInstall)
                _completeStep = null;

            ShowStep(NextStep(_currentStep));
        }
    }

    /// <summary>
    /// Applies the Privacy step's telemetry choice fully detached so a slow or failed telemetry call can
    /// never block the wizard (issue #659). <see cref="PrivacyStep.ApplyChoiceAsync"/> itself never
    /// throws and writes the local config.json mirror either way; this only logs the completion.
    /// </summary>
    private void ApplyPrivacyChoiceBestEffort()
    {
        var step = _privacyStep;
        if (step is null)
            return;

        // Snapshot the checkbox state and the token on the UI thread; the detached apply touches no UI.
        var snapshot = step.SnapshotChoice();
        SetupLog.Write($"[MainWindow] ApplyPrivacyChoiceBestEffort: applying telemetry choice (detached, non-blocking), enabled={snapshot.Enabled}");
        _ = Task.Run(async () =>
        {
            try
            {
                await step.ApplyChoiceAsync(snapshot);
                SetupLog.Write("[MainWindow] ApplyPrivacyChoiceBestEffort: telemetry choice applied");
            }
            catch (Exception ex)
            {
                SetupLog.Write($"[MainWindow] ApplyPrivacyChoiceBestEffort: applying telemetry choice failed (ignored, best-effort): {ex.Message}");
            }
        });
    }
}
