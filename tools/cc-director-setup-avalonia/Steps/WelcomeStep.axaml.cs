using Avalonia.Controls;
using Avalonia.Threading;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

public partial class WelcomeStep : UserControl
{
    public WelcomeStep()
    {
        InitializeComponent();
    }

    public WelcomeStep(bool isUpdate, string? installedVersion, InstallRole installedRole = InstallRole.Workstation)
    {
        InitializeComponent();

        if (isUpdate)
        {
            TitleText.Text = "Update DevThrottle";
            DescriptionText.Text = "Checking for updates...";

            // An update refreshes whatever is already installed. Show, read-only, which type this
            // machine actually is (a first-install choice is never re-asked); a fresh install no
            // longer makes this choice at all (issue #1807). macOS is Workstation-only today, so
            // the detector always answers Workstation here - the panel matches Windows anyway so
            // the two wizards read identically.
            InstalledRoleText.Text = installedRole == InstallRole.Gateway
                ? "Gateway -- DevThrottle app, all CLI tools, plus the Gateway tray app and Cockpit web UI. There should be only one Gateway."
                : "Workstation -- DevThrottle app + all CLI tools on this machine. Connects to a Gateway; it is not the Gateway itself.";
            InstalledRolePanel.IsVisible = true;

            if (installedVersion != null)
            {
                var displayVersion = installedVersion.Split('+')[0];
                VersionInfoText.Text = $"Currently installed: v{displayVersion}";
                VersionInfoText.IsVisible = true;
            }
        }
        // Fresh install: there is no decision on this screen. The installer lays down the Director set
        // (DevThrottle app + every cc-* tool + the Launcher) with no account and no gateway (issue
        // #1807); connecting a gateway is a later, optional step done from the app. The XAML defaults
        // already show the "what gets installed" description and the "Click Next" hint, so there is
        // nothing to toggle here.

        SetupLog.Write($"[WelcomeStep] Created: isUpdate={isUpdate}");
    }

    public void UpdateVersionInfo(string? installedVersion, string? latestVersion)
    {
        SetupLog.Write($"[WelcomeStep] UpdateVersionInfo: installed={installedVersion}, latest={latestVersion}");

        Dispatcher.UIThread.Post(() =>
        {
            if (installedVersion == null || latestVersion == null)
                return;

            var installedClean = installedVersion.Split('+')[0].TrimStart('v');
            var latestClean = latestVersion.TrimStart('v');

            if (installedClean == latestClean)
            {
                DescriptionText.Text = "No upgrade available. You can reinstall tools as a repair.";
                VersionInfoText.Text = $"Installed: v{installedClean} (latest)";
                VersionInfoText.IsVisible = true;
            }
            else
            {
                DescriptionText.Text = $"Upgrade available: v{installedClean} -> v{latestClean}";
                VersionInfoText.Text = $"Currently installed: v{installedClean}";
                VersionInfoText.IsVisible = true;
            }
        });
    }
}
