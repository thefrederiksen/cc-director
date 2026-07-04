using System.Collections.Concurrent;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The Gateway-owned, in-memory set of sessions whose dictated utterance is currently being
/// transcribed. When the phone's Speak dialog is committed with Send, the phone releases the screen
/// immediately and, in the background, uploads + transcribes the recorded audio and then submits the
/// resulting text into the session. While that is in flight the session is marked here, which the
/// /sessions aggregator stamps onto <see cref="Contracts.SessionDto.Transcribing"/> so every client
/// paints the session orange ("Transcribing...") and nobody else starts using it.
///
/// In-memory by design (transient UI state, not a durable record): a Gateway restart simply clears
/// it, which is the correct resting state.
///
/// The client's explicit <see cref="End"/> call is the authoritative clear (it fires in the phone's
/// finally block once the transcript has been submitted or has failed). The <see cref="MaxAge"/>
/// backstop only exists so a client that goes offline mid-upload - phone locked, app killed, network
/// dropped - cannot wedge a session orange forever: a mark older than the cap is treated as expired
/// on the next read and removed. The cap is deliberately generous (longer than any realistic
/// dictation upload) so it never cuts a genuine transcription short.
/// </summary>
public sealed class TranscribingSessions
{
    /// <summary>How long a mark may stand before it is treated as a stale, abandoned mark. Longer
    /// than any realistic record-then-upload-then-transcribe round trip, including a long clip on a
    /// poor phone network, so it only ever fires for a client that never called <see cref="End"/>.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(20);

    private readonly ConcurrentDictionary<string, DateTime> _since = new(); // sid -> mark time (UTC)
    private readonly Func<DateTime> _utcNow;

    /// <summary>Production uses the wall clock; tests inject a controllable clock so the
    /// <see cref="MaxAge"/> backstop is exercisable without waiting real minutes.</summary>
    public TranscribingSessions(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Mark a session as transcribing (idempotent). Refreshes the mark time so a fresh Send
    /// restarts the backstop clock rather than inheriting an older mark's age.</summary>
    public void Begin(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        _since[sessionId] = _utcNow();
        FileLog.Write($"[TranscribingSessions] sid={sessionId}: begin transcribing");
    }

    /// <summary>Clear a session's transcribing mark (idempotent). The authoritative clear, called by
    /// the client once the transcript has been submitted or the attempt has failed.</summary>
    public void End(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        if (_since.TryRemove(sessionId, out _))
            FileLog.Write($"[TranscribingSessions] sid={sessionId}: end transcribing");
    }

    /// <summary>Whether the session is currently transcribing. A mark older than <see cref="MaxAge"/>
    /// is treated as an abandoned mark and removed, so a crashed/offline client cannot wedge the
    /// session orange forever.</summary>
    public bool IsTranscribing(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        if (!_since.TryGetValue(sessionId, out var since)) return false;
        if (_utcNow() - since <= MaxAge) return true;
        if (_since.TryRemove(sessionId, out _))
            FileLog.Write($"[TranscribingSessions] sid={sessionId}: mark expired after {MaxAge.TotalMinutes:0} min, cleared");
        return false;
    }
}
