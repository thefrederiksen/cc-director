using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The single place the Gateway records a MOBILE dictation capture-health measurement (issue #863) - the
/// audio-loss deficit measured as recording wall-clock versus decoded audio duration. Both the Wingman voice
/// complete path (<see cref="GatewayWingmanVoiceEndpoint"/>) and the durable dictation complete path
/// (<see cref="GatewayDictationEndpoint"/>) call THIS, so a Voice-mode utterance and a Terminal/Chat Send land
/// the same-shaped diagnostic through one implementation, never two drifting copies.
///
/// DIAGNOSTIC ONLY: the measurement changes no transcription behaviour, and it is written to the Gateway log
/// (never a response, never a per-turn failure). It used to also append the whole record - INCLUDING the
/// cleaned transcript - to the host-global <c>dictation/sessions/*.jsonl</c> flat file, which had to be
/// SKIPPED on hosted because that file was one shared, unpartitioned log with no tenant in its path. Issue
/// #509 retired that flat file: the transcript itself is now stored per-tenant in the <c>dictation_transcripts</c>
/// table by the transcription service, so there is nothing tenant-mixing left to gate here. The capture-health
/// deficit has no reader of its own, so it stays a log line on every surface (self-host and hosted alike) -
/// no <c>IsHosted</c> branch.
/// </summary>
internal static class MobileCaptureHealthLog
{
    /// <summary>
    /// Log one capture-health measurement when the client supplied it. A null <paramref name="recordedMs"/>
    /// means the client did not opt in (or its on-device decode failed), so there is nothing to record and
    /// this is a no-op.
    /// </summary>
    /// <param name="uploadId">The upload id, used to correlate the measurement with the turn.</param>
    /// <param name="source">Surface tag: "mobile" for the Voice-mode path, "mobile-send" for the durable
    /// Terminal/Chat Send path, so the two surfaces are told apart in the log.</param>
    /// <param name="recordedMs">Recording wall-clock the client measured; null when the client did not opt in.</param>
    /// <param name="decodedSeconds">Decoded audio duration the client measured; the deficit yardstick.</param>
    /// <param name="sourceBytes">Size of the client's source (compressed) blob, for reference only.</param>
    /// <param name="audioBytes">Bytes of audio the server actually transcribed (the assembled clip).</param>
    /// <param name="cleaned">The cleaned transcript. Retained on the signature so callers are unchanged; the
    /// transcript itself is now persisted per-tenant by the transcription service (issue #509), not here.</param>
    public static void Persist(
        string uploadId, string source, double? recordedMs, double? decodedSeconds, long? sourceBytes,
        int audioBytes, string? cleaned)
    {
        _ = cleaned; // the transcript is stored per-tenant by the transcription service (issue #509), not here
        if (recordedMs is not { } wallMs) return; // client did not opt in - nothing to record

        var decoded = decodedSeconds ?? 0;
        FileLog.Write($"[MobileCaptureHealth] {source} {uploadId}: recordedMs={wallMs:F0}, "
            + $"decodedSec={decoded:F2}, audioBytes={audioBytes}, sourceBytes={sourceBytes ?? 0}");
    }
}
