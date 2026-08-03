using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// What this machine can be said to know about git. THREE states, not two.
///
/// The third state is the point. A detector that answers only "yes" or "no" has to fold every
/// inconclusive outcome into one of them, and both choices tell the user something false: folded
/// into "present" it says nothing when git really is missing, and folded into "absent" it tells
/// someone with git installed that they have not got it. When the probe cannot reach a verdict the
/// honest answer is that we do not know, and the caller says nothing at all.
/// </summary>
public enum GitAvailability
{
    /// <summary>A git executable resolved on PATH AND ran and identified itself. Only this is "yes".</summary>
    Present,

    /// <summary>Nothing named git resolves on this machine's PATH. This is a definite "no".</summary>
    NotFound,

    /// <summary>
    /// Something is there but it did not answer: the launch failed, the probe timed out, it exited
    /// non-zero, or what came back was not a git version. We do not know, and we do not guess.
    /// </summary>
    Undetermined,
}

/// <summary>
/// The finished verdict about git on this machine: the state, what was found, and why.
/// <see cref="ShouldAdviseInstallingGit"/> is the whole ruling the user interface reads - a view
/// never re-derives it from the parts.
/// </summary>
/// <param name="Availability">The three-state verdict.</param>
/// <param name="ExecutablePath">The resolved executable, when one was found. Null otherwise.</param>
/// <param name="Version">The version line git printed, when it ran and identified itself.</param>
/// <param name="Detail">Why the verdict is what it is - for the log, never for the screen.</param>
public readonly record struct GitPresence(
    GitAvailability Availability,
    string? ExecutablePath,
    string? Version,
    string Detail)
{
    /// <summary>
    /// Whether to tell the user that git is missing and recommended. TRUE ONLY for
    /// <see cref="GitAvailability.NotFound"/> - never for <see cref="GitAvailability.Undetermined"/>,
    /// because a machine we could not read is not a machine without git, and saying so would be a
    /// statement about someone's computer that we have not established.
    /// </summary>
    public bool ShouldAdviseInstallingGit => Availability == GitAvailability.NotFound;
}

/// <summary>
/// The one sentence the product shows when a git command could not be launched at all.
///
/// It lives in ONE place because it is rendered by three of them - the read services, the write
/// services, and the desktop Source Control view - and a sentence copied three times is a sentence
/// that can only be corrected in one of them.
/// </summary>
public static class GitLaunchFailure
{
    /// <summary>Windows ERROR_FILE_NOT_FOUND: there is no such executable.</summary>
    private const int FileNotFound = 2;

    /// <summary>Windows ERROR_PATH_NOT_FOUND: the directory the executable would be in is not there.</summary>
    private const int PathNotFound = 3;

    /// <summary>
    /// Describe why git did not launch, WITHOUT over-claiming. "Not installed" is said only for the
    /// two operating-system codes that actually mean the file is not there; every other reason - a
    /// permission refusal, a corrupt image, an execution policy - reports itself in its own words
    /// rather than being relabelled as a missing install.
    /// </summary>
    /// <param name="nativeErrorCode">The operating system's own error number from the failed launch.</param>
    /// <param name="reason">What the failure said, used verbatim for every code but the two below.</param>
    public static string Describe(int nativeErrorCode, string? reason)
    {
        if (nativeErrorCode is FileNotFound or PathNotFound)
            return "git is not installed on this machine, or is not on PATH";
        return string.IsNullOrWhiteSpace(reason)
            ? "git could not be started"
            : $"git could not be started: {reason}";
    }

    /// <summary>The same rule, for a launch failure that arrived as an exception.</summary>
    public static string Describe(System.ComponentModel.Win32Exception ex)
        => Describe(ex.NativeErrorCode, ex.Message);
}

/// <summary>The raw outcome of running <c>git --version</c>: did it run at all, and what came back.</summary>
/// <param name="Ran">False when the process could not be started or did not finish in time.</param>
/// <param name="ExitCode">The exit code, meaningful only when <paramref name="Ran"/> is true.</param>
/// <param name="Output">Everything the probe printed, trimmed. Empty when it did not run.</param>
public readonly record struct GitVersionProbe(bool Ran, int ExitCode, string Output);

/// <summary>
/// Decides whether git is usable on this machine, for the first-run wizard's Code step
/// (devthrottle_internal issue #1048).
///
/// It is deliberately a TWO-part test, because a file existing is not a working install:
///   1. resolve the name on PATH the way the operating system would, then
///   2. RUN it and require it to identify itself as git.
/// A stale shim, a broken install, or a zero-byte placeholder passes step one and fails step two,
/// and each of those lands in <see cref="GitAvailability.Undetermined"/> rather than being reported
/// as a working git.
///
/// This detector NEVER installs anything and never changes the machine. DevThrottle works without
/// git - a plain folder is a perfectly good code folder - so the only product of this class is a
/// sentence on a screen.
/// </summary>
public static class GitPresenceDetector
{
    /// <summary>The command name looked up on PATH.</summary>
    public const string GitCommand = "git";

    /// <summary>
    /// How long the probe waits for <c>git --version</c>. Generous for a command that normally
    /// answers in milliseconds, and short enough that a wedged executable cannot hold the Code step.
    /// A timeout is <see cref="GitAvailability.Undetermined"/>, never "absent".
    /// </summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Detect git on this machine, using the real PATH and a real subprocess.</summary>
    public static Task<GitPresence> DetectAsync(CancellationToken ct = default)
        => DetectAsync(c => ExecutableResolver.Resolve(c), RunVersionAsync, ct);

    /// <summary>
    /// The rule, with the two machine-touching steps injected so every branch is reachable in a
    /// test without needing a machine that genuinely lacks git.
    /// </summary>
    internal static async Task<GitPresence> DetectAsync(
        Func<string, string?> resolve,
        Func<string, CancellationToken, Task<GitVersionProbe>> probe,
        CancellationToken ct)
    {
        var path = resolve(GitCommand);
        if (path is null)
        {
            FileLog.Write("[GitPresenceDetector] DetectAsync: git does not resolve on PATH");
            return new GitPresence(GitAvailability.NotFound, null, null, "git does not resolve on PATH");
        }

        var result = await probe(path, ct);

        if (!result.Ran)
        {
            FileLog.Write($"[GitPresenceDetector] DetectAsync: {path} did not run: {result.Output}");
            return new GitPresence(GitAvailability.Undetermined, path, null,
                $"{path} resolved but did not run: {result.Output}");
        }

        if (result.ExitCode != 0)
        {
            FileLog.Write($"[GitPresenceDetector] DetectAsync: {path} exited {result.ExitCode}");
            return new GitPresence(GitAvailability.Undetermined, path, null,
                $"{path} exited {result.ExitCode}");
        }

        // Exit zero is not enough on its own: it says something ran, not that git ran. Requiring the
        // version banner is what makes the difference between a working git and any other program
        // that happens to sit on PATH under that name and exit cleanly.
        if (!result.Output.Contains("git version", StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[GitPresenceDetector] DetectAsync: {path} did not identify itself as git");
            return new GitPresence(GitAvailability.Undetermined, path, null,
                $"{path} ran but did not print a git version");
        }

        var version = result.Output.Split('\n')[0].Trim();
        FileLog.Write($"[GitPresenceDetector] DetectAsync: {path} -> {version}");
        return new GitPresence(GitAvailability.Present, path, version, version);
    }

    /// <summary>
    /// Run <c>&lt;path&gt; --version</c> and capture what it says. Every way of failing to get an
    /// answer - the launch throwing, the timeout, a broken pipe - comes back as Ran=false, so the
    /// rule above turns it into Undetermined instead of a claim about the machine.
    /// </summary>
    private static async Task<GitVersionProbe> RunVersionAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            var r = await ProcessRunner.RunAsync(path, new[] { "--version" }, null, timeout.Token);
            if (!r.Started)
                return new GitVersionProbe(false, -1, r.StandardError);
            return new GitVersionProbe(true, r.ExitCode, (r.StandardOutput + "\n" + r.StandardError).Trim());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // OUR timeout fired, not the caller's cancellation. That is an unreadable machine, not a
            // cancelled request, so it is reported as a failure to run rather than rethrown.
            return new GitVersionProbe(false, -1, $"timed out after {ProbeTimeout.TotalSeconds:F0} seconds");
        }
        catch (Exception ex)
        {
            return new GitVersionProbe(false, -1, ex.Message);
        }
    }
}
