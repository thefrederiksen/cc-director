using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CcDirector.Core.Utilities;

namespace CcDirector.Launcher;

public partial class App : Application
{
    private LauncherTrayController? _controller;

    /// <summary>
    /// True once the user-interface framework reached application code. Program uses this
    /// to tell a PLATFORM initialization failure (framework never started - e.g. a locked
    /// screen at startup on macOS; fall back to headless mode) apart from a fault in the
    /// running application (exit loudly, no fallback).
    /// </summary>
    public static bool FrameworkInitialized { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        FrameworkInitialized = true;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The launcher has no main window: it lives in the tray and must NOT exit when
            // the (nonexistent) last window closes. Only the tray "Quit" item shuts it down.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            try
            {
                _controller = new LauncherTrayController(desktop, LauncherAppOptions.Port);
                _controller.Start();
            }
            catch (Exception ex)
            {
                FileLog.Write($"[App] Controller start FAILED: {ex}");
                throw;
            }

            desktop.ShutdownRequested += (_, _) => _controller?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
