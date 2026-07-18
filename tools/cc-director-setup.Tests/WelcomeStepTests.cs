using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
public sealed class WelcomeStepTests : IClassFixture<WpfStaFixture>
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

/// <summary>
/// A single, long-lived STA thread that owns one <see cref="Application"/> with App.xaml's resources
/// loaded, plus a <see cref="Dispatcher"/> to marshal work onto it. WPF resource resolution is
/// thread-affine, so every WelcomeStep must be constructed on the one thread that created the
/// Application - this fixture is that thread. Shared across the test class via IClassFixture so the
/// Application (a per-process singleton) is created exactly once.
/// </summary>
public sealed class WpfStaFixture : IDisposable
{
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;
    private readonly ManualResetEventSlim _ready = new(false);

    public WpfStaFixture()
    {
        _thread = new Thread(() =>
        {
            // One Application per process. The Welcome step's XAML binds the App-level brushes by key
            // (StaticResource AccentBrush etc.); in a unit test there is no App.xaml-driven startup, so
            // we register exactly those brushes into Application.Resources here. The values mirror
            // App.xaml; only the keys the step actually binds are needed for it to construct.
            if (Application.Current == null)
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                AddBrush(app, "TextForeground", "#CCCCCC");
                AddBrush(app, "AccentBrush", "#007ACC");
                AddBrush(app, "DimText", "#888888");
                AddBrush(app, "MutedText", "#666666");
                AddBrush(app, "StepInactive", "#3C3C3C");
                AddBrush(app, "ErrorBrush", "#CC4444");
                AddBrush(app, "ButtonBackground", "#3C3C3C");
                AddBrush(app, "ButtonHover", "#505050");

                // The Uninstall button in the step references the DangerButton style by key at parse
                // time (it is only shown in update mode, but the reference is resolved when the XAML
                // loads). A minimal Button style under that key is enough for the step to construct.
                app.Resources["DangerButton"] = new Style(typeof(Button));
            }

            _dispatcher = Dispatcher.CurrentDispatcher;
            _ready.Set();
            Dispatcher.Run();
        });
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
        _ready.Wait();
    }

    private static void AddBrush(Application app, string key, string hex) =>
        app.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    /// <summary>Run <paramref name="body"/> synchronously on the STA thread, surfacing any exception.</summary>
    public void Run(Action body)
    {
        Exception? captured = null;
        _dispatcher!.Invoke(() =>
        {
            try { body(); }
            catch (Exception ex) { captured = ex; }
        });
        if (captured != null)
            throw new Xunit.Sdk.XunitException($"WPF STA body failed: {captured}");
    }

    public void Dispose()
    {
        _dispatcher?.InvokeShutdown();
        _ready.Dispose();
    }
}
