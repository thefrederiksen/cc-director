using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirectorSetup.Models;
using CcDirectorSetup.Services;
using CcDirectorSetup.Steps;

namespace CcDirectorSetup;

public partial class MainWindow : Window
{
    private int _currentStep = 1;
    private InstallProfile _selectedProfile = InstallProfile.Standard;
    private List<string> _selectedGroups;
    private List<PrerequisiteInfo> _prerequisites = [];
    private int _installedCount;
    private int _skippedCount;
    private string _installPath = "";

    private readonly bool _isUpdate;
    private readonly string? _installedVersion;
    private bool _alreadyUpToDate;
    private string? _latestVersion;
    private string? _cachedVersion;
    private Dictionary<string, AssetInfo>? _cachedAssets;

    private WelcomeStep? _welcomeStep;
    private PrerequisitesStep? _prerequisitesStep;
    private ToolsStep? _toolsStep;
    private SkillsStep? _skillsStep;
    private InstallStep? _installStep;
    private CompleteStep? _completeStep;

    private readonly record struct StepUI(Border Circle, TextBlock Label, TextBlock? Number);

    public MainWindow()
    {
        InitializeComponent();

        _isUpdate = InstallDetector.IsInstalled();
        _installedVersion = _isUpdate ? InstallDetector.GetInstalledVersion() : null;
        _selectedGroups = ToolGroupRegistry.GetDefaultGroupNames();

        SetupLog.Write($"[MainWindow] Started: isUpdate={_isUpdate}, installedVersion={_installedVersion}");

        if (_isUpdate)
        {
            Title = "CC Director Update";
            SubtitleText.Text = "Update";
            Step5Label.Text = "Update";
        }

        Loaded += MainWindow_Loaded;
        ShowStep(1);
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            var saved = await Task.Run(() => ProfileStore.Load());
            if (saved != null)
            {
                _selectedProfile = saved.Profile;
                _selectedGroups = saved.Groups;
                _welcomeStep?.UpdateProfile(_selectedProfile);
                SetupLog.Write($"[MainWindow] Restored: profile={_selectedProfile}, groups={_selectedGroups.Count}");
            }
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[MainWindow] Failed to load saved settings: {ex.Message}");
        }

        if (_isUpdate)
            await FetchLatestVersionAsync();
    }

    private async Task FetchLatestVersionAsync()
    {
        SetupLog.Write("[MainWindow] FetchLatestVersionAsync: checking for latest release");

        try
        {
            var github = new GitHubReleaseService();
            var result = await github.GetLatestReleaseAsync();

            if (result != null)
            {
                _latestVersion = result.Value.version;
                SetupLog.Write($"[MainWindow] FetchLatestVersionAsync: latestVersion={_latestVersion}");
                _welcomeStep?.UpdateVersionInfo(_installedVersion, _latestVersion);
            }
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[MainWindow] FetchLatestVersionAsync FAILED: {ex.Message}");
        }
    }

    private List<StepUI> GetStepUIs() =>
    [
        new(Step1Circle, Step1Label, null),
        new(Step2Circle, Step2Label, Step2Num),
        new(Step3Circle, Step3Label, Step3Num),
        new(Step4Circle, Step4Label, Step4Num),
        new(Step5Circle, Step5Label, Step5Num),
        new(Step6Circle, Step6Label, Step6Num),
    ];

    private Border[] GetLines() => [Line12, Line23, Line34, Line45, Line56];

    private void ShowStep(int step)
    {
        SetupLog.Write($"[MainWindow] ShowStep: step={step}");
        _currentStep = step;

        UpdateSidebar();
        UpdateNavButtons();

        StepContent.Content = step switch
        {
            1 => _welcomeStep ??= new WelcomeStep(_selectedProfile, p => _selectedProfile = p, _isUpdate, _installedVersion),
            2 => _prerequisitesStep ??= new PrerequisitesStep(OnPrerequisitesChecked, _isUpdate),
            3 => _toolsStep ??= new ToolsStep(_selectedGroups, g => _selectedGroups = g, _isUpdate),
            4 => _skillsStep ??= new SkillsStep(_isUpdate),
            5 => _installStep ??= new InstallStep(),
            6 => _completeStep ??= new CompleteStep(_installedCount, _skippedCount, _installPath, _isUpdate, _alreadyUpToDate),
            _ => null
        };

        if (step == 5 && _isUpdate)
            _installStep?.SetUpdateMode();

        if (step == 2)
            _prerequisitesStep?.RunChecks();

        if (step == 5)
            _ = RunInstallAsync();
    }

    private void UpdateSidebar()
    {
        var stepUIs = GetStepUIs();
        var lines = GetLines();
        var accentBrush = SolidColorBrush.Parse("#007ACC");
        var successBrush = SolidColorBrush.Parse("#22C55E");
        var inactiveBrush = SolidColorBrush.Parse("#3C3C3C");
        var dimBrush = SolidColorBrush.Parse("#888888");
        var whiteBrush = SolidColorBrush.Parse("#CCCCCC");

        for (int i = 0; i < stepUIs.Count; i++)
        {
            var stepNum = i + 1;
            var ui = stepUIs[i];

            if (stepNum < _currentStep)
            {
                ui.Circle.Background = successBrush;
                ui.Label.Foreground = whiteBrush;
                if (ui.Number != null) ui.Number.Foreground = Brushes.White;
            }
            else if (stepNum == _currentStep)
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

            if (i < lines.Length)
            {
                lines[i].Background = stepNum < _currentStep ? successBrush : inactiveBrush;
            }
        }
    }

    private void UpdateNavButtons()
    {
        BackButton.IsVisible = _currentStep > 1 && _currentStep < 6;

        if (_currentStep == 6)
        {
            NextButton.Content = "Close";
        }
        else if (_currentStep == 5)
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
        installer.OnProcessBlocking = OnProcessBlockingAsync;
        _installPath = installer.InstallDir;

        var toolNames = ToolGroupRegistry.GetToolsForGroups(_selectedGroups);
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

        if (_isUpdate && _installedVersion != null)
        {
            var installedSemVer = _installedVersion.Split('+')[0].TrimStart('v');
            var releaseSemVer = version.TrimStart('v');

            if (installedSemVer == releaseSemVer)
            {
                SetupLog.Write($"[MainWindow] Already up to date: installed={installedSemVer}, release={releaseSemVer}");
                _alreadyUpToDate = true;
                _cachedVersion = version;
                _cachedAssets = assets;
                SaveSettingsSafe();
                _installStep?.SetUpToDate(version);
                if (_installStep != null)
                    _installStep.OnRepairRequested += OnRepairRequested;
                _installedCount = 0;
                _skippedCount = 0;
                NextButton.Content = "Next";
                NextButton.IsEnabled = true;
                return;
            }
        }

        var statusText = _isUpdate && _installedVersion != null
            ? $"Updating from v{_installedVersion.Split('+')[0]} to {version}..."
            : $"Installing {version}...";
        _installStep?.SetStatus(statusText);

        var (installed, skipped) = await installer.InstallToolsAsync(downloadItems, assets);
        _installedCount = installed;
        _skippedCount = skipped;

        PathManager.AddToPath(_installPath);

        var directorBin = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows)
            ? Path.Combine(_installPath, "cc-director.exe")
            : Path.Combine(_installPath, "cc-director");

        if (File.Exists(directorBin))
        {
            _installStep?.SetStatus("Creating shortcut...");
            ShortcutCreator.CreateShortcut(directorBin);
        }

        _installStep?.SetStatus("Installing skills...");
        var skillItems = _installStep?.GetSkillItems() ?? [];
        if (skillItems.Count > 0)
        {
            await installer.InstallSkillsAsync(skillItems);
            _installStep?.UpdateSkillsStatus();
        }

        SaveSettingsSafe();

        _installStep?.SetStatus($"Done - {installed} tools installed, {skipped} skipped");
        SetupLog.Write($"[MainWindow] RunInstallAsync: complete, installed={installed}, skipped={skipped}");

        NextButton.Content = "Next";
        NextButton.IsEnabled = true;
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

        if (_cachedVersion == null || _cachedAssets == null)
        {
            SetupLog.Write("[MainWindow] RunRepairAsync: no cached release data");
            return;
        }

        NextButton.Content = _isUpdate ? "Updating..." : "Installing...";
        NextButton.IsEnabled = false;

        var installer = new ToolInstaller();
        installer.OnProcessBlocking = OnProcessBlockingAsync;
        _installPath = installer.InstallDir;

        var toolNames = ToolGroupRegistry.GetToolsForGroups(_selectedGroups);
        var downloadItems = installer.BuildDownloadList(toolNames);

        _installStep?.SetItems(downloadItems);
        _installStep?.SetStatus($"Repairing {_cachedVersion}...");
        _installStep?.ShowProgress();

        var (installed, skipped) = await installer.InstallToolsAsync(downloadItems, _cachedAssets);
        _installedCount = installed;
        _skippedCount = skipped;

        PathManager.AddToPath(_installPath);

        var directorBin = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows)
            ? Path.Combine(_installPath, "cc-director.exe")
            : Path.Combine(_installPath, "cc-director");

        if (File.Exists(directorBin))
        {
            _installStep?.SetStatus("Creating shortcut...");
            ShortcutCreator.CreateShortcut(directorBin);
        }

        _installStep?.SetStatus("Installing skills...");
        var skillItems = _installStep?.GetSkillItems() ?? [];
        if (skillItems.Count > 0)
        {
            await installer.InstallSkillsAsync(skillItems);
            _installStep?.UpdateSkillsStatus();
        }

        SaveSettingsSafe();

        _installStep?.SetStatus($"Repair complete - {installed} tools installed, {skipped} skipped");
        SetupLog.Write($"[MainWindow] RunRepairAsync: complete, installed={installed}, skipped={skipped}");

        NextButton.Content = "Next";
        NextButton.IsEnabled = true;
    }

    private void SaveSettingsSafe()
    {
        try
        {
            var settings = new SavedSettings(_selectedProfile, _selectedGroups);
            ProfileStore.Save(settings);
            SetupLog.Write($"[MainWindow] SaveSettingsSafe: profile={_selectedProfile}, groups={_selectedGroups.Count}");
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[MainWindow] SaveSettingsSafe FAILED: {ex.Message}");
        }
    }

    private async Task<bool> OnProcessBlockingAsync(string processName)
    {
        // Use a simple dialog in Avalonia
        var dialog = new Window
        {
            Title = "CC Director is Running",
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = SolidColorBrush.Parse("#1E1E1E"),
            CanResize = false,
        };

        var result = false;
        var panel = new StackPanel { Margin = new Avalonia.Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "CC Director is currently running and cannot be updated.\n\nPlease close CC Director and click OK to retry,\nor click Cancel to skip.",
            Foreground = SolidColorBrush.Parse("#CCCCCC"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 13,
        });

        var btnPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Avalonia.Thickness(0, 16, 0, 0) };
        var okBtn = new Button { Content = "OK", Width = 80, Classes = { "primary" }, Margin = new Avalonia.Thickness(0, 0, 8, 0) };
        var cancelBtn = new Button { Content = "Cancel", Width = 80, Classes = { "setup" } };

        okBtn.Click += (_, _) => { result = true; dialog.Close(); };
        cancelBtn.Click += (_, _) => { result = false; dialog.Close(); };

        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(btnPanel);
        dialog.Content = panel;

        await dialog.ShowDialog(this);
        return result;
    }

    private void BackButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
            ShowStep(_currentStep - 1);
    }

    private void NextButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentStep == 6)
        {
            Close();
            return;
        }

        if (_currentStep == 5 && NextButton.Content?.ToString() == "Retry")
        {
            _installStep = null;
            ShowStep(5);
            return;
        }

        if (_currentStep < 6)
        {
            if (_currentStep == 1)
            {
                _welcomeStep?.UpdateProfile(ref _selectedProfile);
                _prerequisitesStep = null;
                _toolsStep = null;
                _skillsStep = null;
                _installStep = null;
                _completeStep = null;
            }

            if (_currentStep == 3)
            {
                _selectedGroups = _toolsStep?.GetEnabledGroups() ?? _selectedGroups;
                _skillsStep = null;
                _installStep = null;
                _completeStep = null;
            }

            if (_currentStep == 5)
                _completeStep = null;

            ShowStep(_currentStep + 1);
        }
    }
}
