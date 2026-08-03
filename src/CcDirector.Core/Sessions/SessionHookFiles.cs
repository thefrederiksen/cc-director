using CcDirector.Core.Storage;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Remove-the-network-port mission, phase 3: the ONE place that names the two files a session's
/// SessionStart hook uses, and the two environment variables the Director stamps to tell the hook
/// where they are.
///
/// The hook scripts used to call the Director's Control API for both jobs - a GET for the fleet
/// preamble and a POST to report a rotated transcript pointer. Both are now files, so the hook needs
/// no listening port, no address and no credential.
///
/// WHY THE PATHS ARE HANDED OVER RATHER THAN COMPUTED. The scripts are static and shared across every
/// session, and they run under PowerShell on Windows and POSIX shell on macOS and Linux. Working out
/// the storage root in each of those - per platform, and per NAMED INSTANCE, which moves the root -
/// would put a second copy of <see cref="CcStorage"/> into two shell dialects, where it could drift
/// from this one silently. The Director already knows the exact path, so it says so.
/// </summary>
public static class SessionHookFiles
{
    /// <summary>The environment variable naming the session's ready-to-print SessionStart hook output.
    /// A hook that does not see it is not in a Director session and prints nothing.</summary>
    public const string PreambleFileEnvVar = "CC_SESSION_PREAMBLE_FILE";

    /// <summary>The environment variable naming the file a Claude hook writes its current session id
    /// and transcript path into. A hook that does not see it reports nothing.</summary>
    public const string PointerFileEnvVar = "CC_SESSION_POINTER_FILE";

    /// <summary>The file extension both drops use, so the pointer watcher can ignore the temporary
    /// file an in-progress atomic write leaves beside the real one.</summary>
    public const string DropExtension = ".json";

    /// <summary>The maintained hook-output file for one session, under the real storage root.</summary>
    public static string PreamblePathFor(Guid sessionId) => PreamblePathFor(sessionId, directory: null);

    /// <summary>Testable overload: the maintained hook-output file under an explicit directory.</summary>
    public static string PreamblePathFor(Guid sessionId, string? directory)
        => Path.Combine(directory ?? CcStorage.SessionPreambles(), sessionId.ToString() + DropExtension);

    /// <summary>The pointer drop file for one session, under the real storage root.</summary>
    public static string PointerPathFor(Guid sessionId) => PointerPathFor(sessionId, directory: null);

    /// <summary>Testable overload: the pointer drop file under an explicit directory.</summary>
    public static string PointerPathFor(Guid sessionId, string? directory)
        => Path.Combine(directory ?? CcStorage.SessionPointers(), sessionId.ToString() + DropExtension);

    /// <summary>
    /// The session a drop file belongs to, read from its name, or null when the name is not a session
    /// id. Used by the pointer watcher: the file NAME carries the session, so a hook cannot report a
    /// pointer for a session other than the one whose path it was handed.
    /// </summary>
    public static Guid? SessionIdFromDropPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return Guid.TryParse(name, out var id) ? id : null;
    }

    /// <summary>
    /// Write <paramref name="content"/> to <paramref name="path"/> so a reader never sees half of it:
    /// into a sibling temporary file first, then a single move over the destination. The temporary
    /// name deliberately does NOT end in <see cref="DropExtension"/>, so a directory watcher filtering
    /// on that extension never sees the half-written copy at all.
    /// </summary>
    public static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Cannot determine directory for path: {path}");
        Directory.CreateDirectory(dir);
        var tmp = Path.ChangeExtension(path, ".tmp");
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
