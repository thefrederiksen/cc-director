using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CcDirectorSetup.Models;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

public partial class WelcomeStep : UserControl
{
    private InstallProfile _profile;
    private readonly Action<InstallProfile> _onProfileChanged;

    public WelcomeStep(InstallProfile initial, Action<InstallProfile> onProfileChanged,
        bool isUpdate, string? installedVersion)
    {
        InitializeComponent();
        _profile = initial;
        _onProfileChanged = onProfileChanged;
        UpdateSelection();

        if (isUpdate)
        {
            TitleText.Text = "Update CC Director";
            DescriptionText.Text = "Checking for updates...";
            ProfilePromptText.Text = "Update profile:";

            if (installedVersion != null)
            {
                var displayVersion = installedVersion.Split('+')[0];
                VersionInfoText.Text = $"Currently installed: v{displayVersion}";
                VersionInfoText.Visibility = Visibility.Visible;
            }
        }

        SetupLog.Write($"[WelcomeStep] Created: profile={initial}, isUpdate={isUpdate}");
    }

    public void UpdateVersionInfo(string? installedVersion, string? latestVersion)
    {
        SetupLog.Write($"[WelcomeStep] UpdateVersionInfo: installed={installedVersion}, latest={latestVersion}");

        Dispatcher.BeginInvoke(() =>
        {
            if (installedVersion == null || latestVersion == null)
                return;

            var installedClean = installedVersion.Split('+')[0].TrimStart('v');
            var latestClean = latestVersion.TrimStart('v');

            if (installedClean == latestClean)
            {
                DescriptionText.Text = "No upgrade available. You can reinstall tools as a repair.";
                VersionInfoText.Text = $"Installed: v{installedClean} (latest)";
                VersionInfoText.Visibility = Visibility.Visible;
            }
            else
            {
                DescriptionText.Text = $"Upgrade available: v{installedClean} -> v{latestClean}";
                VersionInfoText.Text = $"Currently installed: v{installedClean}";
                VersionInfoText.Visibility = Visibility.Visible;
            }
        });
    }

    public void UpdateProfile(ref InstallProfile profile)
    {
        profile = _profile;
    }

    public void UpdateProfile(InstallProfile profile)
    {
        _profile = profile;
        UpdateSelection();
        SetupLog.Write($"[WelcomeStep] UpdateProfile: profile={profile}");
    }

    private void DeveloperCard_Click(object sender, MouseButtonEventArgs e)
    {
        _profile = InstallProfile.Developer;
        _onProfileChanged(_profile);
        UpdateSelection();
        SetupLog.Write("[WelcomeStep] Selected Developer profile");
    }

    private void StandardCard_Click(object sender, MouseButtonEventArgs e)
    {
        _profile = InstallProfile.Standard;
        _onProfileChanged(_profile);
        UpdateSelection();
        SetupLog.Write("[WelcomeStep] Selected Standard profile");
    }

    private void UpdateSelection()
    {
        var accentBrush = (SolidColorBrush)FindResource("AccentBrush");
        var inactiveBrush = (SolidColorBrush)FindResource("StepInactive");
        var dimBrush = (SolidColorBrush)FindResource("DimText");

        if (_profile == InstallProfile.Developer)
        {
            DeveloperCard.BorderBrush = accentBrush;
            DeveloperRadio.Text = "(*)";
            DeveloperRadio.Foreground = accentBrush;

            StandardCard.BorderBrush = inactiveBrush;
            StandardRadio.Text = "( )";
            StandardRadio.Foreground = dimBrush;
        }
        else
        {
            DeveloperCard.BorderBrush = inactiveBrush;
            DeveloperRadio.Text = "( )";
            DeveloperRadio.Foreground = dimBrush;

            StandardCard.BorderBrush = accentBrush;
            StandardRadio.Text = "(*)";
            StandardRadio.Foreground = accentBrush;
        }
    }
}
