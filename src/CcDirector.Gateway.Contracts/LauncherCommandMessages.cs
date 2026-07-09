namespace CcDirector.Gateway.Contracts;

/// <summary>
/// launcher-persistent-join: a lifecycle command the Gateway sends DOWN a launcher's persistent stream.
/// <see cref="Verb"/> selects the action on the launcher; the remaining fields carry the payload the verb
/// needs (all optional - each verb reads only the fields it uses).
///
/// This is the launcher twin of <see cref="DirectorCommand"/>. It rides the SAME outbound-dialed connection
/// (the launcher dials the Gateway; the Gateway never dials the launcher), and the launcher executes it
/// through the SAME <c>DirectorSupervisor</c> / <c>LaunchService</c> actions the loopback REST endpoints use,
/// so the stream path and the REST relay path cannot drift.
///
/// Verbs: "director/start", "director/stop", "director/restart", "launch".
/// </summary>
public sealed class LauncherCommand
{
    /// <summary>Selects the action on the launcher: "director/start", "director/stop", "director/restart", or "launch".</summary>
    public string Verb { get; set; } = "";

    /// <summary>Optional workspace/context hint for a launch, reserved for future verbs. Unused by the current verbs.</summary>
    public string? Workspace { get; set; }

    /// <summary>For "launch": the absolute path to the executable. For lifecycle verbs: the target Director exe (informational).</summary>
    public string? Path { get; set; }

    /// <summary>For "launch": optional command-line arguments.</summary>
    public string? Args { get; set; }

    /// <summary>For "launch": optional working directory (defaults to the executable's directory).</summary>
    public string? Cwd { get; set; }

    /// <summary>For "launch": when true, run headless (no window); when false, GUI mode with clean parentage.</summary>
    public bool Headless { get; set; }

    /// <summary>
    /// True when the caller confirmed a protected-slot lifecycle action. The Gateway's slot guard already
    /// runs before the command is pushed; this echoes the caller's confirmation for the launcher's audit log.
    /// </summary>
    public bool ConfirmProtected { get; set; }
}

/// <summary>The outcome category of a <see cref="LauncherCommand"/>. The stream path has no HTTP status codes.</summary>
public enum LauncherCommandStatus
{
    Ok = 0,
    BadRequest = 1,
    Error = 2,
}

/// <summary>
/// launcher-persistent-join: the reply to a <see cref="LauncherCommand"/>, returned to the Gateway over the
/// same connection (SignalR client results). The launcher twin of <see cref="DirectorCommandResult"/>.
/// </summary>
public sealed class LauncherCommandResult
{
    /// <summary>The outcome category.</summary>
    public LauncherCommandStatus Status { get; set; }

    /// <summary>A human-readable error message on failure; null on success.</summary>
    public string? Error { get; set; }

    /// <summary>True when the command succeeded.</summary>
    public bool IsOk => Status == LauncherCommandStatus.Ok;

    /// <summary>Build a success result.</summary>
    public static LauncherCommandResult Ok() =>
        new() { Status = LauncherCommandStatus.Ok };

    /// <summary>Build a failure result with the given status and message.</summary>
    public static LauncherCommandResult Fail(LauncherCommandStatus status, string error) =>
        new() { Status = status, Error = error };
}
