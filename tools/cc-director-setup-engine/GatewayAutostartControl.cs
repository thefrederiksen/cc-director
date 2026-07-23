namespace CcDirector.Setup.Engine;

/// <summary>
/// The one cross-OS entry point over the three per-OS Gateway autostart mechanisms (issue #2022), so a
/// caller states its intent - on / off / status - without an operating-system switch of its own. Backs the
/// <c>devthrottle-setup-cli autostart on|off|status</c> command (and, through it, the user-facing
/// <c>cc-devthrottle autostart</c>).
///
/// The mechanism per platform, each already the installer's and the running Gateway's own:
///   - Windows: the HKCU Run key (<see cref="GatewayAutostart"/>).
///   - macOS: a launchd user launch agent (<see cref="GatewayLaunchdAutostart"/>).
///   - Linux: a <c>systemd --user</c> unit (<see cref="GatewaySystemdAutostart"/>) - the home a headless
///     server needs, where no tray or window exists.
///
/// This is a THIN facade: it owns no policy, only the dispatch. Each arm is guarded by the matching
/// <see cref="OperatingSystem"/> check so the platform-attributed per-OS methods are only ever called on
/// their platform. An unsupported platform FAILS LOUD rather than pretending (the no-fallback rule).
/// </summary>
public static class GatewayAutostartControl
{
    /// <summary>True on the three platforms that have a real per-user autostart mechanism.</summary>
    public static bool Supported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    /// <summary>A human-readable name for the mechanism on this platform, for the status line.</summary>
    public static string MechanismName =>
        OperatingSystem.IsWindows() ? "Windows Run key (HKCU)"
        : OperatingSystem.IsMacOS() ? "macOS launch agent"
        : OperatingSystem.IsLinux() ? "Linux systemd --user unit"
        : "unsupported on this platform";

    /// <summary>Whether Gateway autostart is currently registered on this platform.</summary>
    public static bool IsEnabled =>
        OperatingSystem.IsWindows() ? GatewayAutostart.IsRegistered()
        : OperatingSystem.IsMacOS() ? GatewayLaunchdAutostart.IsRegistered()
        : OperatingSystem.IsLinux() ? GatewaySystemdAutostart.IsRegistered()
        : false;

    /// <summary>The registered command line, or null when autostart is off / unsupported.</summary>
    public static string? RegisteredCommand =>
        OperatingSystem.IsWindows() ? GatewayAutostart.Registered()
        : OperatingSystem.IsMacOS() ? GatewayLaunchdAutostart.Registered()
        : OperatingSystem.IsLinux() ? GatewaySystemdAutostart.Registered()
        : null;

    /// <summary>
    /// Turn autostart on for the given Gateway executable and arguments. Idempotent: returns true if a
    /// change was made, false if it was already correct. Throws on an unsupported platform.
    /// </summary>
    public static bool Enable(string exePath, string? arguments = null)
    {
        if (OperatingSystem.IsWindows()) return GatewayAutostart.EnsureRegistered(exePath, arguments);
        if (OperatingSystem.IsMacOS()) return GatewayLaunchdAutostart.EnsureRegistered(exePath, arguments);
        if (OperatingSystem.IsLinux()) return GatewaySystemdAutostart.EnsureRegistered(exePath, arguments);
        throw new PlatformNotSupportedException(
            "Gateway autostart is not supported on this platform - it exists on Windows, macOS, and Linux only.");
    }

    /// <summary>Turn autostart off. Returns true if something was removed. Throws on an unsupported platform.</summary>
    public static bool Disable()
    {
        if (OperatingSystem.IsWindows()) return GatewayAutostart.Unregister();
        if (OperatingSystem.IsMacOS()) return GatewayLaunchdAutostart.Unregister();
        if (OperatingSystem.IsLinux()) return GatewaySystemdAutostart.Unregister();
        throw new PlatformNotSupportedException(
            "Gateway autostart is not supported on this platform - it exists on Windows, macOS, and Linux only.");
    }
}
