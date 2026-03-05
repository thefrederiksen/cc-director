using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CcDirectorSetup.Models;
using CcDirectorSetup.Services;
using CcDirectorSetup.Steps;

namespace CcDirectorSetup;

public partial class MainWindow : Window
{
    private int _currentStep = 1;
    private InstallProfile _selectedProfile = InstallProfile.Standard;
    private List<PrerequisiteInfo> _prerequisites = [];
    private int _installedCount;
    private int _skippedCount;
    private string _installPath = "";

    private WelcomeStep? _welcomeStep;
    private PrerequisitesStep? _prerequisitesStep;
    private InstallStep? _installStep;
    private CompleteStep? _completeStep;

    // Sidebar UI elements per step
    private readonly record struct StepUI(Border Circle, TextBlock Label, TextBlock? Number);

    public MainWindow()
    {
        InitializeComponent();
        SetupLog.Write("[MainWindow] Started");
        ShowStep(1);
    }

    private List<StepUI> GetStepUIs() =>
    [
        new(Step1Circle, Step1Label, null),
        new(Step2Circle, Step2Label, Step2Num),
        new(Step3Circle, Step3Label, Step3Num),
        new(Step4Circle, Step4Label, Step4Num),
    ];

    private Border[] GetLines() => [Line12, Line23, Line34];

    private void ShowStep(int step)
    {
        SetupLog.Write($"[MainWindow] ShowStep: step={step}");
        _currentStep = step;

        UpdateSidebar();
        UpdateNavButtons();

        StepContent.Content = step switch
        {
            1 => _welcomeStep ??= new WelcomeStep(_selectedProfile, p => _selectedProfile = p),
            2 => _prerequisitesStep ??= new PrerequisitesStep(OnPrerequisitesChecked),
            3 => _installStep ??= new InstallStep(),
            4 => _completeStep ??= new CompleteStep(_installedCount, _skippedCount, _installPath),
            _ => null
        };

        // Trigger prerequisite check when entering step 2
        if (step == 2)
            _prerequisitesStep?.RunChecks();

        // Trigger install when entering step 3
        if (step == 3)
            _ = RunInstallAsync();
    }

    private void UpdateSidebar()
    {
        var stepUIs = GetStepUIs();
        var lines = GetLines();
        var accentBrush = (SolidColorBrush)FindResource("AccentBrush");
        var successBrush = (SolidColorBrush)FindResource("SuccessBrush");
        var inactiveBrush = (SolidColorBrush)FindResource("StepInactive");
        var dimBrush = (SolidColorBrush)FindResource("DimText");

        for (int i = 0; i < stepUIs.Count; i++)
        {
            var stepNum = i + 1;
            var ui = stepUIs[i];

            if (stepNum < _currentStep)
            {
                // Completed
                ui.Circle.Background = successBrush;
                ui.Label.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
                if (ui.Number != null) ui.Number.Foreground = Brushes.White;
            }
            else if (stepNum == _currentStep)
            {
                // Active
                ui.Circle.Background = accentBrush;
                ui.Label.Foreground = Brushes.White;
                if (ui.Number != null) ui.Number.Foreground = Brushes.White;
            }
            else
            {
                // Upcoming
                ui.Circle.Background = inactiveBrush;
                ui.Label.Foreground = dimBrush;
                if (ui.Number != null) ui.Number.Foreground = dimBrush;
            }

            // Lines
            if (i < lines.Length)
            {
                lines[i].Background = stepNum < _currentStep ? successBrush : inactiveBrush;
            }
        }
    }

    private void UpdateNavButtons()
    {
        BackButton.Visibility = _currentStep > 1 && _currentStep < 4
            ? Visibility.Visible : Visibility.Collapsed;

        if (_currentStep == 4)
        {
            NextButton.Content = "Close";
        }
        else if (_currentStep == 3)
        {
            NextButton.Content = "Installing...";
            NextButton.IsEnabled = false;
        }
        else if (_currentStep == 2)
        {
            NextButton.Content = "Next";
            UpdateNextButtonForPrereqs();
        }
        else
        {
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

        var installer = new ToolInstaller();
        _installPath = installer.InstallDir;

        var toolNames = ProfileToolLists.GetToolsForProfile(_selectedProfile);
        var downloadItems = installer.BuildDownloadList(toolNames);

        _installStep?.SetItems(downloadItems);
        _installStep?.SetStatus("Fetching release info...");
        _installStep?.ShowProgress();

        var github = new GitHubReleaseService();
        var releaseResult = await github.GetLatestReleaseAsync();

        if (releaseResult == null)
        {
            _installStep?.SetStatus("ERROR: Could not fetch release info from GitHub.");
            SetupLog.Write("[MainWindow] RunInstallAsync: no release found");
            NextButton.Content = "Retry";
            NextButton.IsEnabled = true;
            return;
        }

        var (version, assets) = releaseResult.Value;
        VersionText.Text = version;
        _installStep?.SetStatus($"Installing from {version}...");

        var (installed, skipped) = await installer.InstallToolsAsync(downloadItems, assets);
        _installedCount = installed;
        _skippedCount = skipped;

        // Add to PATH
        PathManager.AddToPath(_installPath);

        // Install skills
        _installStep?.SetStatus("Installing skills...");
        var skillItems = _installStep?.GetSkillItems() ?? [];
        if (skillItems.Count > 0)
        {
            await installer.InstallSkillsAsync(skillItems);
            _installStep?.UpdateSkillsStatus();
        }

        _installStep?.SetStatus($"Done - {installed} tools installed, {skipped} skipped");
        SetupLog.Write($"[MainWindow] RunInstallAsync: complete, installed={installed}, skipped={skipped}");

        NextButton.Content = "Next";
        NextButton.IsEnabled = true;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
            ShowStep(_currentStep - 1);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep == 4)
        {
            Close();
            return;
        }

        if (_currentStep == 3 && NextButton.Content?.ToString() == "Retry")
        {
            _installStep = null;
            ShowStep(3);
            return;
        }

        if (_currentStep < 4)
        {
            // Reset forward steps when going forward from profile selection
            if (_currentStep == 1)
            {
                _welcomeStep?.UpdateProfile(ref _selectedProfile);
                _prerequisitesStep = null;
                _installStep = null;
                _completeStep = null;
            }

            if (_currentStep == 3)
                _completeStep = null; // Rebuild with final counts

            ShowStep(_currentStep + 1);
        }
    }
}
