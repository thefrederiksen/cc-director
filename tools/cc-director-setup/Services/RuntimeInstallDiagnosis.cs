namespace CcDirectorSetup.Services;

/// <summary>Why an automatic prerequisite install did not happen.</summary>
public enum RuntimeInstallFailure
{
    /// <summary>Windows would not let this install proceed without an administrator.</summary>
    ElevationRequired,

    /// <summary>Anything else - a download failure, a bad package, an unreadable installer.</summary>
    Other,
}

/// <summary>
/// Turns an automatic-install failure into words a person can act on.
///
/// What the screen used to say was:
///
///   winget could not install .NET 10 Runtime (exit -1978335226). Use the download link to
///   install it manually, then click Re-check.
///
/// which names a tool the user has never heard of, shows a raw negative error number, never
/// states the actual cause, and then recommends a manual download that needs an administrator
/// too - so the advice leads straight back to the same wall. The raw code belongs in the setup
/// log; the screen gets the cause and the way out.
/// </summary>
public static class RuntimeInstallDiagnosis
{
    /// <summary>
    /// The package manager's "the installer I shelled out to did not complete" result
    /// (0x8A150006). Installing a machine-wide runtime from a non-elevated session lands here:
    /// the runtime's own installer needs an administrator and cannot ask for one, because the
    /// install runs with interactivity disabled.
    /// </summary>
    public const int ShellExecInstallFailed = unchecked((int)0x8A150006);

    /// <summary>ERROR_CANCELLED - the user dismissed the Windows elevation prompt.</summary>
    public const int ErrorCancelled = 1223;

    /// <summary>ERROR_ELEVATION_REQUIRED - Windows refused to start the installer unelevated.</summary>
    public const int ErrorElevationRequired = 740;

    /// <summary>Windows Installer's own "this install needs elevated privileges".</summary>
    public const int MsiErrorInstallServiceFailure = 1601;

    /// <summary>Windows Installer's "administrator rights are required".</summary>
    public const int MsiErrorInstallPackageRejected = 1625;

    /// <summary>Does this exit code mean an administrator was needed and was not there?</summary>
    public static RuntimeInstallFailure Classify(int exitCode) => exitCode switch
    {
        ShellExecInstallFailed => RuntimeInstallFailure.ElevationRequired,
        ErrorCancelled => RuntimeInstallFailure.ElevationRequired,
        ErrorElevationRequired => RuntimeInstallFailure.ElevationRequired,
        MsiErrorInstallServiceFailure => RuntimeInstallFailure.ElevationRequired,
        MsiErrorInstallPackageRejected => RuntimeInstallFailure.ElevationRequired,
        _ => RuntimeInstallFailure.Other,
    };

    /// <summary>
    /// What the Prerequisites screen shows. Never names the package manager and never prints a
    /// bare error number - the number is in the log, where it is useful and cannot mislead.
    /// </summary>
    /// <param name="displayName">The row the user clicked, e.g. ".NET 10 Runtime".</param>
    /// <param name="failure">The classified cause.</param>
    /// <param name="logPath">The setup log, so a failure is never a dead end.</param>
    public static string Message(string displayName, RuntimeInstallFailure failure, string logPath) =>
        failure switch
        {
            // Says WHO can fix it and WHAT to do. Crucially it does not send the user to the
            // manual download link, which needs an administrator just the same.
            RuntimeInstallFailure.ElevationRequired =>
                $"Windows needs an administrator to install {displayName}. Ask an administrator to "
                + "run this setup, or sign in to Windows as one, then click Re-check.",

            _ =>
                $"{displayName} could not be installed automatically. Use the download link to "
                + $"install it manually, then click Re-check. The details are in the setup log: {logPath}",
        };

    /// <summary>The short words for the row's own status column.</summary>
    public static string RowStatus(RuntimeInstallFailure failure) =>
        failure == RuntimeInstallFailure.ElevationRequired ? "Needs an administrator" : "Install failed";
}
