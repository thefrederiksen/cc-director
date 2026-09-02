using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Sends each turn-end terminal screen to the Gateway (the Terminal Rules mission,
/// <c>docs/missions/terminal-rules-2026-09-02/brief.md</c>) - the Director half of "the Director
/// captures, the Gateway stores".
///
/// Rides the Director's existing outbound tunnel (<see cref="GatewayStreamClient"/>), the same
/// authenticated connection the session snapshot goes up, so the tenant and the Director identity come
/// from the connection binding and can never be supplied by the payload.
///
/// UNLIKE <see cref="GatewayPromptSink"/>, THIS ONE IS FIRE-AND-FORGET, and the difference is
/// deliberate. The prompt sink must be acknowledged because the Director keeps no copy of the
/// conversation, so an unconfirmed write means those messages exist nowhere.
///
/// WHAT A MISS ACTUALLY COSTS, corrected (inspection 01, finding 5). This comment used to say a miss cost
/// "a round trip, never a record", on the reasoning that the next turn sends a fresh screen. It does not
/// send THIS one. There is no outbox, no sequence and no reconnect replay for screens, so a turn whose
/// screen could not be sent has no row in the Gateway's history and never will - the next turn sends the
/// NEXT turn's screen. The Director's own local turn-review file still holds it and nothing replays that
/// file into the store. If the machine then goes offline, the history read has no fallback for that turn
/// at all.
///
/// That hole is accepted for now and NAMED rather than described away: a durable outbox is a mechanism
/// that would owe its own proofs. What was fixed is that the loss is no longer silent - every drop is
/// logged with its session and reason and counted by
/// <see cref="GatewayStreamClient.ScreenPushesDropped"/>, so a Director that is dropping every screen no
/// longer looks exactly like one that captured none.
/// </summary>
public sealed class GatewayScreenSink : ITurnEndScreenSink
{
    private readonly Func<GatewayStreamClient?> _stream;

    /// <param name="stream">Resolves the Director's CURRENT outbound tunnel, or null when this Director
    /// has none configured. Resolved per send, not captured: the Director rebuilds its client when the
    /// Gateway is reconfigured, and a captured instance would go stale and silently stop sending.</param>
    public GatewayScreenSink(Func<GatewayStreamClient?> stream) => _stream = stream;

    public void Send(TurnEndScreen screen)
    {
        if (screen is null) return;
        var stream = _stream();
        if (stream is null)
        {
            FileLog.Write($"[GatewayScreenSink] No Gateway tunnel - screen for session={screen.SessionId} NOT pushed");
            return;
        }
        // Logged on the SUCCESS path too, not only on failure. A push that logs nothing when it works and
        // nothing when it is dropped is indistinguishable from a capture that never fired - which is exactly
        // what happened while proving this path end to end: every log on both sides was silent and the
        // silence carried no information at all.
        FileLog.Write($"[GatewayScreenSink] pushing screen for session={screen.SessionId} "
            + $"capturedAt={screen.CapturedAtUtc:O} rows={screen.Rows.Length} hasGrid={screen.HasGrid} "
            + $"bufferBytes={screen.BufferBytes}");
        stream.PushScreen(ToPush(screen));
    }

    /// <summary>
    /// The captured screen as the push the Gateway stores. A NAMED FUNCTION rather than an object
    /// initialiser inside <see cref="Send"/>, and that is not a style preference - it is inspection 01's
    /// finding 4. Every screen's rows were replaced here with one constant and the entire Gateway unit
    /// project stayed green, because nothing anywhere compared what came out of this mapping with what
    /// went into it: the store's own tests seed the store by hand, and the end-to-end rig only checked
    /// that SOMETHING nonblank arrived. Pulling the mapping out gives that comparison somewhere to live -
    /// see <c>GatewayScreenSinkMappingTests</c>, which asserts field-for-field equality and turns that
    /// exact mutation red in the default gate.
    ///
    /// It carries the screen ACROSS and changes nothing. Any transformation added here is a place where
    /// the terminal's content can differ from the stored row, which is the whole thing being guarded.
    /// </summary>
    internal static ScreenPush ToPush(TurnEndScreen screen) => new()
    {
        SessionId = screen.SessionId,
        CapturedAtUtc = screen.CapturedAtUtc,
        Rows = screen.Rows.ToList(),
        CursorRow = screen.CursorRow,
        CursorCol = screen.CursorCol,
        CursorVisible = screen.CursorVisible,
        IsAlternateScreen = screen.IsAlternateScreen,
        HasGrid = screen.HasGrid,
        BufferBytes = screen.BufferBytes,
        ActivityState = screen.ActivityState,
        Agent = screen.Agent,
    };
}
