using Avalonia.Threading;

namespace CcDirector.TrayUi;

/// <summary>
/// Owns the single long-lived <see cref="TrayFlyout"/> and turns a tray-icon LEFT-CLICK into an
/// open/close toggle. The window is created (and warmed up off-screen) ONCE - at startup via
/// <see cref="WarmUp"/>, or lazily on the first toggle - and then only hidden/shown, so a click
/// never pays native window creation, first layout, or first render: it pays one cheap content
/// rebuild from the model factory (which must read only cached values) and a re-show.
///
/// The tricky bit is the toggle: clicking the tray icon while the flyout is open first deactivates
/// the flyout (which hides it) AND then raises the icon's Clicked - so a naive handler would hide
/// then immediately reopen. A short post-hide debounce swallows that reopen, giving a clean toggle.
/// </summary>
public sealed class TrayFlyoutController
{
    private readonly Func<TrayFlyoutModel> _build;
    private TrayFlyout? _window;

    public TrayFlyoutController(Func<TrayFlyoutModel> build)
        => _build = build ?? throw new ArgumentNullException(nameof(build));

    /// <summary>
    /// Pre-create the hidden window at startup so the FIRST left-click is as instant as every
    /// later one. Safe to call from any thread; a no-op if the window already exists.
    /// </summary>
    public void WarmUp() => Dispatcher.UIThread.Post(() => EnsureWindow());

    /// <summary>Show the flyout if hidden (with fresh content), hide it if visible. Safe to call from the tray Clicked handler.</summary>
    public void Toggle() => Dispatcher.UIThread.Post(() =>
    {
        var window = EnsureWindow();
        if (window.IsVisible)
        {
            window.HideFlyout();
            return;
        }

        // A click that just deactivated+hid the flyout also fires Clicked; don't reopen on it.
        if ((DateTime.UtcNow - window.LastHiddenUtc).TotalMilliseconds < 300)
            return;

        window.UpdateModel(_build());
        window.ShowFlyout();
    });

    /// <summary>Really close the window (app shutdown), not just hide it.</summary>
    public void Close() => Dispatcher.UIThread.Post(() => _window?.Close());

    private TrayFlyout EnsureWindow()
    {
        if (_window is null)
        {
            _window = new TrayFlyout(_build());
            // If something does close the window (shutdown, or an external Close), drop the
            // reference so a later toggle transparently recreates it.
            _window.Closed += (_, _) => _window = null;
            _window.WarmUp();
        }
        return _window;
    }
}
