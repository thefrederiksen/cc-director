namespace CcDirector.Gateway.Tray;

/// <summary>
/// Issue #927: builds the user-facing "Restart Gateway" confirmation strings for the Gateway tray.
/// Pure string logic (no Avalonia, no I/O), kept in the library - not buried in the Avalonia tray
/// controller - so the exact SUCCESS/FAILURE wording (including the running version) is unit-testable
/// without an Avalonia UI thread, mirroring <see cref="GatewayTrayFlyoutCache"/> and
/// <see cref="CcDirector.Gateway.Account.GatewaySignInTraySurface"/>.
///
/// The restart itself is unchanged (this phase is FEEDBACK only); these strings are what the tray
/// tooltip and flyout show so the user can tell a click actually restarted the in-process host - the
/// PID never changes, so without this the restart is invisible.
/// </summary>
public static class GatewayRestartFeedback
{
    /// <summary>
    /// The flyout status line and persistent "Last restart" row after a successful restart, e.g.
    /// "Restarted OK - v0.9.32 at 14:32:05". Includes the running version so the user can see which
    /// build is now live, and the local time of the restart.
    /// </summary>
    public static string SuccessStatus(string version, DateTime localTime)
        => $"Restarted OK - v{version} at {localTime:HH:mm:ss}";

    /// <summary>
    /// The distinct tray tooltip shown briefly after a successful restart, e.g.
    /// "DevThrottle Gateway - restarted OK (v0.9.32) at 14:32:05". Distinct from the normal running
    /// tooltip so a hover confirms the restart; the tray reverts to its normal running tooltip after.
    /// </summary>
    public static string SuccessTooltip(string version, DateTime localTime)
        => $"DevThrottle Gateway - restarted OK (v{version}) at {localTime:HH:mm:ss}";

    /// <summary>
    /// The flyout status line and persistent "Last restart" row after a FAILED restart, e.g.
    /// "Restart FAILED - Port 7900 in use by another app". Never a silent no-op - the underlying
    /// reason is surfaced so the user knows the restart did not take effect.
    /// </summary>
    public static string FailureStatus(string reason)
        => $"Restart FAILED - {reason}";

    /// <summary>
    /// The distinct tray tooltip shown after a FAILED restart, e.g.
    /// "DevThrottle Gateway - RESTART FAILED: Port 7900 in use by another app".
    /// </summary>
    public static string FailureTooltip(string reason)
        => $"DevThrottle Gateway - RESTART FAILED: {reason}";
}
