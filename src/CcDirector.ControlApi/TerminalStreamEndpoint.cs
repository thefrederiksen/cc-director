using System.Net.WebSockets;
using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Maps <c>GET /sessions/{sid}/stream</c>: a WebSocket that streams a session's
/// raw PTY bytes to a browser-side xterm.js terminal.
///
/// This replaces the old "poll the server-rendered HTML grid" Raw view, which
/// snapshotted Claude Code's constantly-repainting TUI on a timer and stacked
/// half-drawn frames as ghost lines. Streaming the byte stream in order to a real
/// terminal emulator (xterm.js) means the client applies every cursor move and
/// repaint in sequence, so the screen is always coherent -- the same way the
/// desktop terminal control renders.
///
/// Wire protocol:
///   Server -> {"type":"size","cols":C,"rows":R}   (immediately, and again on every PTY resize)
///   Server -> &lt;binary snapshot frame&gt;            (ANSI that reconstructs the CURRENT screen)
///   Server -> &lt;binary frames&gt;                  (raw PTY bytes: live output from the snapshot on)
///   Server -> {"type":"closed","reason":"..."}     (session ended) then the socket closes
///   Client -> &lt;binary/text frames&gt;               (the user's keystrokes -> the PTY)
///
/// On attach the server sends a self-contained SNAPSHOT of the current screen (see
/// <see cref="Session.GetTerminalSnapshot"/>) rather than replaying a mid-stream slice of raw bytes:
/// a byte replay only reconstructs correctly from byte 0, so starting mid-stream tears the screen for
/// agents that repaint incrementally (Codex). The snapshot carries its own byte cursor, so live
/// output resumes with no gap or overlap.
///
/// The terminal is bidirectional: the client (xterm.js) sends the user's keystrokes as
/// frames and we forward every byte straight to the PTY via <c>Session.SendInput</c>, so
/// the terminal is fully interactive -- text, Enter, arrows, Ctrl+C, Esc, the
/// slash-command UI. The composer's POST /sessions/{sid}/prompt path still exists for
/// long or dictated messages. A close frame from the client ends the stream.
/// Localhost-only by default; the ControlApiHost auth middleware applies when enabled,
/// exactly like /dictate.
/// </summary>
internal static class TerminalStreamEndpoint
{
    public static void Map(IEndpointRouteBuilder app, SessionManager sessionManager)
    {
        // Vendored xterm.js assets (offline; no CDN -- the phone reaches the Director
        // over Tailscale and may have no path to a public CDN).
        app.MapGet("/xterm.js", () =>
            Results.Content(EmbeddedResources.Load("xterm.js"), "application/javascript; charset=utf-8"));
        app.MapGet("/xterm.css", () =>
            Results.Content(EmbeddedResources.Load("xterm.css"), "text/css; charset=utf-8"));
        app.MapGet("/xterm-addon-canvas.js", () =>
            Results.Content(EmbeddedResources.Load("xterm-addon-canvas.js"), "application/javascript; charset=utf-8"));

        app.MapGet("/sessions/{sid}/stream", async (string sid, HttpContext ctx) =>
        {
            FileLog.Write($"[TerminalStreamEndpoint] GET /sessions/{sid}/stream from {ctx.Connection.RemoteIpAddress}");

            if (!Guid.TryParse(sid, out var guid))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("invalid session id format");
                return;
            }
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("expected websocket upgrade");
                return;
            }

            // permessage-deflate: ANSI terminal output compresses 5-10x, which matters when
            // the client is another machine on the tailnet (possibly DERP-relayed). The
            // "Dangerous" prefix is about BREACH-style attacks mixing secrets with
            // attacker-controlled data in one compression context - not applicable on a
            // single-user tailnet streaming the user's own terminal.
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync(
                new WebSocketAcceptContext { DangerousEnableCompression = true });
            try
            {
                await StreamSessionAsync(ws, sessionManager, guid, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // Client navigated away or the server is shutting down. Normal.
            }
            catch (WebSocketException ex)
            {
                FileLog.Write($"[TerminalStreamEndpoint] sid={guid} socket dropped: {ex.Message}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TerminalStreamEndpoint] stream FAILED: sid={guid} {ex.Message}");
            }
        });
    }

    private static async Task StreamSessionAsync(WebSocket ws, SessionManager sessionManager, Guid guid, CancellationToken requestAborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var ct = cts.Token;

        // The client sends the user's keystrokes; we forward every byte to the PTY.
        // This loop also observes the close handshake / dropped connection: any close
        // or receive error cancels the send pump below.
        var receiveTask = ForwardClientInputAsync(ws, sessionManager, guid, cts);

        // GetWrittenSince(cursor) returns the retained history from the cursor on the first
        // call, then only the bytes appended since the previous call. One monotonic cursor
        // drives both the initial replay and the live tail -- never a snapshot, never a
        // frame boundary, so xterm renders a coherent screen.
        //
        // The initial cursor starts at most ReplayCapBytes behind the live tail rather than
        // at byte 0: replaying the full 2MB ring per attach made remote (laptop/phone)
        // attaches take seconds, and 256KB is still thousands of scrollback lines. Starting
        // mid-stream can tear the first reconstructed screen, but the attach nudge below
        // forces a full Claude repaint that heals it -- the cap and the nudge are a pair.
        long cursor;
        short lastCols;
        short lastRows;

        // Attach: send a self-contained SNAPSHOT of the current screen (the server's authoritative
        // PTY-sized parser serialized back to ANSI), NOT a mid-stream slice of raw bytes. A byte
        // replay from a fresh client terminal only reconstructs correctly from byte 0; starting
        // mid-stream tears the screen for agents that repaint incrementally (Codex). The snapshot is
        // correct regardless, and it carries its own byte cursor so live output resumes with no gap
        // or overlap. This replaces the old 256KB replay + resize-jiggle "nudge" heal entirely.
        {
            var session0 = sessionManager.GetSession(guid);
            if (session0 is null)
            {
                await SendJsonAsync(ws, new { type = "closed", reason = "session not found" }, ct);
                return;
            }
            var (snapshot, reflected, snapCols, snapRows) = session0.GetTerminalSnapshot();
            lastCols = (short)snapCols;
            lastRows = (short)snapRows;
            cursor = reflected;
            await SendJsonAsync(ws, new { type = "size", cols = snapCols, rows = snapRows }, ct);
            if (snapshot.Length > 0)
                await ws.SendAsync(snapshot, WebSocketMessageType.Binary, endOfMessage: true, ct);
            FileLog.Write($"[TerminalStreamEndpoint] attach snapshot: sid={guid}, grid={snapCols}x{snapRows}, snapshotBytes={snapshot.Length}, cursor={cursor}");
        }

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var session = sessionManager.GetSession(guid);
            if (session is null)
            {
                await SendJsonAsync(ws, new { type = "closed", reason = "session not found" }, ct);
                break;
            }

            // Report a LIVE PTY resize (the desktop pane changed size) so xterm re-sizes its grid.
            // The agent's repaint after the resize flows through as live bytes below.
            if (session.CurrentCols != lastCols || session.CurrentRows != lastRows)
            {
                lastCols = session.CurrentCols;
                lastRows = session.CurrentRows;
                await SendJsonAsync(ws, new { type = "size", cols = (int)lastCols, rows = (int)lastRows }, ct);
            }

            var buffer = session.Buffer;
            if (buffer is not null)
            {
                var (data, newCursor) = buffer.GetWrittenSince(cursor);
                if (data.Length > 0)
                {
                    await ws.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, ct);
                    cursor = newCursor;
                    continue; // drain at full speed while bytes are flowing before sleeping
                }
            }

            // Once a dead session's buffer is fully drained (no more new bytes above),
            // tell the client and end the stream.
            if (session.Status is SessionStatus.Exited or SessionStatus.Failed)
            {
                await SendJsonAsync(ws, new { type = "closed", reason = "session exited" }, ct);
                break;
            }

            await Task.Delay(40, ct);
        }

        cts.Cancel();
        await TryCloseAsync(ws);
        try { await receiveTask; } catch { /* receive loop already unwound */ }
    }

    // Forward the client's keystrokes to the PTY. Each WebSocket message (text frames from
    // xterm's onData, or binary) is accumulated until EndOfMessage, then written verbatim to
    // the session via SendInput - so control bytes (arrows, Ctrl+C, Esc) pass through intact.
    // A Close frame or a receive fault ends the loop and cancels the send pump.
    private static async Task ForwardClientInputAsync(WebSocket ws, SessionManager sessionManager, Guid guid, CancellationTokenSource cts)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        try
        {
            while (!cts.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                if (result.Count > 0)
                    message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                if (message.Length == 0)
                    continue;
                var bytes = message.ToArray();
                message.SetLength(0);

                sessionManager.GetSession(guid)?.SendInput(bytes);
            }
        }
        catch
        {
            // Receive faulting (client dropped, socket aborted) is the expected end-of-life
            // signal for this loop, not an error worth surfacing.
        }
        finally
        {
            cts.Cancel();
        }
    }

    private static async Task SendJsonAsync(WebSocket ws, object payload, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalStreamEndpoint] SendJsonAsync failed: {ex.Message}");
        }
    }

    private static async Task TryCloseAsync(WebSocket ws)
    {
        try
        {
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch
        {
            // Best effort -- the socket may already be gone.
        }
    }
}
