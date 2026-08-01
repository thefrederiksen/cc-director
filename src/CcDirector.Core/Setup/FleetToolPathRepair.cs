using Microsoft.Win32;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Setup;

/// <summary>What a repair attempt did, for the panel to report back verbatim.</summary>
public sealed record PathRepairResult(bool Succeeded, string Detail);

/// <summary>
/// Puts this Director's own tool directory first on PATH, so the sessions it spawns reach ITS
/// cc-devthrottle rather than one left behind by an older install.
///
/// It is deliberately NON-DESTRUCTIVE: it moves our directory to the front and touches nothing else.
/// The old install stays on disk and stays on PATH behind us, so the change is reversible by hand and
/// a developer who is deliberately keeping an older toolchain still has it. Removing anything is a
/// separate decision and not this one.
///
/// TWO THINGS THIS GETS RIGHT, both of which are easy to get wrong:
///
/// 1. It writes the RAW registry value, not the expanded one. Reading the user PATH through
///    Environment.GetEnvironmentVariable returns it with %USERPROFILE% and friends already expanded;
///    writing that back would bake today's expansion into the user's PATH permanently and silently
///    destroy every variable reference in it. This reads with DoNotExpandEnvironmentNames and writes
///    back the same RegistryValueKind it found.
///
/// 2. It updates THIS PROCESS's PATH as well as the persisted one. A running process inherited its
///    environment at launch, so persisting alone would leave the Director handing its sessions the
///    stale tool until it restarted - the badge would not clear, and the button would read as broken.
///    Sessions ALREADY running keep the old PATH; nothing can repair those in place, and the panel
///    says so rather than implying otherwise.
/// </summary>
public static class FleetToolPathRepair
{
    private const string UserEnvironmentKey = "Environment";
    private const string PathValueName = "Path";

    /// <summary>
    /// Move <paramref name="binDir"/> to the front of the user PATH and of this process's PATH.
    /// </summary>
    public static PathRepairResult PutFirstOnPath(string binDir)
    {
        FileLog.Write($"[FleetToolPathRepair] PutFirstOnPath: {binDir}");

        if (string.IsNullOrWhiteSpace(binDir))
            throw new ArgumentException("A directory is required.", nameof(binDir));
        if (!Directory.Exists(binDir))
            throw new DirectoryNotFoundException($"Cannot put a directory on PATH that does not exist: {binDir}");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Persisting a PATH change is Windows-only here; on macOS and Linux the shell profile owns PATH.");

        try
        {
            var persisted = RepairPersistedPath(binDir);
            RepairProcessPath(binDir);

            FileLog.Write($"[FleetToolPathRepair] PutFirstOnPath done: {persisted}");
            return new PathRepairResult(true, persisted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            FileLog.Write($"[FleetToolPathRepair] PutFirstOnPath FAILED: {ex.Message}");
            return new PathRepairResult(false, $"Could not update PATH: {ex.Message}");
        }
    }

    // PutFirstOnPath refuses every non-Windows caller before reaching here; this states that for the
    // platform analyzer, which cannot see the guard across the method boundary.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string RepairPersistedPath(string binDir)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UserEnvironmentKey, writable: true)
            ?? throw new IOException($"The user environment registry key ({UserEnvironmentKey}) is not readable.");

        // DoNotExpandEnvironmentNames is the whole point: %USERPROFILE% must survive as %USERPROFILE%.
        var raw = key.GetValue(PathValueName, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var kind = raw.Contains('%') ? RegistryValueKind.ExpandString : key.GetValueKind(PathValueName);

        var updated = MoveToFront(raw, binDir);
        if (string.Equals(raw, updated, StringComparison.Ordinal))
            return $"{binDir} was already first on the user PATH.";

        key.SetValue(PathValueName, updated, kind);
        return $"{binDir} is now first on the user PATH. The previous install is still on PATH behind it.";
    }

    private static void RepairProcessPath(string binDir)
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        Environment.SetEnvironmentVariable("PATH", MoveToFront(current, binDir));
    }

    /// <summary>
    /// Return <paramref name="path"/> with <paramref name="entry"/> at the front and any existing copy
    /// of it removed, so repeated repairs cannot accumulate duplicates. Every other entry keeps its
    /// order.
    /// </summary>
    internal static string MoveToFront(string path, string entry)
    {
        var normalizedEntry = Normalize(entry);
        var kept = (path ?? "")
            .Split(Path.PathSeparator)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .Where(segment => !string.Equals(Normalize(segment), normalizedEntry, StringComparison.OrdinalIgnoreCase))
            .ToList();

        kept.Insert(0, entry);
        return string.Join(Path.PathSeparator, kept);
    }

    /// <summary>
    /// A comparable form of a PATH entry. Trailing separators and surrounding whitespace are noise;
    /// unexpanded variables are left alone, because a segment we cannot resolve is one we must not
    /// claim to recognise.
    /// </summary>
    private static string Normalize(string segment)
        => (segment ?? "").Trim().Trim('"').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
