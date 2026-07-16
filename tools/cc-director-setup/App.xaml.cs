using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;

namespace CcDirectorSetup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Render the wizard purely in software (issue #1730). On some Windows machines the WPF
        // hardware-composition path (Direct3D 9Ex handed to the Desktop Window Manager) never presents
        // a single frame: the window opens, every step object is built and the setup log runs clean,
        // but the whole client area stays blank white - only the OS-drawn title bar and Close button
        // appear. It was reproduced on Windows 11 (SORENLAPTOP) on the update path with no exception in
        // the setup log or the Windows event log, while the Director - which uses Avalonia's separate
        // GPU path - painted fine on the same desktop, confirming the fault is WPF-specific, not the GPU.
        // The wizard is short-lived and visually simple, so software rendering costs nothing perceptible
        // and it must paint on any GPU or driver. This MUST be set before the first window is created
        // (base.OnStartup builds the StartupUri window), so it runs first.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        SetupLog.Write("[App] OnStartup: forced WPF software rendering (RenderMode.SoftwareOnly) for #1730");

        // Route the shared engine's detailed step logs (Director swap, Python tools extract/venv/pip,
        // SHA verify) into the setup log. Without this the engine's log lines are discarded
        // (EngineLog defaults to a no-op), leaving the log blank during the apply phase - exactly
        // where installs stall - so a failed/stuck install can't be diagnosed.
        EngineLog.Sink = SetupLog.Write;
        base.OnStartup(e);
    }
}
