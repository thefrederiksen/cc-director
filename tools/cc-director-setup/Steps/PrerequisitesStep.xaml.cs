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
    private readonly CcDirector.Setup.Engine.InstallRole _role;
    private List<PrerequisiteInfo> _items;

    public PrerequisitesStep(Action<List<PrerequisiteInfo>> onChecksComplete, bool isUpdate, CcDirector.Setup.Engine.InstallRole role)
    {
        InitializeComponent();
        _onChecksComplete = onChecksComplete;
        _role = role;
        _items = PrerequisiteChecker.CreateChecklist(_role);
        PrereqList.ItemsSource = _items;

        if (isUpdate)
            SubtitleText.Text = "Verifying your environment...";

        SetupLog.Write($"[PrerequisitesStep] Created: isUpdate={isUpdate}, role={_role}");
    }

    public async void RunChecks()
    {
        SetupLog.Write("[PrerequisitesStep] RunChecks: starting");
        RefreshButton.IsEnabled = false;

        _items = PrerequisiteChecker.CreateChecklist(_role);
        PrereqList.ItemsSource = _items;

        await PrerequisiteChecker.CheckAllAsync(_items);

        RefreshButton.IsEnabled = true;

        var allRequiredMet = PrerequisiteChecker.AllRequiredMet(_items);
        var missingRecommended = _items.Count(p => p.IsRecommended && !p.IsFound);
        if (allRequiredMet)
        {
            // Honest about the recommended ones: they no longer block, but saying only "all
            // found" while three rows sit unfound would read as a lie against the screen.
            SubtitleText.Text = missingRecommended == 0
                ? "All prerequisites found."
                : $"Ready to install. {missingRecommended} recommended item(s) are missing - "
                  + "you can install them here, or later.";
            SuccessBanner.Visibility = Visibility.Visible;
        }
        else
        {
            SubtitleText.Text = "Some required prerequisites are missing. Install them and re-check.";
            SuccessBanner.Visibility = Visibility.Collapsed;
        }

        _onChecksComplete(_items);
        SetupLog.Write($"[PrerequisitesStep] RunChecks: complete, allRequiredMet={allRequiredMet}");
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RunChecks();
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[PrerequisitesStep] InstallButton_Click");
        try
        {
            if (sender is not Button { DataContext: PrerequisiteInfo item } button
                || string.IsNullOrWhiteSpace(item.WingetId))
            {
                SetupLog.Write("[PrerequisitesStep] InstallButton_Click: no winget id on item");
                return;
            }

            button.IsEnabled = false;
            RefreshButton.IsEnabled = false;
            item.Status = "Installing...";

            var result = await RuntimeInstaller.InstallAsync(item.WingetId, item.Name);
            SetupLog.Write($"[PrerequisitesStep] InstallButton_Click: success={result.Success}, {result.Message}");

            if (result.Success)
            {
                // Re-check so the row flips to Found and the install action hides itself.
                RunChecks();
            }
            else
            {
                // The row says WHAT is wrong in two words, the subtitle says what to do about it.
                // Both come from RuntimeInstallDiagnosis so they can never disagree.
                item.Status = RuntimeInstallDiagnosis.RowStatus(result.Failure ?? RuntimeInstallFailure.Other);
                SubtitleText.Text = result.Message;
                button.IsEnabled = true;
                RefreshButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[PrerequisitesStep] InstallButton_Click FAILED: {ex}");
            SubtitleText.Text = "The install did not finish. Use the download link, then click Re-check. "
                                + $"The details are in the setup log: {SetupLog.Path}";
            RefreshButton.IsEnabled = true;
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        SetupLog.Write($"[PrerequisitesStep] Opening URL: {e.Uri}");
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
