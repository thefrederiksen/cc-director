using CcDirector.Core.Dictation;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The single place the Gateway persists a MOBILE dictation capture-health record (issue #863) - the
/// audio-loss deficit measured as recording wall-clock versus decoded audio duration. Both the Wingman
/// voice complete path (<see cref="GatewayWingmanVoiceEndpoint"/>) and the durable dictation complete
/// path (<see cref="GatewayDictationEndpoint"/>) call THIS, so a Voice-mode utterance and a Terminal/Chat
/// Send land the same-shaped record in the same log - one implementation, never two drifting copies.
///
/// The measurement is diagnostics only: it changes no transcription behaviour, and a logging failure
/// never affects the response (fire-and-forget on a background task).
///
/// On the hosted Gateway the write is SKIPPED (MTR-10 Gap H3): the session log is one shared,
/// unpartitioned file with a single static lock and no reader on hosted, so persisting it would mix every
/// tenant's cleaned transcript at rest. Because both endpoints route through <see cref="Persist"/>, gating
/// it here closes the hosted write path in one place. Mirrors the archive skip #1985 / telemetry skip #1897.
/// </summary>
internal static class MobileCaptureHealthLog
{
    /// <summary>
    /// Append one capture-health record when the client supplied its measurements. A null
    /// <paramref name="recordedMs"/> means the client did not opt in (or its on-device decode failed),
    /// so there is nothing to persist and this is a no-op. The transcription-context fields a desktop
    /// record carries (dictionary counts, cleanup model) are left at their defaults - this record exists
    /// only to carry the audio-loss deficit.
    /// </summary>
    /// <param name="uploadId">The upload id, used as the record's session id (as the desktop log does).</param>
    /// <param name="source">Surface tag: "mobile" for the Voice-mode path, "mobile-send" for the durable
    /// Terminal/Chat Send path, so the two surfaces are told apart in the log.</param>
    /// <param name="recordedMs">Recording wall-clock the client measured; null when the client did not opt in.</param>
    /// <param name="decodedSeconds">Decoded audio duration the client measured; the deficit yardstick.</param>
    /// <param name="sourceBytes">Size of the client's source (compressed) blob, for reference only.</param>
    /// <param name="audioBytes">Bytes of audio the server actually transcribed (the assembled clip).</param>
    /// <param name="cleaned">The cleaned transcript, stored for context (never re-transcribed here).</param>
    public static void Persist(
        string uploadId, string source, double? recordedMs, double? decodedSeconds, long? sourceBytes,
        int audioBytes, string? cleaned)
    {
        var record = BuildRecord(uploadId, source, recordedMs, decodedSeconds, sourceBytes, audioBytes, cleaned);
        if (record is null) return; // client did not opt in - nothing to persist

        // HOSTED WRITE GATE (MTR-10 Gap H3). DictationSessionLog appends every record to ONE process/user
        // file (dictation/sessions/YYYY-MM-DD.jsonl) under a single static lock, with no tenant in the path,
        // no tenant in the API and no per-tenant lock - and the record carries the customer's CleanedTranscript.
        // That is right for single-tenant self-host, but on a multi-tenant hosted Gateway (both this and the
        // Voice complete path reach it) every account's transcript text and diagnostics would mix at rest in
        // that one file and contend on that one lock. The log is a write-only diagnostic aid with no
        // production reader and no hosted read route, so there is nothing on hosted to serve it: mirror the
        // transcription-audio archive skip beside it (MTR-10 Gap A, #1985) and the telemetry skip before that
        // (#1897) - stop the write on hosted. Self-host (including a self-host Gateway) stays byte-identical.
        if (GatewayHostedMode.IsHosted)
        {
            FileLog.Write($"[MobileCaptureHealth] {source} {uploadId}: SKIPPED on hosted - the dictation session log has no tenant partition and no reader on hosted (MTR-10 Gap H3; mirrors the archive skip #1985)");
            return;
        }

        FileLog.Write($"[MobileCaptureHealth] {source} {uploadId}: recordedMs={record.RecordedWallMs:F0}, "
            + $"decodedSec={record.DecodedAudioSeconds:F2}, audioBytes={audioBytes}, sourceBytes={sourceBytes ?? 0}");

        Task.Run(() => DictationSessionLog.TryAppend(record));
    }

    /// <summary>
    /// The pure half of <see cref="Persist"/>: the dictation session record to append, or null when the
    /// client sent no measurement (<paramref name="recordedMs"/> is null) and there is nothing to persist.
    /// Split out so the null-guard and the field mapping are unit-testable without the fire-and-forget
    /// append to the shared log.
    /// </summary>
    internal static DictationSessionRecord? BuildRecord(
        string uploadId, string source, double? recordedMs, double? decodedSeconds, long? sourceBytes,
        int audioBytes, string? cleaned)
    {
        if (recordedMs is not { } wallMs) return null; // client did not opt in - nothing to persist

        var decoded = decodedSeconds ?? 0;
        return new DictationSessionRecord(
            TimestampUtc: DateTime.UtcNow.ToString("o"),
            SessionId: uploadId,
            Profile: "default",
            VocabularyTermCount: 0,
            MistranscriptionPatternCount: 0,
            RecordingDurationMs: (long)wallMs,
            StopToTranscribedMs: 0,
            StopToCleanedMs: 0,
            AudioBytesReceived: (int)Math.Min(audioBytes, int.MaxValue),
            RawTranscript: "",
            CleanedTranscript: cleaned ?? "",
            CleanupApplied: false,
            CleanupReason: null,
            CleanupModel: "",
            RemoteIp: null,
            ClientError: null,
            Source: source,
            RecordedWallMs: wallMs,
            DecodedAudioSeconds: decoded);
    }
}
