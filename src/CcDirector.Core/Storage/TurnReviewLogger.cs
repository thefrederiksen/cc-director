using System.Collections.Concurrent;
using System.Text;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Storage;

/// <summary>
/// Writes a <see cref="TurnReviewLog"/> record every time a session's state flips from
/// Working to <see cref="ActivityState.WaitingForInput"/> - i.e. the moment OUR detector
/// decides "I need the user". That is the one and only trigger; there is no Claude Code hook
/// involved. One flip == one turn == one record (cosmetic repaints don't flip state, so the
/// noise is filtered for free).
///
/// Read-only over sessions: it snapshots the resolved screen, the transcript produced during
/// the turn (bytes since the previous flip), and whatever the Wingman had said/done, then
/// writes off the event thread. One instance per Director.
/// </summary>
public sealed class TurnReviewLogger : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly ConcurrentDictionary<Guid, Action<ActivityState, ActivityState>> _handlers = new();
    private readonly ConcurrentDictionary<Guid, long> _cursors = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastFlipAt = new();
    private bool _started;
    private int _disposed;

    private readonly ITurnEndScreenSink? _screenSink;

    public TurnReviewLogger(SessionManager sessionManager) : this(sessionManager, null) { }

    /// <param name="screenSink">Where the turn-end screen is ALSO sent, or null to keep the local record
    /// only. The Terminal Rules mission pushes the screen to the Gateway from here rather than adding a
    /// second capture: this class already fires on the one trigger that means "the screen has stopped
    /// moving and now means something", and a second trigger would eventually disagree with this one.</param>
    public TurnReviewLogger(SessionManager sessionManager, ITurnEndScreenSink? screenSink)
    {
        _sessionManager = sessionManager;
        _screenSink = screenSink;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write("[TurnReviewLogger] Start");
        _sessionManager.OnSessionCreated += WireSession;
        _sessionManager.OnSessionRemoved += UnwireSession;
        foreach (var s in _sessionManager.ListSessions())
            WireSession(s);
    }

    private void WireSession(Session session)
    {
        if (_handlers.ContainsKey(session.Id)) return;
        // Start the transcript cursor at the current end of the buffer so the first record
        // covers only output produced from here on, and anchor the turn-start clock now.
        _cursors[session.Id] = session.Buffer?.TotalBytesWritten ?? 0;
        _lastFlipAt[session.Id] = DateTime.UtcNow;

        Action<ActivityState, ActivityState> handler = (_, @new) =>
        {
            // The single trigger: we just flipped to "needs you".
            if (@new != ActivityState.WaitingForInput) return;
            CaptureAndWrite(session);
        };
        _handlers[session.Id] = handler;
        session.OnActivityStateChanged += handler;
    }

    private void UnwireSession(Session session)
    {
        if (_handlers.TryRemove(session.Id, out var h))
            session.OnActivityStateChanged -= h;
        _cursors.TryRemove(session.Id, out _);
        _lastFlipAt.TryRemove(session.Id, out _);
    }

    private void CaptureAndWrite(Session session)
    {
        // Snapshot the cheap in-memory bits on the event thread, then write off-thread.
        var turnStart = _lastFlipAt.TryGetValue(session.Id, out var t) ? t : DateTime.MinValue;
        var now = DateTime.UtcNow;
        _lastFlipAt[session.Id] = now;

        var screenCells = session.SnapshotScreenColoredRows();
        var transcript = CaptureSinceCursor(session);
        EmitScreen(session, now);

        var record = new TurnReviewRecord
        {
            TsUtc = now,
            SessionId = session.Id.ToString(),
            SessionName = session.CustomName,
            Transcript = transcript,
            StatusColor = session.StatusColor,
            StatusReason = session.LastStatusReason,
            // Only count the Wingman's spoken briefing as "said this turn" if it was produced
            // after the turn started; otherwise it belongs to an earlier turn.
            WingmanSaid = session.CachedExplainAt is { } at && at >= turnStart ? session.CachedExplainText : null,
        };
        foreach (var styledRow in screenCells)
        {
            var rowSegments = new List<TurnReviewSegment>(styledRow.Count);
            foreach (var seg in styledRow)
                rowSegments.Add(new TurnReviewSegment { Text = seg.Text, Fg = seg.Fg, Bg = seg.Bg, Bold = seg.Bold });
            record.ScreenCells.Add(rowSegments);
        }
        foreach (var a in session.RecentWingmanActions)
        {
            if (a.At < turnStart) continue;
            record.WingmanActions.Add(new TurnReviewAction { At = a.At, Action = a.Action, Detail = a.Detail, Reason = a.Reason });
        }

        _ = Task.Run(() => TurnReviewLog.Write(record));
    }

    /// <summary>
    /// Hand the same turn-end moment to the screen sink, in the plain-rows-plus-flags shape a reader
    /// classifies (which is what the Gateway's screen-grid verb answers), together with the terminal's
    /// byte mark taken in the same operation. Never throws into the state-change event: the local turn
    /// review is the caller's contract here, and a sink that cannot send must not cost the record.
    /// </summary>
    private void EmitScreen(Session session, DateTime capturedAt)
    {
        if (_screenSink is null) return;
        try
        {
            var (rows, cursorRow, cursorCol, cursorVisible, isAlternateScreen, bufferBytes) =
                session.SnapshotLiveScreenWithBufferMark();
            _screenSink.Send(new TurnEndScreen
            {
                SessionId = session.Id.ToString(),
                CapturedAtUtc = capturedAt,
                Rows = rows,
                CursorRow = cursorRow,
                CursorCol = cursorCol,
                CursorVisible = cursorVisible,
                IsAlternateScreen = isAlternateScreen,
                // A session with a real terminal parser yields a fixed-height grid even when the screen is
                // blank; only a session with no parser yields an empty array, and that is UNREADABLE - the
                // same rule the screen-grid verb applies, stated the same way so the two cannot drift.
                HasGrid = rows.Length > 0,
                BufferBytes = bufferBytes,
                ActivityState = ActivityState.WaitingForInput.ToString(),
                Agent = session.AgentKind.ToString(),
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnReviewLogger] screen sink FAILED for session={session.Id}: {ex.Message}");
        }
    }

    /// <summary>Capture THIS session's terminal output since the stored cursor, ANSI-stripped,
    /// keeping the end when very long, and advance the cursor.</summary>
    private string CaptureSinceCursor(Session session, int maxChars = 16000)
    {
        var buffer = session.Buffer;
        if (buffer is null) return "";
        var since = _cursors.TryGetValue(session.Id, out var c) ? c : 0;
        var (bytes, newCursor) = buffer.GetWrittenSince(since);
        _cursors[session.Id] = newCursor;
        if (bytes.Length == 0) return "";
        var text = TerminalOutputParser.StripAnsi(Encoding.UTF8.GetString(bytes));
        return text.Length > maxChars ? text[^maxChars..] : text;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sessionManager.OnSessionCreated -= WireSession;
        _sessionManager.OnSessionRemoved -= UnwireSession;
        foreach (var s in _sessionManager.ListSessions())
            if (_handlers.TryRemove(s.Id, out var h))
                s.OnActivityStateChanged -= h;
    }
}
