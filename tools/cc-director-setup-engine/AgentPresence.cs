namespace CcDirector.Setup.Engine;

/// <summary>
/// Answers one question for the Complete screen: is a coding agent OTHER than Claude Code already
/// on this machine?
///
/// It matters because the whole point of making Claude Code non-blocking is that the Director runs
/// eight agent command line tools. Without this, a user who runs Codex or Gemini would finish the
/// install and be told "no coding agent is set up yet" - repeating in words the exact mistake the
/// classification change removed.
///
/// Presence only: no process is started, nothing is authenticated, and no version is read.
/// </summary>
public static class AgentPresence
{
    /// <summary>The non-Claude agent command line tools the Director can drive.</summary>
    public static readonly IReadOnlyList<string> OtherAgentCommands =
        ["codex", "gemini", "opencode", "copilot", "cursor-agent", "grok", "pi"];

    /// <summary>Testable core: true when the probe finds any non-Claude agent.</summary>
    public static bool AnyOtherAgent(Func<string, bool> isOnPath)
    {
        ArgumentNullException.ThrowIfNull(isOnPath);
        return OtherAgentCommands.Any(isOnPath);
    }

    /// <summary>Production probe: walk PATH (honouring PATHEXT on Windows) without spawning anything.</summary>
    public static bool AnyOtherAgent() => AnyOtherAgent(IsOnPath);

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
