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
/// conversation, so an unconfirmed write means those messages exist nowhere. A screen is not like that:
/// the local turn review still holds it, the next turn end sends a fresh one, and a reader that finds no
/// stored screen falls back to a live tunnel pull - which is precisely the behaviour it had before this
/// store existed. A miss costs a round trip, never a record.
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
        stream.PushScreen(new ScreenPush
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
        });
    }
}
