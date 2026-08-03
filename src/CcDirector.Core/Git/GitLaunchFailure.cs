namespace CcDirector.Core.Git;

/// <summary>
/// The one sentence the product shows when a git command could not be LAUNCHED at all - as opposed
/// to one that ran and failed, which speaks for itself.
///
/// It lives in ONE place because three of them render it - the read providers, the write service,
/// and the desktop Source Control view - and a sentence copied three times is a sentence that can
/// only ever be corrected in one of them.
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
    /// rather than being relabelled as a missing install and sending someone off to reinstall
    /// software that is already on their machine.
    /// </summary>
    /// <param name="nativeErrorCode">The operating system's own error number from the failed launch.</param>
    /// <param name="reason">What the failure said, used verbatim for every other code.</param>
    public static string Describe(int nativeErrorCode, string? reason)
        => Describe(nativeErrorCode, reason, OperatingSystem.IsWindows());

    /// <summary>
    /// The rule with the platform passed in. Exposed because the Windows-only reading of code 3 is
    /// otherwise UNFALSIFIABLE on Windows: a test running here cannot tell the correct rule from one
    /// that reads code 3 as a missing file everywhere, so the guard would have no test able to fail.
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
