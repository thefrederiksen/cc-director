using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// The DIRECTOR-side read of the enforced dictation session lock (issue #1181, Task 3b). The lock is
/// a pure projection of the durable PENDING delivery marker the Gateway writes at
/// <see cref="CcStorage.DictationUploads"/><c>/&lt;uploadId&gt;/record.json</c> (issue #1188). A session
/// is LOCKED exactly while at least one of those markers is in state <c>Pending</c> and names it - the
/// SAME rule the Gateway enforces at its front door with <c>VoiceUploadStore.IsSessionLocked</c>.
///
/// Why a separate reader here rather than calling the Gateway's <c>VoiceUploadStore</c>: the Director
/// executable references only <c>CcDirector.Gateway.Contracts</c>, not the full Gateway assembly. On a
/// single machine (the phone-dictates-into-my-desktop case) the Gateway and Director share the same
/// <c>%LOCALAPPDATA%\cc-director</c>, so the Director reads the identical markers straight from disk -
/// no network, restart-safe, and the lock never auto-releases because it is a pure function of the
/// durable markers (exactly the Task 3a philosophy). This reads only the two fields it needs
/// (<c>State</c>, <c>SessionId</c>) so it does not couple to the Gateway's full record shape; the
/// writer of record is <c>CcDirector.Gateway.Voice.DictationDeliveryRecord</c>.
///
/// Cross-machine (a Director on another box than its Gateway) is NOT covered by this disk read - the
/// marker lives on the Gateway's machine. That path is a documented follow-up; here the reader simply
/// finds no marker and reports unlocked, which fails OPEN for the remote desktop only.
/// </summary>
public static class DictationLockReader
{
    private static readonly JsonSerializerOptions ReadJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// True when a dictation is inbound to <paramref name="sessionId"/>: some
    /// <c>record.json</c> under the shared dictation-uploads root is in state <c>Pending</c> and
    /// names this session. Reads the production root (<see cref="CcStorage.DictationUploads"/>).
    /// </summary>
    public static bool IsSessionLocked(Guid sessionId)
        => IsSessionLocked(CcStorage.DictationUploads(), sessionId.ToString());

    /// <summary>
    /// Test seam: check against an explicit uploads root. Robust to a missing root, half-written
    /// markers, and unrelated files - any read error is treated as "no lock from this marker" so a
    /// transient disk hiccup can never wedge a session locked, only ever miss a lock (fail open),
    /// which the fail-closed <see cref="SendSource.UserInput"/> default already compensates for.
    /// </summary>
    public static bool IsSessionLocked(string uploadsRoot, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;

        string[] dirs;
        try
        {
            if (!Directory.Exists(uploadsRoot)) return false;
            dirs = Directory.GetDirectories(uploadsRoot);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DictationLockReader] enumerate {uploadsRoot} failed: {ex.Message}");
            return false;
        }

        foreach (var dir in dirs)
        {
            var marker = ReadMarker(Path.Combine(dir, "record.json"));
            if (marker is null) continue;
            if (string.Equals(marker.State, "Pending", StringComparison.OrdinalIgnoreCase)
                && string.Equals(marker.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Every session id with an inbound dictation, from ONE pass over the uploads root. Reads the
    /// production root (<see cref="CcStorage.DictationUploads"/>).
    /// </summary>
    public static IReadOnlySet<string> LockedSessionIds()
        => LockedSessionIds(CcStorage.DictationUploads());

    /// <summary>
    /// The bulk read behind the roster's "receiving a dictation" paint (issue #1111). Asking
    /// <see cref="IsSessionLocked(Guid)"/> once per session re-enumerated this directory and re-read
    /// every marker once per session, so a Director holding two dozen sessions did hundreds of file
    /// reads a second to answer one question whose answer is the SAME for every session in a tick.
    /// This answers it once: one enumeration, each marker read exactly once, and the caller then asks
    /// the returned set per session for free.
    ///
    /// Deliberately NOT a replacement for <see cref="IsSessionLocked(string, string)"/>. That one stays
    /// as it is: it is the single-session question, and callers depending on its exact posture keep it
    /// unchanged. This method matches that posture rather than inventing a new one - it FAILS OPEN the
    /// same way (an unreadable root or a half-written marker contributes no lock, never a false lock),
    /// so the two can never disagree about a marker they both managed to read.
    ///
    /// Returns an ordinal-ignore-case set because the marker's session id is compared case-insensitively
    /// by the single-session path, and a caller passing a <c>Guid.ToString()</c> must get the same answer.
    /// </summary>
    public static IReadOnlySet<string> LockedSessionIds(string uploadsRoot)
    {
        var locked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string[] dirs;
        try
        {
            if (!Directory.Exists(uploadsRoot)) return locked;
            dirs = Directory.GetDirectories(uploadsRoot);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DictationLockReader] enumerate {uploadsRoot} failed: {ex.Message}");
            return locked;
        }

        foreach (var dir in dirs)
        {
            var marker = ReadMarker(Path.Combine(dir, "record.json"));
            if (marker is null) continue;
            if (string.Equals(marker.State, "Pending", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(marker.SessionId))
            {
                locked.Add(marker.SessionId);
            }
        }

        return locked;
    }

    private static Marker? ReadMarker(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<Marker>(json, ReadJson);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DictationLockReader] read {path} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>The two fields of the Gateway's durable delivery record this reader needs. The
    /// <c>State</c> is the string form the Gateway writes with a <c>JsonStringEnumConverter</c>.</summary>
    private sealed record Marker(string State, string SessionId);
}
