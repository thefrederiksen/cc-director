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
    public static bool AnyAgent(Func<string, bool> isOnPath)
    {
        ArgumentNullException.ThrowIfNull(isOnPath);
        return AgentCommands.Any(isOnPath);
    }

    /// <summary>Production probe: walk PATH (honouring PATHEXT on Windows) without spawning anything.</summary>
    public static bool AnyAgent() => AnyAgent(IsOnPath);

    private static bool IsOnPath(string exe)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                try
                {
                    if (File.Exists(Path.Combine(dir.Trim(), exe + ext)))
                        return true;
                }
                catch
                {
                    // A malformed PATH entry must not take down the notice; keep probing the rest.
                }
            }
        }

        return false;
    }
}
