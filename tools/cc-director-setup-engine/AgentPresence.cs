namespace CcDirector.Setup.Engine;

/// <summary>
/// Answers one question for the Complete screen: is ANY coding agent already on this machine?
///
/// It is asked because "You're ready to go" is a claim about the machine rather than about the
/// install, and it is false on a machine with nothing to run. It is asked about every agent, not
/// about Claude Code, because the Director drives eight of them - telling a user who runs Codex or
/// Gemini that no agent is set up would be the same mistake in different words.
///
/// This is the ONLY thing the installer detects about the machine. Everything else it used to check
/// went with the Prerequisites step: the wizard installs nothing that needs a tool already present,
/// and detection that can be acted on belongs in the Director, which has a tool-detection wizard
/// that can add what it finds to your board.
///
/// Presence only: no process is started, nothing is authenticated, and no version is read.
/// </summary>
public static class AgentPresence
{
    /// <summary>The agent command line tools the Director can drive, one per plugin in
    /// <c>src/CcDirector.Core/AgentPlugins/</c>.</summary>
    public static readonly IReadOnlyList<string> AgentCommands =
        ["claude", "codex", "gemini", "opencode", "copilot", "cursor-agent", "grok", "pi"];

    /// <summary>Testable core: true when the probe finds any agent.</summary>
    public static bool AnyAgent(Func<string, bool> isPresent)
    {
        ArgumentNullException.ThrowIfNull(isPresent);
        return AgentCommands.Any(isPresent);
    }

    /// <summary>Production probe: every directory in <see cref="SearchDirectories"/>, without
    /// spawning anything.</summary>
    public static bool AnyAgent() => AnyAgentIn(SearchDirectories());

    /// <summary>
    /// Is any agent in one of these directories? Takes the directories as an argument so a test can
    /// hand it a temporary directory and get a deterministic answer - probing the real machine's
    /// home directory would let a test pass because the DEVELOPER has Claude installed, which proves
    /// nothing about the code.
    /// </summary>
    public static bool AnyAgentIn(IEnumerable<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        var dirs = directories.ToList();
        return AnyAgent(exe => dirs.Any(d => InDirectory(d, exe)));
    }

    /// <summary>
    /// Everywhere this probe looks: PATH, plus the two directories installers actually write to.
    ///
    /// PATH alone made this notice LIE. The Director deliberately looks in the same two extra places
    /// (see the npm-global and ~/.local/bin resolution in <c>src/CcDirector.Core/AgentPlugins/</c>)
    /// because that is where agents are installed and because a long-lived shell's PATH goes stale.
    /// The official Claude installer targets ~/.local/bin, so on a machine where that directory was
    /// not on the wizard's PATH the Complete screen said "no coding agent is set up" and the Director
    /// then found Claude immediately. Two components must not disagree about that.
    /// </summary>
    public static IReadOnlyList<string> SearchDirectories() =>
        SearchDirectories(Environment.GetEnvironmentVariable("PATH") ?? "");

    /// <summary>
    /// Testable core of <see cref="SearchDirectories()"/>: the PATH value is handed in so a test can
    /// pass an EMPTY one and see the two extra directories on their own. Reading the real PATH would
    /// let the test pass because the developer's PATH already contains ~/.local/bin - which is
    /// exactly the vacuous pass this seam exists to prevent.
    /// </summary>
    public static IReadOnlyList<string> SearchDirectories(string pathVariable)
    {
        var dirs = (pathVariable ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim())
            .ToList();

        dirs.Add(NpmGlobalDir());
        dirs.Add(LocalBinDir());
        return dirs.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
    }

    /// <summary>Is <paramref name="exe"/> in this one directory, under any executable extension?</summary>
    private static bool InDirectory(string dir, string exe)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;

        foreach (var ext in Extensions())
        {
            try
            {
                if (File.Exists(Path.Combine(dir, exe + ext))) return true;
            }
            catch
            {
                // A malformed PATH entry must not take down the notice; keep probing the rest.
            }
        }
        return false;
    }

    /// <summary>On Windows an agent may be a .cmd shim (npm) as readily as an .exe, so PATHEXT is
    /// honoured and .CMD is in the fallback list.</summary>
    private static string[] Extensions() =>
        OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

    /// <summary>Where a global npm install puts its shims.</summary>
    private static string NpmGlobalDir()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrWhiteSpace(appData) ? "" : Path.Combine(appData, "npm");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? "" : Path.Combine(home, ".npm-global", "bin");
    }

    /// <summary>The official installer's target: ~/.local/bin.</summary>
    private static string LocalBinDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? "" : Path.Combine(home, ".local", "bin");
    }
}
