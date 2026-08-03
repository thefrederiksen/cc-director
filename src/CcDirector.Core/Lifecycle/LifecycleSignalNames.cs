using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Instances;
using CcDirector.Core.Storage;

namespace CcDirector.Core.Lifecycle;

/// <summary>
/// The names of every lifecycle signal, written once so the process that listens and the process that
/// raises cannot disagree about a string.
///
/// EVERY NAME IS SCOPED, and the scope is the whole point. A signal called "shut down" with no scope
/// would stop whichever Director happened to be listening - and this machine routinely runs several, as
/// named instances and development slots. A Director signal is therefore keyed by its DirectorId, which
/// is the only identifier that names ONE process; a launcher signal is keyed by the storage root it
/// serves, so a test rig with its own root and the installed launcher never hear each other.
///
/// The Director's identifier and the launcher's root are both read from files - the instance
/// registration and the storage layout - so a sender needs nothing running and nothing listening to
/// work out what to call.
/// </summary>
public static class LifecycleSignalNames
{
    /// <summary>Ask a specific Director to shut down cleanly - the replacement for POST /shutdown.</summary>
    public static string DirectorShutdown(string directorId)
        => $"cc-director-shutdown-{Require(directorId, nameof(directorId))}";

    /// <summary>Ask a specific Director to check for an update now - the replacement for POST /update/check.</summary>
    public static string DirectorUpdateCheck(string directorId)
        => $"cc-director-update-check-{Require(directorId, nameof(directorId))}";

    /// <summary>Ask the launcher serving <paramref name="sharedRoot"/> to quit.</summary>
    public static string LauncherShutdown(string? sharedRoot = null)
        => $"cc-director-launcher-shutdown-{RootKey(sharedRoot)}";

    /// <summary>
    /// Ask the launcher serving <paramref name="sharedRoot"/> to restart the Director it supervises -
    /// which is what installs a staged update. The replacement for POST /director/restart.
    /// </summary>
    public static string LauncherRestartDirector(string? sharedRoot = null)
        => $"cc-director-launcher-restart-director-{RootKey(sharedRoot)}";

    /// <summary>
    /// A short, stable, path-safe key for a storage root.
    ///
    /// It defaults to <see cref="InstanceContext.SharedRoot"/> rather than
    /// <see cref="CcStorage.Root"/> on purpose. A Director redirects CcStorage to its own instance home,
    /// so asking CcStorage for "the root" inside a Director gives the INSTANCE home and every named
    /// instance would compute a different key for the one launcher they all share. SharedRoot is
    /// captured before that redirect and is the same value the launcher itself reads.
    /// </summary>
    public static string RootKey(string? sharedRoot = null)
    {
        var root = sharedRoot ?? InstanceContext.SharedRoot;
        var normalized = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    private static string Require(string value, string name)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A Director identifier is required to name a Director signal", name)
            : value.Trim().ToLowerInvariant();
}
