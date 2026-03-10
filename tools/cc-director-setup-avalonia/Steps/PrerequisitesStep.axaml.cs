using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirectorSetup.Models;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

public partial class PrerequisitesStep : UserControl
{
    private readonly Action<List<PrerequisiteInfo>> _onChecksComplete;
    private List<PrerequisiteInfo> _items;

    public PrerequisitesStep(Action<List<PrerequisiteInfo>> onChecksComplete, bool isUpdate)
    {
        InitializeComponent();
        _onChecksComplete = onChecksComplete;
        _items = PrerequisiteChecker.CreateChecklist();
        PrereqList.ItemsSource = _items;

        if (isUpdate)
            SubtitleText.Text = "Verifying your environment...";

        SetupLog.Write($"[PrerequisitesStep] Created: isUpdate={isUpdate}");
    }

    public async void RunChecks()
    {
        SetupLog.Write("[PrerequisitesStep] RunChecks: starting");
        RefreshButton.IsEnabled = false;

        _items = PrerequisiteChecker.CreateChecklist();
        PrereqList.ItemsSource = _items;

        await PrerequisiteChecker.CheckAllAsync(_items);

        RefreshButton.IsEnabled = true;

        var allMet = _items.All(p => p.IsFound);
        if (allMet)
        {
            SubtitleText.Text = "All prerequisites found.";
            SuccessBanner.IsVisible = true;
        }
        else
        {
            SubtitleText.Text = "Some prerequisites are missing. Install them and re-check.";
            SuccessBanner.IsVisible = false;
        }

        _onChecksComplete(_items);
        SetupLog.Write($"[PrerequisitesStep] RunChecks: complete, allMet={allMet}");
    }

    private void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        RunChecks();
    }
}
