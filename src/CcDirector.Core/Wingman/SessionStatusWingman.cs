using System.Collections.Concurrent;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Per-Director wingman that is the SINGLE WRITER of <see cref="Session.StatusColor"/>.
///
/// Phase 2.3 (issue #1177): the Director now computes ONLY the dumb standalone-desktop color map -
/// the no-Gateway fallback. The badge is a direct, mechanical mapping from the session's
/// <see cref="ActivityState"/> and nothing else:
///
///   Working / Starting            -> blue  ("working")
///   WaitingForInput / Perm / Idle -> red   ("needs you")
///   Exited                        -> gray  ("exited", the "unknown" color string the UI renders gray)
///
/// The richer colors that used to be layered here as overlays - transcribing orange, wingman-reading
/// yellow (briefing / auto-explain), background-running purple, brand-new "ready" green, and the
/// controlled-sub-agent slate (issue #815) - are GONE from the Director. When a Gateway is present it
/// owns every one of those: it folds them from the RAW FACTS this Director still reports on each
/// SessionDto (IsBrandNew, IsControlled, ControllerSessionId, IsBackgroundRunning, IsTranscribing,
/// IsExplaining, BriefingState, VoiceMode). The Director sets those facts on the Session as before -
/// this class simply no longer turns them into a color. A Gateway-less desktop therefore shows the
/// plain blue/red/gray map, which is the intended standalone fallback.
///
/// ActivityState itself is owned by the <see cref="TerminalStateDetector"/>, whose entire rule is:
/// bytes out of the ConPTY -> Working; <see cref="TerminalStateDetector.QuietThreshold"/> of complete
/// silence -> WaitingForInput. So in practice the badge is just blue or red.
///
/// It still wires a <see cref="PromptInjectionWatcher"/> per session, but that only mirrors text
/// Claude Code injects into its own input line back to the cc-director textbox - it never touches the
/// color.
/// </summary>
public sealed class SessionStatusWingman : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly ConcurrentDictionary<Guid, Action<ActivityState, ActivityState>> _activityHandlers = new();
    private readonly ConcurrentDictionary<Guid, PromptInjectionWatcher> _injectionWatchers = new();
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Debounce window between the last buffer write and running the prompt-input
    /// extraction. Long enough that Claude Code's Ink TUI has settled into a
    /// final frame; short enough that the user sees the suggestion almost
    /// immediately after Claude Code finishes drawing.
    /// </summary>
    internal static readonly TimeSpan PromptInjectionDebounce = TimeSpan.FromMilliseconds(500);

    public SessionStatusWingman(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    /// <summary>Begin watching sessions. Idempotent.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write("[SessionStatusWingman] Start");

        _sessionManager.OnSessionCreated += OnSessionCreated;

        // Wire existing sessions (restored from persistence on Director boot).
        foreach (var s in _sessionManager.ListSessions())
            WireSession(s, isNew: false);
    }

    private void OnSessionCreated(Session session) => WireSession(session, isNew: true);

    private void WireSession(Session session, bool isNew)
    {
        if (_activityHandlers.ContainsKey(session.Id)) return;

        // Initialize color from the current activity state - the dumb standalone map, no overlays.
        var (color, reason) = ColorFromActivityState(session.ActivityState, isNew);
        session.SetStatusColor(color, reason);
        FileLog.Write($"[SessionStatusWingman] init {session.Id} -> {color} ({reason})");

        // Brand-new session: seed a canned Wingman greeting so the Wingman tab has
        // content the moment the user opens it, with no Opus call. The
        // ProactiveExplainService skips the first turn-end briefing for IsBrandNew
        // sessions (nothing useful to summarize yet); this line is what the user reads
        // until they send their first prompt. This reads IsBrandNew; it does not set it.
        if (isNew && session.IsBrandNew)
        {
            session.SetCachedExplain(
                "This is a brand new session. Nothing to explain yet -- the Wingman will pick up after your first turn.",
                "system");
        }

        Action<ActivityState, ActivityState> handler = (oldState, newState) =>
        {
            try
            {
                var (c, r) = ColorFromActivityState(session.ActivityState, isNew: false);
                session.SetStatusColor(c, r);
                FileLog.Write($"[SessionStatusWingman] {session.Id} activity {oldState}->{newState} => {c} ({r})");

                // Durable record of every state transition (blue<->red), so a session's
                // history survives a Director restart. The in-memory ring on the Session
                // (RecordStateChange, populated in SetActivityState) feeds the live tab.
                StateChangeLog.Append(session.Id, new StateChangeLog.Record(
                    DateTime.UtcNow.ToString("o"), oldState.ToString(), newState.ToString(), c));

                // Keep the prompt-text mirror responsive: when the session settles at its
                // input box, nudge the injection watcher to scan now rather than waiting
                // for the next byte. This is text mirroring only - not a color decision.
                if (newState == ActivityState.WaitingForInput
                    && _injectionWatchers.TryGetValue(session.Id, out var w))
                    w.RequestImmediateScan();
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionStatusWingman] handler failed for {session.Id}: {ex.Message}");
            }
        };
        _activityHandlers[session.Id] = handler;
        session.OnActivityStateChanged += handler;

        // Subscribe to the byte stream so we re-scan Claude Code's input-prompt
        // line whenever the TUI redraws and then goes quiet. The watcher debounces
        // bursts; we only run extraction once writes settle.
        var buffer = session.Buffer;
        if (buffer is not null)
        {
            var watcher = new PromptInjectionWatcher(session, buffer);
            if (_injectionWatchers.TryAdd(session.Id, watcher))
                watcher.Start();
            else
                watcher.Dispose();
        }
    }

    /// <summary>
    /// The one and only state-to-color mapping (the standalone-desktop fallback). Working (and the
    /// brief Starting state) are blue; every state that means "not producing output, your turn" is red;
    /// a gone process is gray (the "unknown" color string the UI renders gray). The
    /// <see cref="TerminalStateDetector"/> only ever emits Working and WaitingForInput, so in practice
    /// the badge is just blue or red.
    /// </summary>
    internal static (string color, string reason) ColorFromActivityState(ActivityState state, bool isNew)
    {
        return state switch
        {
            ActivityState.Starting        => (StatusColor.Blue, isNew ? "session created" : "starting"),
            ActivityState.Working         => (StatusColor.Blue, "working"),
            ActivityState.WaitingForInput => (StatusColor.Red,  "needs you"),
            ActivityState.WaitingForPerm  => (StatusColor.Red,  "needs you"),
            ActivityState.Idle            => (StatusColor.Red,  "needs you"),
            ActivityState.Exited          => (StatusColor.Unknown, "exited"),
            _                             => (StatusColor.Unknown, "unknown activity state"),
        };
    }

    /// <summary>
    /// The voice-mode "yellow until audio ready" color rule (issue #553). A VOICE-MODE session that
    /// is waiting for the user (WaitingForInput / WaitingForPerm) must NOT show red "needs you" while
    /// its spoken audio is still being prepared: it stays YELLOW ("preparing voice / not ready yet")
    /// while the wingman is still generating (<paramref name="voiceGenerating"/>) OR there is no
    /// playable audio yet (<c>!</c><paramref name="voiceAudioReady"/>), and only turns RED once
    /// playable audio exists. Non-voice sessions, and voice sessions that are not at a waiting
    /// turn-end, are returned with their <paramref name="baseColor"/> unchanged.
    ///
    /// This is a shared, pure rule DEFINITION: the Director no longer applies it (Phase 2.3 removed the
    /// Director overlays), but the Gateway's SessionOrdering.EffectiveColor and the /m client's effColor
    /// apply the same rule on SessionDto.VoiceAudioReady / VoiceGenerating. Kept here so the rule is
    /// defined and unit-tested once, next to the rest of the color mapping.
    /// </summary>
    internal static (string color, string reason) VoiceColorFor(
        bool voiceMode, ActivityState state, bool voiceGenerating, bool voiceAudioReady,
        (string color, string reason) baseColor)
    {
        var atTurnEnd = state is ActivityState.WaitingForInput or ActivityState.WaitingForPerm;
        if (voiceMode && atTurnEnd && string.Equals(baseColor.color, StatusColor.Red, StringComparison.OrdinalIgnoreCase)
            && (voiceGenerating || !voiceAudioReady))
            return (StatusColor.Yellow, "preparing voice");
        return baseColor;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sessionManager.OnSessionCreated -= OnSessionCreated;
        foreach (var s in _sessionManager.ListSessions())
        {
            if (_activityHandlers.TryRemove(s.Id, out var h))
                s.OnActivityStateChanged -= h;
        }
        foreach (var kv in _injectionWatchers)
            kv.Value.Dispose();
        _injectionWatchers.Clear();
    }
}

/// <summary>
/// Watches a session's terminal buffer for text Claude Code has injected into
/// its own input-prompt line, and forwards detected text to
/// <see cref="Session.SetPendingPromptText"/> with source "wingman" so the
/// cc-director "Type a message..." textbox can mirror it.
///
/// Operation:
/// 1. Subscribe to <see cref="CircularTerminalBuffer.OnBytesWritten"/>.
/// 2. On each write, restart a 500ms debounce timer.
/// 3. When the timer fires (no new bytes for 500ms), snapshot the resolved grid
///    plus the live cursor and run
///    <see cref="PromptInputLineExtractor.ExtractUserAuthoredInput"/>. The cursor
///    lets us reject a dim history/autocomplete suggestion (cursor parked at the
///    start of the box) instead of mirroring it as if the user had entered it.
/// 4. If the extracted text is non-empty and differs from what we last pushed,
///    call <c>session.SetPendingPromptText(text, "wingman")</c>.
/// 5. The UI side decides whether to actually populate the visible textbox
///    (e.g. don't clobber what the user has already typed). This class is
///    intentionally ignorant of UI state.
///
/// State machine for <c>_lastPushedText</c>:
///  - null = nothing pushed yet for this session
///  - ""   = we observed an empty input box; resets the "already pushed" memory
///  - "X"  = we pushed "X"; don't push it again unless the extracted text changes
///
/// This means: if the user clears the cc-director textbox while Claude Code's
/// injection is still in the terminal, we will NOT re-inject — by the next
/// scan, <c>_lastPushedText</c> still equals "X" and we short-circuit. Only when
/// Claude Code itself changes its injection (or empties its prompt) does the
/// state reset.
/// </summary>
internal sealed class PromptInjectionWatcher : IDisposable
{
    private readonly Session _session;
    private readonly CircularTerminalBuffer _buffer;
    private readonly Action<byte[]> _onBytes;
    private readonly System.Threading.Timer _timer;
    private string? _lastPushedText;
    private int _disposed;

    public PromptInjectionWatcher(Session session, CircularTerminalBuffer buffer)
    {
        _session = session;
        _buffer = buffer;
        _onBytes = _ => Bump();
        _timer = new System.Threading.Timer(OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        _buffer.OnBytesWritten += _onBytes;
        FileLog.Write($"[PromptInjectionWatcher] start session={_session.Id}");
    }

    /// <summary>
    /// Force a scan at the next debounce window, without waiting for new bytes.
    /// Used on activity-state transitions where the relevant content may already
    /// be in the buffer.
    /// </summary>
    public void RequestImmediateScan() => Bump();

    private void Bump()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _timer.Change(SessionStatusWingman.PromptInjectionDebounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* race with Dispose; ignore */ }
    }

    private void OnTimerTick(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            // Read the RESOLVED grid plus the live cursor, not the raw byte buffer:
            // the cursor tells a real entry (cursor at the end of the box text) apart
            // from a dim history/autocomplete suggestion (cursor parked at the start).
            var (rows, cursorRow, cursorCol) = _session.SnapshotScreenRowsWithCursor();
            var extracted = PromptInputLineExtractor.ExtractUserAuthoredInput(rows, cursorRow, cursorCol);

            if (extracted is null)
            {
                // No Claude Code TUI frame detectable. Don't disturb whatever's in
                // the textbox; just reset our "already pushed" memory so the next
                // detected injection (after a frame change) is treated as new.
                _lastPushedText = null;
                return;
            }

            if (extracted.Length == 0)
            {
                // Claude Code's input box is empty. Reset memory so we'll push
                // again if it later fills with the same suggestion.
                _lastPushedText = null;
                return;
            }

            if (string.Equals(extracted, _lastPushedText, StringComparison.Ordinal))
                return; // already pushed this exact text — don't re-fire

            FileLog.Write($"[PromptInjectionWatcher] session={_session.Id} push len={extracted.Length} text=\"{Truncate(extracted, 80)}\"");
            _session.SetPendingPromptText(extracted, "wingman");
            _lastPushedText = extracted;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[PromptInjectionWatcher] tick failed session={_session.Id}: {ex.Message}");
        }
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _buffer.OnBytesWritten -= _onBytes;
        _timer.Dispose();
    }
}
