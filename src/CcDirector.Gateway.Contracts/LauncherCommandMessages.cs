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
/// Verbs fall into two families:
///
///   * ACTION verbs, which change something on the machine and answer only with success or failure:
///     "director/start", "director/stop", "director/restart", "launch".
///   * QUERY verbs, which change nothing and answer WITH DATA carried in
///     <see cref="LauncherCommandResult.Payload"/>: "apps" (the installed application catalogue) and
///     "files" (a filename search across the machine's drives).
///
/// The query verbs are the reason <see cref="LauncherCommandResult"/> has a payload at all. Before they
/// existed every verb answered yes or no, so the stream arm of the relay could synthesise its own reply and
/// discard whatever the launcher returned. A query whose answer is discarded is not a query, so the payload
/// travels the whole way back now.
/// </summary>
public sealed class LauncherCommand
{
    /// <summary>Selects the action on the launcher: "director/start", "director/stop", "director/restart",
    /// "launch", "apps", or "files".</summary>
    public string Verb { get; set; } = "";

    /// <summary>Optional workspace/context hint for a launch, reserved for future verbs. Unused by the current verbs.</summary>
    public string? Workspace { get; set; }

    /// <summary>For "launch": the absolute path to the executable. For lifecycle verbs: the target Director exe (informational).</summary>
    public string? Path { get; set; }

    /// <summary>
    /// For "launch": the display name of an application from the "apps" catalogue, used when
    /// <see cref="Path"/> is not given. The launcher resolves the name against its own catalogue, so the
    /// caller never has to know where the application lives on that machine.
    /// </summary>
    public string? App { get; set; }

    /// <summary>For "apps" and "files": the search text. A "files" query may use * and ? wildcards; an empty
    /// query on "apps" returns the whole catalogue.</summary>
    public string? Query { get; set; }

    /// <summary>For "apps" and "files": the largest number of results to return. The launcher clamps this to
    /// its own ceiling, and says so in the reply rather than trimming in silence.</summary>
    public int Limit { get; set; }

    /// <summary>For "files": how long the search may run in milliseconds before it returns what it has found
    /// so far and reports itself truncated. The launcher clamps this to its own ceiling.</summary>
    public int TimeoutMilliseconds { get; set; }

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

    /// <summary>
    /// The answer to a QUERY verb, already serialised as JavaScript Object Notation by the launcher; null for
    /// the action verbs, which have nothing to say beyond success or failure.
    ///
    /// It is carried as text rather than as a typed object on purpose. The launcher and the Gateway are
    /// upgraded separately, and a launcher that is a version ahead may answer with fields this Gateway has
    /// never heard of. Text passes those through to the caller untouched, where a typed field would drop
    /// them silently on the floor during deserialisation.
    /// </summary>
    public string? Payload { get; set; }

    /// <summary>True when the command succeeded.</summary>
    public bool IsOk => Status == LauncherCommandStatus.Ok;

    /// <summary>Build a success result for an action verb, which carries no payload.</summary>
    public static LauncherCommandResult Ok() =>
        new() { Status = LauncherCommandStatus.Ok };

    /// <summary>Build a success result for a query verb, carrying its already-serialised answer.</summary>
    public static LauncherCommandResult OkWithPayload(string payload) =>
        new() { Status = LauncherCommandStatus.Ok, Payload = payload };

    /// <summary>Build a failure result with the given status and message.</summary>
    public static LauncherCommandResult Fail(LauncherCommandStatus status, string error) =>
        new() { Status = status, Error = error };
}
