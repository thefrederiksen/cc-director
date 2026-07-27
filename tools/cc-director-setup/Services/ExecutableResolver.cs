namespace CcDirectorSetup.Services;

/// <summary>
/// Turns a bare command name ("dotnet") into the ABSOLUTE path of the executable to start.
///
/// This exists because of a proven, invisible failure in the Re-check button. Setting
/// <c>psi.Environment["PATH"]</c> fixes the CHILD process's environment, but <c>Process.Start</c>
/// resolves a bare <c>FileName</c> against the PARENT process's PATH - the one this wizard
/// snapshotted when it launched. So after a user installs .NET while setup is open:
///
///   - <c>RunCommand("where", "dotnet")</c> SUCCEEDS. where.exe lives in System32 (which Windows
///     searches regardless of PATH) and it searches using the refreshed PATH we hand the child.
///   - <c>RunCommand("dotnet", "--list-runtimes")</c> on the very next line NEVER STARTS. Windows
///     looks for "dotnet.exe" on the stale parent PATH, does not find it, and Process.Start throws
///     "The system cannot find the file specified" - which RunCommand swallows and reports as
///     "Not found". Forever, however many times the user clicks Re-check.
///
/// Passing the absolute path as <c>FileName</c> takes the parent's PATH out of the equation
/// entirely. The search here mirrors what Windows itself does - each directory in turn, trying
/// each executable extension within that directory - but reads the LIVE machine and user PATH
/// from the registry rather than the process's stale copy.
///
/// Everything is injectable so the search can be tested against directories that are definitely
/// not on the test process's PATH.
/// </summary>
public static class ExecutableResolver
{
    /// <summary>
    /// Extensions tried, in order, when the caller gives a name with no extension. This is the
    /// meaningful part of the default PATHEXT: a coding agent installed through npm lands as a
    /// .cmd shim rather than an .exe.
    /// </summary>
    private static readonly string[] Extensions = [".exe", ".cmd", ".bat"];

    /// <summary>
    /// The live machine+user PATH read straight from the registry, so a tool added to PATH after
    /// this wizard launched (or one on the USER PATH the process never inherited) is visible.
    /// Returns null when neither target could be read, leaving callers on the inherited PATH.
    /// </summary>
    public static string? LivePath()
    {
        var machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
        var user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";

        var parts = new[] { machine, user }.Where(p => !string.IsNullOrWhiteSpace(p));
        var combined = string.Join(";", parts);

        return string.IsNullOrWhiteSpace(combined) ? null : combined;
    }

    /// <summary>
    /// The absolute path of <paramref name="exeName"/>, or null when it is nowhere to be found.
    /// Searches the live PATH first, then the well-known install directories for that tool (a
    /// freshly installed .NET is on disk before its PATH entry reaches every process).
    /// </summary>
    public static string? Resolve(string exeName) =>
        FindIn(exeName, LivePath(), WellKnownDirectories(exeName), File.Exists);

    /// <summary>
    /// The search itself, with no ambient state: walk <paramref name="searchPath"/> and then
    /// <paramref name="extraDirectories"/>, trying each executable extension inside each directory
    /// before moving to the next one - the order Windows uses.
    /// </summary>
    /// <param name="exeName">A command name ("dotnet") or a file name with its extension.</param>
    /// <param name="searchPath">A semicolon-separated directory list, or null for none.</param>
    /// <param name="extraDirectories">Directories searched after the path, in order.</param>
    /// <param name="fileExists">The existence probe (<c>File.Exists</c> in production).</param>
    public static string? FindIn(
        string exeName,
        string? searchPath,
        IEnumerable<string> extraDirectories,
        Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exeName);
        ArgumentNullException.ThrowIfNull(extraDirectories);
        ArgumentNullException.ThrowIfNull(fileExists);

        var fileNames = Path.HasExtension(exeName)
            ? [exeName]
            : Extensions.Select(ext => exeName + ext).ToArray();

        var directories = (searchPath ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(extraDirectories);

        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            foreach (var fileName in fileNames)
            {
                // A malformed PATH entry (illegal characters, a stray quote) must not abort the
                // whole search - the entries after it may hold the tool we are looking for.
                string candidate;
                try
                {
                    candidate = Path.Combine(directory.Trim().Trim('"'), fileName);
                }
                catch (ArgumentException)
                {
                    break;
                }

                if (fileExists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Where a tool lands when it is installed, for the case where it is on disk but its PATH
    /// entry has not reached us. Only .NET has locations worth guessing; everything else relies
    /// on the live PATH.
    /// </summary>
    public static IReadOnlyList<string> WellKnownDirectories(string exeName)
    {
        if (!string.Equals(exeName, "dotnet", StringComparison.OrdinalIgnoreCase))
            return [];

        return
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "dotnet"),
        ];
    }
}
