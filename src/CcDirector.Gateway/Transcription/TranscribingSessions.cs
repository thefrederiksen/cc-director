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
/// The clear is authoritative from two directions. The happy path is the explicit <see cref="End"/>
/// call the dictation-upload flow makes on every terminal outcome. The safety net is the
/// <see cref="IdleTimeout"/> backstop: a mark that has seen no progress (no <see cref="Begin"/> or
/// <see cref="Refresh"/>) for the idle window is treated as abandoned on the next read and removed,
/// so a client that goes offline mid-upload - phone locked, app killed, network dropped - or a
/// completion that the explicit clear somehow missed cannot wedge a session orange for long.
///
/// The window is deliberately an IDLE timeout, not a fixed age from the first mark: the active upload
/// path calls <see cref="Refresh"/> as it makes progress (each chunk stored, each completion attempt),
/// so a genuinely slow upload keeps its mark alive and is never cut short, while an abandoned one goes
/// quiet and ages out within the idle window. Issue #1126 shortened this from a fixed 20-minute age -
/// which wedged sessions orange for a full 20 minutes whenever the explicit clear was skipped - to
/// this short idle window.
/// </summary>
public sealed class TranscribingSessions
{
    /// <summary>How long a mark may stand with NO progress (no <see cref="Begin"/> or
    /// <see cref="Refresh"/>) before it is treated as a stale, abandoned mark and removed. Comfortably
    /// longer than the gap between progress events on a healthy upload (a dictation clip is small and
    /// each stored chunk / completion attempt refreshes the mark), so it only ever fires for a client
    /// that has genuinely stopped making progress.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);

    private readonly ConcurrentDictionary<string, DateTime> _lastProgress = new(); // sid -> last progress time (UTC)
    private readonly Func<DateTime> _utcNow;

    /// <summary>Production uses the wall clock; tests inject a controllable clock so the
    /// <see cref="IdleTimeout"/> backstop is exercisable without waiting real seconds.</summary>
    public TranscribingSessions(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Mark a session as transcribing (idempotent). Stamps the progress time so a fresh Send
    /// restarts the idle backstop clock rather than inheriting an older mark's age.</summary>
    public void Begin(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        _lastProgress[sessionId] = _utcNow();
        FileLog.Write($"[TranscribingSessions] sid={sessionId}: begin transcribing");
    }

    /// <summary>Record progress on an EXISTING mark (a chunk stored, a completion attempt) so an active
    /// but slow upload keeps its mark alive past the idle backstop. A no-op when the session is not
    /// currently marked: Refresh keeps a live mark alive, it never resurrects a cleared one.</summary>
    public void Refresh(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        // Only bump an existing mark. Using TryGetValue-then-set (not an unconditional write) keeps a
        // Refresh that races an End from re-creating the mark End just removed.
        if (_lastProgress.ContainsKey(sessionId))
            _lastProgress[sessionId] = _utcNow();
    }

    /// <summary>Clear a session's transcribing mark (idempotent). The authoritative clear, called by
    /// the dictation-upload flow on every terminal outcome.</summary>
    public void End(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        if (_lastProgress.TryRemove(sessionId, out _))
            FileLog.Write($"[TranscribingSessions] sid={sessionId}: end transcribing");
    }

    /// <summary>Whether the session is currently transcribing. A mark that has seen no progress for
    /// <see cref="IdleTimeout"/> is treated as an abandoned mark and removed on read, so a
    /// crashed/offline client cannot wedge the session orange.</summary>
    public bool IsTranscribing(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        if (!_lastProgress.TryGetValue(sessionId, out var since)) return false;
        if (_utcNow() - since <= IdleTimeout) return true;
        if (_lastProgress.TryRemove(sessionId, out _))
            FileLog.Write($"[TranscribingSessions] sid={sessionId}: mark idle for over {IdleTimeout.TotalSeconds:0}s, cleared");
        return false;
    }
}
