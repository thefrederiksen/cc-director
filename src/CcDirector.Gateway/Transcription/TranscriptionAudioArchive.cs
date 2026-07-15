using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// Rolling on-disk archive of the audio behind every transcription the Gateway performs.
///
/// WHY THIS EXISTS. The desktop dictation safety net (<c>DictationRecordingStore</c>) saves the clip
/// before transcribing and DELETES it the moment the words are delivered. That net catches a
/// transcription that FAILS. It does not catch one that LIES: a truncated transcript is a "success", so
/// the audio that would prove it is deleted seconds later. When a turn came back with a fraction of
/// what was said, the recording was already gone and the loss could not be localized - the byte count
/// matching the wall clock proves no samples were DROPPED, but it can never prove the bytes carried
/// speech. Only the audio can settle that. So the audio is now KEPT.
///
/// WHY HERE. This sits beside <see cref="TranscriptionTelemetryLog"/> on purpose. That log is the one
/// place that sees EVERY turn from every surface (desktop Send, the Speak dialog, the phone, Car Mode),
/// because they all transcribe through <see cref="GatewayTranscriptionService"/>. The two desktop
/// dictation paths do not agree on this: only the fire-and-forget Send saves a clip at all. Archiving at
/// the choke point covers every surface once instead of per-caller.
///
/// The file is named for the same <c>turnId</c> the telemetry log records, so a suspicious line in
/// transcription-log-YYYYMMDD.jsonl leads straight to the audio that produced it.
///
/// BOUNDED BY CONSTRUCTION. This is a diagnostic window, not an archive that grows forever: every save
/// prunes clips older than <see cref="MaxAge"/> and, whatever their age, all but the newest
/// <see cref="MaxClips"/>. Both bounds apply, so neither a quiet week nor a busy hour can run the disk up.
///
/// Privacy: LOCAL disk only, exactly like the telemetry text it sits next to. Never transmitted.
///
/// Fail-safe: archiving must never break a transcription. Every operation swallows and logs its errors,
/// the same fail-open contract as the telemetry log.
/// </summary>
public sealed class TranscriptionAudioArchive
{
    /// <summary>Process-wide shared archive used by the transcription service.</summary>
    public static readonly TranscriptionAudioArchive Shared = new();

    /// <summary>How long a clip is kept. A problem reported "yesterday" must still have its audio.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Hard ceiling on clip count regardless of age, so a heavy dictation day cannot fill the disk
    /// before <see cref="MaxAge"/> retires anything. At a typical turn size this is a few hundred
    /// megabytes at the very worst.
    /// </summary>
    public const int MaxClips = 500;

    private readonly object _gate = new();
    private readonly string _directory;

    /// <param name="directory">Override the archive directory (tests). Defaults to the per-user location.</param>
    public TranscriptionAudioArchive(string? directory = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory() : directory;
    }

    /// <summary>The per-user transcription-audio directory.</summary>
    public static string DefaultDirectory() => CcStorage.TranscriptionAudio();

    /// <summary>The file a clip for <paramref name="turnId"/> lands in.</summary>
    public string FileFor(string turnId, string extension)
        => Path.Combine(_directory, $"turn-{SafeName(turnId)}{extension}");

    /// <summary>
    /// Keep a turn id to characters a filename allows. Production ids are GUIDs and pass through
    /// untouched; this only stops a hand-supplied id from escaping the archive directory.
    /// </summary>
    private static string SafeName(string name)
        => string.Join("_", name.Split(Path.GetInvalidFileNameChars()));

    /// <summary>
    /// Archive the exact bytes sent for transcription and return the saved path, or null when nothing
    /// was saved. Never throws: a full disk must degrade the diagnostics, never the transcription.
    /// </summary>
    /// <param name="turnId">The telemetry turn id; ties the clip to its transcription-log line.</param>
    /// <param name="audio">The clip bytes, exactly as sent to the provider.</param>
    /// <param name="contentType">The clip's MIME type, used to pick a playable file extension.</param>
    public string? TrySave(string turnId, byte[] audio, string contentType)
    {
        if (string.IsNullOrWhiteSpace(turnId)) return null;
        if (audio is null || audio.Length == 0) return null;

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                var path = FileFor(turnId, ExtensionFor(contentType));
                File.WriteAllBytes(path, audio);
                FileLog.Write($"[TranscriptionAudioArchive] archived {audio.Length} bytes for turn {turnId} to {path}");
                Prune();
                return path;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TranscriptionAudioArchive] TrySave FAILED for turn {turnId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Enforce both bounds: drop anything past <see cref="MaxAge"/>, then drop the oldest until at most
    /// <see cref="MaxClips"/> remain. Caller holds the lock. Never throws - a clip that cannot be
    /// deleted is disk noise, not a functional problem.
    /// </summary>
    private void Prune()
    {
        var files = new DirectoryInfo(_directory)
            .GetFiles("turn-*")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        var cutoff = DateTime.UtcNow - MaxAge;
        var doomed = files
            .Where((f, index) => index >= MaxClips || f.LastWriteTimeUtc < cutoff)
            .ToList();

        foreach (var file in doomed)
        {
            try
            {
                file.Delete();
                FileLog.Write($"[TranscriptionAudioArchive] pruned {file.Name}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TranscriptionAudioArchive] prune FAILED for {file.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// A playable extension for the clip's MIME type. The archive exists to be listened to, so the file
    /// must open in a player by double-click. An unrecognized type keeps its bytes under .bin rather
    /// than claiming a format it may not be.
    /// </summary>
    private static string ExtensionFor(string contentType) => contentType?.ToLowerInvariant() switch
    {
        "audio/wav" or "audio/wave" or "audio/x-wav" => ".wav",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/webm" => ".webm",
        "audio/ogg" => ".ogg",
        "audio/mp4" or "audio/m4a" or "audio/x-m4a" => ".m4a",
        "audio/flac" or "audio/x-flac" => ".flac",
        _ => ".bin",
    };
}
