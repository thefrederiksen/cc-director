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

    /// <summary>
    /// Nothing named git resolves on this process's PATH. That is the same lookup every git call in
    /// the product makes, so it is exactly the condition under which DevThrottle cannot run git. It
    /// is NOT a claim to have searched the disk: a git installed somewhere the PATH does not reach
    /// lands here too, and telling that user to set git up is still the right advice, because until
    /// it is on the PATH nothing here can use it.
    /// </summary>
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
    /// Whether to tell the user that git could not be found and is recommended. TRUE ONLY for
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
    /// <summary>
    /// Code 2. Windows ERROR_FILE_NOT_FOUND, and POSIX ENOENT. It means the same thing on both -
    /// there is no such file - which is why it is the one code read on every platform.
    /// </summary>
    private const int FileNotFound = 2;

    /// <summary>
    /// Code 3. WINDOWS ONLY: ERROR_PATH_NOT_FOUND. On POSIX the same number is ESRCH, "no such
    /// process", which says nothing at all about whether git is installed - reading it as a missing
    /// install on macOS would tell a Mac user to reinstall software that is sitting on their disk.
    /// </summary>
    private const int PathNotFoundWindowsOnly = 3;

    /// <summary>
    /// Describe why git did not launch, WITHOUT over-claiming. "Not installed" is said only for the
    /// codes that actually mean the file is not there on THIS operating system; every other reason -
    /// a permission refusal, a corrupt image, an execution policy - reports itself in its own words
    /// rather than being relabelled as a missing install.
    /// </summary>
    /// <param name="nativeErrorCode">The operating system's own error number from the failed launch.</param>
    /// <param name="reason">What the failure said, used verbatim for every other code.</param>
    public static string Describe(int nativeErrorCode, string? reason)
        => Describe(nativeErrorCode, reason, OperatingSystem.IsWindows());

    /// <summary>
    /// The rule with the platform passed in. Exposed because the Windows-only reading of code 3 is
    /// otherwise UNFALSIFIABLE on Windows: a test running here cannot tell the correct rule from one
    /// that reads code 3 as a missing file everywhere, so the guard would have no test that can fail.
    /// </summary>
    internal static string Describe(int nativeErrorCode, string? reason, bool isWindows)
    {
        var meansThereIsNoSuchFile =
            nativeErrorCode == FileNotFound
            || (isWindows && nativeErrorCode == PathNotFoundWindowsOnly);

        if (meansThereIsNoSuchFile)
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
        => DetectAsync(c => ExecutableResolver.Resolve(c), RunVersionAsync, OperatingSystem.IsMacOS(), ct);

    /// <summary>
    /// The rule, with the two machine-touching steps injected so every branch is reachable in a
    /// test without needing a machine that genuinely lacks git.
    /// </summary>
    internal static Task<GitPresence> DetectAsync(
        Func<string, string?> resolve,
        Func<string, CancellationToken, Task<GitVersionProbe>> probe,
        CancellationToken ct)
        => DetectAsync(resolve, probe, OperatingSystem.IsMacOS(), ct);

    /// <summary>
    /// The rule with the platform passed in as well. The macOS branch is the reason this exists: it
    /// is never taken on Windows, so on the machines this suite runs on there would otherwise be NO
    /// test that can fail if the guard is deleted - and that guard is the one stopping the detector
    /// from putting an installer dialog on a Mac user's screen.
    /// </summary>
    internal static async Task<GitPresence> DetectAsync(
        Func<string, string?> resolve,
        Func<string, CancellationToken, Task<GitVersionProbe>> probe,
        bool isMacOs,
        CancellationToken ct)
    {
        var path = resolve(GitCommand);
        if (path is null)
        {
            FileLog.Write("[GitPresenceDetector] DetectAsync: git does not resolve on PATH");
            return new GitPresence(GitAvailability.NotFound, null, null, "git does not resolve on PATH");
        }

        // DO NOT RUN APPLE'S STUB. On macOS /usr/bin/git is a Command Line Tools shim, and executing
        // it when the tools are absent puts up Apple's "install the developer tools?" dialog. This
        // detector's entire remit is to say one sentence and change nothing, so it must not be the
        // thing that offers to install software. Nothing is claimed about such a machine either way.
        if (IsAppleCommandLineToolsStub(path, isMacOs))
        {
            FileLog.Write($"[GitPresenceDetector] DetectAsync: {path} is the macOS developer-tools stub; not running it");
            return new GitPresence(GitAvailability.Undetermined, path, null,
                $"{path} is the macOS developer-tools stub and running it can prompt an install, so it was not run");
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
        //
        // The banner has to be the START of the first line, not merely present somewhere in the
        // output. Real git prints "git version 2.45.1" and nothing before it; a program whose
        // warning text happens to CONTAIN those words would otherwise be accepted as git, and the
        // line stored as its version would be the warning.
        var firstLine = result.Output.Split('\n')[0].Trim();
        if (!firstLine.StartsWith("git version ", StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[GitPresenceDetector] DetectAsync: {path} did not identify itself as git");
            return new GitPresence(GitAvailability.Undetermined, path, null,
                $"{path} ran but did not print a git version");
        }

        FileLog.Write($"[GitPresenceDetector] DetectAsync: {path} -> {firstLine}");
        return new GitPresence(GitAvailability.Present, path, firstLine, firstLine);
    }

    /// <summary>
    /// Whether this path is Apple's Command Line Tools shim at <c>/usr/bin/git</c>. On a Mac without
    /// the tools installed that file EXISTS and running it opens Apple's install dialog, so it is the
    /// one executable this detector refuses to launch. The consequence is accepted deliberately: on a
    /// Mac whose git lives there we reach no verdict and say nothing, which is the quiet, honest
    /// outcome. A Mac with git from Homebrew or elsewhere resolves to that path instead and is probed
    /// normally.
    /// </summary>
    private static bool IsAppleCommandLineToolsStub(string path, bool isMacOs)
        => isMacOs && string.Equals(path, AppleStubPath, StringComparison.Ordinal);

    /// <summary>Where Apple puts its Command Line Tools shims.</summary>
    internal const string AppleStubPath = "/usr/bin/git";

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
        catch (OperationCanceledException)
        {
            // The CALLER cancelled. That must propagate: turning it into a verdict would hand a
            // caller who asked to stop a confident-looking answer about the machine instead. This
            // has to sit ABOVE the general catch below, which would otherwise swallow it.
            throw;
        }
        catch (Exception ex)
        {
            return new GitVersionProbe(false, -1, ex.Message);
        }
    }
}
