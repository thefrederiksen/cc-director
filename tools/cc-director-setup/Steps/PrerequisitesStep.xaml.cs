using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using CcDirectorSetup.Models;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

public partial class PrerequisitesStep : UserControl
{
    private readonly Action<List<PrerequisiteInfo>> _onChecksComplete;
    private List<PrerequisiteInfo> _items;

    public PrerequisitesStep(Action<List<PrerequisiteInfo>> onChecksComplete)
    {
        InitializeComponent();
        _onChecksComplete = onChecksComplete;
        _items = PrerequisiteChecker.CreateChecklist();
        PrereqList.ItemsSource = _items;
        SetupLog.Write("[PrerequisitesStep] Created");
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
            SuccessBanner.Visibility = Visibility.Visible;
        }
        else
        {
            SubtitleText.Text = "Some prerequisites are missing. Install them and re-check.";
            SuccessBanner.Visibility = Visibility.Collapsed;
        }

        _onChecksComplete(_items);
        SetupLog.Write($"[PrerequisitesStep] RunChecks: complete, allMet={allMet}");
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RunChecks();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        SetupLog.Write($"[PrerequisitesStep] Opening URL: {e.Uri}");
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
