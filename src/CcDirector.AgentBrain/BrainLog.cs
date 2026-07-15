namespace CcDirector.AgentBrain;

/// <summary>
/// Default diagnostic log sink for the AgentBrain library: a daily file under
/// %LOCALAPPDATA%\cc-director\logs\agent-brain\. The library is reused across many
/// host programs, so it cannot depend on CcDirector.Core's FileLog; hosts that want
/// their own sink set the Log action on their options (e.g. HostedAgentOptions.Log).
/// </summary>
public static class BrainLog
{
    private static readonly object Gate = new();

    /// <summary>
    /// base/logs/agent-brain/. This is the ONE place outside CcStorage allowed to compose the
    /// cc-director root, because this library deliberately has no project references (see the class
    /// summary) and so cannot call CcStorage - StorageRootGuardTests carries a matching exemption.
    /// It therefore honors CC_DIRECTOR_ROOT by hand, exactly as CcStorage.Base() does, so a test that
    /// pins the root cannot be written into the real Director's log folder. Resolved per access, not
    /// baked into a static readonly field: a field is captured at type load and no test can undo it.
    /// </summary>
    private static string Dir
    {
        get
        {
            var overrideRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
            var root = !string.IsNullOrEmpty(overrideRoot)
                ? overrideRoot
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "cc-director");
            return Path.Combine(root, "logs", "agent-brain");
        }
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Dir);
            var file = Path.Combine(Dir, $"agent-brain-{DateTime.Now:yyyy-MM-dd}.log");
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
    }
}
