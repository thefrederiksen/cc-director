using System.Windows;
using System.Windows.Controls;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Steps;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// Tests for the installer's Welcome step after it became Director-only with no account gate (issue
/// #1807). A fresh install makes NO decision on this screen - there is no role picker at all: the step
/// just shows what gets installed and a "Click Next" hint. Update mode still shows, read-only, which
/// install type this machine is (from InstalledRoleDetector) plus the Uninstall entry (issue #257).
///
/// <see cref="WelcomeStep"/> is a WPF UserControl whose XAML binds App-level static resources, so all
/// cases run on ONE shared STA thread that owns a single <see cref="Application"/> with App.xaml's
/// resources loaded (resource lookup is thread-affine, so every control must be built on the thread
/// that owns the Application). The shared thread is provided by <see cref="WpfStaFixture"/>.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class WelcomeStepTests
{
    private readonly WpfStaFixture _wpf;

    public WelcomeStepTests(WpfStaFixture wpf) => _wpf = wpf;

    [Fact]
    public void FreshInstall_ShowsNoRolePicker_AndNoInstalledRoleOrUninstall() =>
        _wpf.Run(() =>
        {
            var step = new WelcomeStep(isUpdate: false, installedVersion: null);

            // The reframed "do you already have a gateway?" role cards are gone entirely - there is no
            // decision to make on a fresh install.
            Assert.Null(step.FindName("RolePanel"));
            Assert.Null(step.FindName("FirstMachineRadio"));
            Assert.Null(step.FindName("HaveGatewayRadio"));

            // The update-only chrome stays hidden on a fresh install.
            Assert.Equal(Visibility.Collapsed, Panel(step, "InstalledRolePanel").Visibility);
            Assert.Equal(Visibility.Collapsed, ButtonNamed(step, "UninstallButton").Visibility);

            // The "what gets installed" description and the Next hint are shown.
            Assert.Equal(Visibility.Visible, Text(step, "DescriptionText").Visibility);
            Assert.Equal(Visibility.Visible, Text(step, "ClickNextHint").Visibility);
        });

    [Fact]
    public void UpdateMode_Gateway_ShowsTheDetectedRoleReadOnly_AndOffersUninstall() =>
        _wpf.Run(() =>
        {
            var step = new WelcomeStep(isUpdate: true, installedVersion: "1.2.3", installedRole: InstallRole.Gateway);

            // Update mode surfaces the detected install type read-only (never a picker) so the person
            // sees they are updating the Gateway, and offers Uninstall (issue #257).
            Assert.Equal(Visibility.Visible, Panel(step, "InstalledRolePanel").Visibility);
            Assert.Contains("Gateway", Text(step, "InstalledRoleText").Text);
            Assert.Equal(Visibility.Visible, ButtonNamed(step, "UninstallButton").Visibility);
        });

    [Fact]
    public void UpdateMode_Workstation_ShowsTheWorkstationTypeReadOnly() =>
        _wpf.Run(() =>
        {
            var step = new WelcomeStep(isUpdate: true, installedVersion: "1.2.3", installedRole: InstallRole.Workstation);

            Assert.Equal(Visibility.Visible, Panel(step, "InstalledRolePanel").Visibility);
            Assert.Contains("Workstation", Text(step, "InstalledRoleText").Text);
        });

    private static TextBlock Text(WelcomeStep step, string name)
    {
        var t = step.FindName(name) as TextBlock;
        Assert.NotNull(t);
        return t!;
    }

    private static Border Panel(WelcomeStep step, string name)
    {
        var b = step.FindName(name) as Border;
        Assert.NotNull(b);
        return b!;
    }

    private static Button ButtonNamed(WelcomeStep step, string name)
    {
        var b = step.FindName(name) as Button;
        Assert.NotNull(b);
        return b!;
    }
}
