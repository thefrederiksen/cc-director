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
///
/// 3. It refuses to promote a directory that holds no cc-devthrottle. This is the one that was
///    missing. The old guard asked only whether the directory EXISTED - and on the machine this was
///    written for it existed and was empty, because that Director's tools had never been installed.
///    PATH was reordered perfectly, resolution fell through the empty directory to the same stale
///    install, and the repair reported the failure it had just been asked to fix. A container is not
///    its contents.
/// </summary>
public static class FleetToolPathRepair
{
    private const string UserEnvironmentKey = "Environment";
    private const string PathValueName = "Path";

    /// <summary>The command whose presence makes a directory a DevThrottle tool directory.</summary>
    private const string ToolName = "cc-devthrottle";

    /// <summary>
    /// Move <paramref name="binDir"/> to the front of the user PATH and of this process's PATH, and
    /// remove the superseded DevThrottle tool directories left behind it (see
    /// <see cref="IsSuperseded"/>). Only PATH entries are removed - nothing on disk is touched.
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

        // The precondition the original repair assumed. Promoting an empty directory changes the order
        // of PATH and nothing about what resolves, so it looks like a repair and is not one.
        if (!HoldsFleetTool(binDir))
        {
            var refusal =
                $"This Director's own tools are not installed - there is no {ToolName} in {binDir}. " +
                "Install the tools first; putting an empty directory on PATH would change nothing.";
            FileLog.Write($"[FleetToolPathRepair] PutFirstOnPath REFUSED: {refusal}");
            return new PathRepairResult(false, refusal);
        }

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

    /// <summary>
    /// The user PATH exactly as it is STORED, with every %VARIABLE% intact.
    ///
    /// Public because it is the only safe way to read it, and more than one component needs to.
    /// <c>Environment.GetEnvironmentVariable("Path", User)</c> returns the value with variables
    /// already expanded; anything that reads it that way and writes the result back bakes today's
    /// expansion into the user's PATH permanently and silently destroys every variable reference in
    /// it. That is not a hypothetical - it is what the install finalizer did until this was shared.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string ReadUserPathRaw()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UserEnvironmentKey)
            ?? throw new IOException($"The user environment registry key ({UserEnvironmentKey}) is not readable.");
        return key.GetValue(PathValueName, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
    }

    /// <summary>
    /// Write the user PATH back, keeping it expandable when it carries variables. Pass a value that
    /// came from <see cref="ReadUserPathRaw"/> and was edited without expanding anything.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void WriteUserPathRaw(string value)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UserEnvironmentKey, writable: true)
            ?? throw new IOException($"The user environment registry key ({UserEnvironmentKey}) is not writable.");

        // A value holding %VARIABLE% must be stored as ExpandString or the variables stop resolving.
        var kind = value.Contains('%') ? RegistryValueKind.ExpandString : key.GetValueKind(PathValueName);
        key.SetValue(PathValueName, value, kind);
    }

    // PutFirstOnPath refuses every non-Windows caller before reaching here; this states that for the
    // platform analyzer, which cannot see the guard across the method boundary.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string RepairPersistedPath(string binDir)
    {
        var raw = ReadUserPathRaw();
        var rewrite = Rewrite(raw, binDir);
        if (string.Equals(raw, rewrite.Path, StringComparison.Ordinal))
            return $"{binDir} was already first on the user PATH, and nothing superseded was left behind it.";

        WriteUserPathRaw(rewrite.Path);

        var removed = rewrite.Removed.Count == 0
            ? "Nothing else needed removing."
            : $"Removed {rewrite.Removed.Count} superseded DevThrottle entr{(rewrite.Removed.Count == 1 ? "y" : "ies")} " +
              $"from your PATH: {string.Join("; ", rewrite.Removed)}. The files are still on disk.";
        return $"{binDir} is now first on the user PATH. {removed}";
    }

    private static void RepairProcessPath(string binDir)
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        Environment.SetEnvironmentVariable("PATH", Rewrite(current, binDir).Path);
    }

    /// <summary>The rewritten PATH and the entries dropped from it, so the panel can name them.</summary>
    internal sealed record PathRewrite(string Path, IReadOnlyList<string> Removed);

    /// <summary>Rewrite against the real machine: real directories, the real temp root.</summary>
    internal static PathRewrite Rewrite(string path, string ownBinDir)
        => Rewrite(path, ownBinDir, Directory.Exists, HoldsFleetTool, System.IO.Path.GetTempPath());

    /// <summary>
    /// Put <paramref name="ownBinDir"/> first and drop the superseded DevThrottle tool directories.
    ///
    /// Two entries pointing at two copies of the same command line serve nobody: only the first can
    /// ever win, and the loser sits there waiting to win again the moment the order shifts. So the
    /// repair leaves ONE. What it will not touch is another LIVE install's directory - a second
    /// Director in its own instance home is legitimate on this machine, and removing its tools from
    /// PATH to tidy ours up would be sabotage dressed as hygiene.
    /// </summary>
    /// <param name="directoryExists">Existence test, taking an EXPANDED path.</param>
    /// <param name="holdsFleetTool">Whether an existing (expanded) directory holds cc-devthrottle.</param>
    /// <param name="tempRoot">The machine temp directory, or null to skip the temp rule.</param>
    internal static PathRewrite Rewrite(
        string path,
        string ownBinDir,
        Func<string, bool> directoryExists,
        Func<string, bool> holdsFleetTool,
        string? tempRoot)
    {
        var normalizedOwn = Normalize(ownBinDir);
        var kept = new List<string>();
        var removed = new List<string>();

        foreach (var segment in (path ?? "").Split(System.IO.Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;

            // Our own entry comes out here and goes back at the front below, so a repeated repair
            // cannot accumulate duplicates.
            if (string.Equals(Normalize(segment), normalizedOwn, StringComparison.OrdinalIgnoreCase))
                continue;

            // Expand ONLY to ask questions about the disk. The raw text is what gets written back:
            // %USERPROFILE% must still be %USERPROFILE% afterwards.
            var expanded = Expand(segment);

            if (IsFleetToolDirectory(expanded, directoryExists, holdsFleetTool)
                && IsSuperseded(expanded, normalizedOwn, directoryExists, tempRoot))
            {
                removed.Add(segment.Trim());
                continue;
            }

            kept.Add(segment);
        }

        kept.Insert(0, ownBinDir);
        return new PathRewrite(string.Join(System.IO.Path.PathSeparator, kept), removed);
    }

    /// <summary>
    /// Is this PATH entry a DevThrottle tool directory at all? An existing directory answers for
    /// itself - it holds cc-devthrottle or it does not. A directory that is GONE cannot be asked, so
    /// it is recognised by shape instead, and only by a shape no other product writes: a "bin"
    /// directory inside a cc-director install. Nothing outside that shape is ever a candidate for
    /// removal, so an ordinary entry whose drive happens to be unplugged is left exactly where it is.
    /// </summary>
    private static bool IsFleetToolDirectory(
        string expanded, Func<string, bool> directoryExists, Func<string, bool> holdsFleetTool)
        => directoryExists(expanded) ? holdsFleetTool(expanded) : LooksLikeAnInstallBin(expanded);

    /// <summary>
    /// Has this tool directory been superseded by ours? Three ways, all facts rather than guesses:
    /// it is gone from disk; it lives in the temp directory (a test rig or an unpacked bundle that
    /// leaked into the real user PATH - there is one on the machine that prompted this); or it is the
    /// flat pre-migration bin of our own install root, which the move to per-instance homes left
    /// behind and ahead of us.
    /// </summary>
    private static bool IsSuperseded(
        string expanded, string normalizedOwn, Func<string, bool> directoryExists, string? tempRoot)
    {
        if (!directoryExists(expanded)) return true;
        if (IsUnder(expanded, tempRoot)) return true;

        var legacy = LegacyFlatBinFor(normalizedOwn);
        return legacy is not null
               && string.Equals(Normalize(expanded), legacy, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The pre-migration bin of the install our own bin belongs to. Storage moved from
    /// <c>&lt;root&gt;\bin</c> to <c>&lt;root&gt;\instances\&lt;slug&gt;\bin</c>; when our directory has
    /// that shape, <c>&lt;root&gt;\bin</c> is the copy the migration superseded and left in front of
    /// us. When it does not (a development build), there is no such directory and nothing is claimed.
    /// </summary>
    private static string? LegacyFlatBinFor(string normalizedOwnBinDir)
    {
        var instanceHome = System.IO.Path.GetDirectoryName(normalizedOwnBinDir);
        var instancesDir = instanceHome is null ? null : System.IO.Path.GetDirectoryName(instanceHome);
        if (instancesDir is null) return null;
        if (!string.Equals(
                new DirectoryInfo(instancesDir).Name, "instances", StringComparison.OrdinalIgnoreCase))
            return null;

        var root = System.IO.Path.GetDirectoryName(instancesDir);
        return root is null ? null : Normalize(System.IO.Path.Combine(root, "bin"));
    }

    private static bool LooksLikeAnInstallBin(string expanded)
    {
        var normalized = Normalize(expanded);
        if (!string.Equals(
                SafeName(normalized), "bin", StringComparison.OrdinalIgnoreCase)) return false;

        return normalized.Split(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            .Any(part => string.Equals(part, "cc-director", StringComparison.OrdinalIgnoreCase));
    }

    private static string SafeName(string path)
    {
        try { return new DirectoryInfo(path).Name; }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return "";
        }
    }

    private static bool IsUnder(string candidate, string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var normalizedRoot = Normalize(root) + System.IO.Path.DirectorySeparatorChar;
        return (Normalize(candidate) + System.IO.Path.DirectorySeparatorChar)
            .StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string Expand(string segment)
    {
        try { return Environment.ExpandEnvironmentVariables(segment.Trim().Trim('"')); }
        catch (ArgumentException) { return segment.Trim(); }
    }

    /// <summary>Does this directory hold a runnable cc-devthrottle? The whole question, on disk.</summary>
    internal static bool HoldsFleetTool(string directory)
    {
        try
        {
            return ExecutableResolver.Resolve(System.IO.Path.Combine(directory, ToolName)) is not null;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// The PATH a session we spawn should get: our own tools first, whatever the machine PATH says.
    /// Nothing is removed here - a session inherits the user's PATH and we only guarantee which copy
    /// of our own command line wins.
    /// </summary>
    public static string PathWithOwnToolsFirst(string binDir, string? currentPath)
        => MoveToFront(currentPath ?? "", binDir);

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
