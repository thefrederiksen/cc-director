using CcDirector.Core.Configuration;
using CcDirector.Core.Network;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection; // AddMessagePackProtocol (client)

namespace CcDirector.ControlApi;

/// <summary>
/// Issue #1176 (Phase 1a): the Director's outbound client for the Gateway's DirectorHub. It dials the
/// Gateway (never the other way), authenticates with the same token the HTTP client uses, and pushes this
/// Director's session state UP the stream:
///
///   - on connect AND on every reconnect it sends <c>Hello</c> then a full <c>PushSnapshot</c> (the new
///     connection makes the snapshot authoritative at the Gateway, so a Director restart reseeds cleanly);
///   - <see cref="NotifyDelta"/> pushes one changed session; <see cref="NotifyRemove"/> pushes a removal.
///
/// It runs ALONGSIDE the existing <see cref="GatewayClient"/> in Phase 1a (additive): the heartbeat/
/// doorbell stay as the reconcile floor. It is inert unless the config has a Gateway URL and
/// <see cref="GatewayConfig.StreamMode"/> is on, so a Director with stream mode off behaves exactly as today.
///
/// Sends are best-effort and fire-and-forget: a dropped delta is harmless because the next snapshot (on
/// reconnect) re-establishes the full truth, mirroring the doorbell/heartbeat resilience model.
/// </summary>
public sealed class GatewayStreamClient : IAsyncDisposable
{
    private readonly GatewayConfig _config;
    private readonly string _directorId;
    private readonly string _version;
    private readonly Func<List<SessionDto>> _snapshot;

    /// <summary>
    /// The repository/worktree snapshot provider (issue devthrottle_internal#510, phase C). Null in
    /// older callers and tests: repo pushes are then simply skipped. Pushed as full snapshots only -
    /// repositories change slowly, so there is no delta path.
    /// </summary>
    private readonly Func<List<RepoStatusDto>>? _repoSnapshot;
    private readonly Func<DirectorCommand, Task<DirectorCommandResult>>? _commandDispatcher;
    private readonly TimeSpan _rePushInterval;

    /// <summary>
    /// Gateway Cleanup mission (tunnel-only): the connectivity indicator. The tunnel IS the Gateway
    /// connection now, so its up/down state IS the desktop's green/yellow. Null in tests/older callers.
    /// </summary>
    private readonly GatewayConnectionMonitor? _monitor;

    /// <summary>
    /// Gateway Cleanup mission, Phase 0 (up-stream): the handler for the four connection-bound stream verbs.
    /// Null when no <see cref="SessionManager"/> was supplied (older callers and tests that never drive a
    /// stream verb); in that case a stream verb returns a typed Error, exactly as an absent dispatcher does.
    /// </summary>
    private readonly DirectorUpStreamHandler? _upStreamHandler;

    // Gateway Cleanup mission (tunnel-only): the Hello now carries this Director's identity so the Gateway
    // registers it from the stream (HTTP register is gone). Captured once at construction.
    private readonly DateTime _startedAt = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();

    private HubConnection? _connection;
    private long _sequence;
    private int _started;
    private int _rePushInFlight;
    private Timer? _rePushTimer;
    private volatile bool _disposed;

    /// <summary>Reconnect backoff between long-outage restart attempts once auto-reconnect has given up.</summary>
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    /// <param name="commandDispatcher">
    /// Issue #1177 (Phase 1): handler for commands the Gateway sends DOWN this stream. When null (or in
    /// Phase 1a callers that pre-date it), the Director declines any command with an Error result; the
    /// Gateway then falls back to its HTTP command path, so behaviour is unchanged.
    /// </param>
    /// <param name="rePushInterval">
    /// Issue #1177 (Phase 4a): how often, while connected, the Director re-pushes its full snapshot so a
    /// QUIET session's pushed cache never ages past the Gateway's stale window. Null uses half the default
    /// stale window (comfortably under it). A test seam; production passes null.
    /// </param>
    /// <param name="sessionManager">
    /// Issue: Gateway Cleanup mission, Phase 0 (up-stream). When supplied, enables the four connection-bound
    /// stream verbs (open-terminal-stream / read-file / screenshot-file / close-stream) by building the
    /// up-stream handler, whose producer sends frames up this same connection. Null (older callers, tests)
    /// leaves stream verbs declined with a typed Error, so behaviour is unchanged for them.
    /// </param>
    public GatewayStreamClient(GatewayConfig config, string directorId, string version, Func<List<SessionDto>> snapshot,
        Func<DirectorCommand, Task<DirectorCommandResult>>? commandDispatcher = null,
        TimeSpan? rePushInterval = null,
        SessionManager? sessionManager = null,
        GatewayConnectionMonitor? monitor = null,
        Func<List<RepoStatusDto>>? repoSnapshot = null)
    {
        _repoSnapshot = repoSnapshot;
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _directorId = string.IsNullOrWhiteSpace(directorId) ? throw new ArgumentException("directorId is required", nameof(directorId)) : directorId;
        _version = version ?? "";
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _commandDispatcher = commandDispatcher;
        _monitor = monitor;
        _rePushInterval = rePushInterval ?? TimeSpan.FromSeconds(GatewayConfig.DefaultStreamStaleAfterSeconds / 2.0);
        _upStreamHandler = sessionManager is null
            ? null
            : new DirectorUpStreamHandler(sessionManager, (streamId, frames) => _connection!.SendAsync("StreamUp", streamId, frames));
    }

    /// <summary>
    /// True when a Gateway is configured. Gateway Cleanup mission (tunnel-only): the streamMode gate is
    /// GONE - the tunnel is the ONLY Gateway connection, so a configured Director always dials it. When
    /// no gateway.url is set (local-only) this is false and <see cref="Start"/> is a no-op.
    /// </summary>
    public bool IsEnabled => _config.IsEnabled;

    /// <summary>Start dialing the Gateway. Idempotent; inert when <see cref="IsEnabled"/> is false.</summary>
    public void Start()
    {
        if (!IsEnabled) return;
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        FileLog.Write($"[GatewayStreamClient] Start: dialing {_config.Url} for director {_directorId}");
        _ = SuperviseAsync();

        // Issue #1177 (Phase 4a): keep the Gateway's pushed cache fresh for a QUIET session. A portless
        // (remotely-unreachable) Director has no HTTP pull floor, so once the last push ages past the
        // Gateway's stale window its sessions vanish from the roster and can no longer be located. A periodic
        // full re-push - comfortably under that window - keeps TryGetFresh/TryLocate fresh. Best-effort, only
        // while connected. Armed only here (inside the IsEnabled guard), so it is inert when stream mode off.
        _rePushTimer = new Timer(_ => RePushTick(), null, _rePushInterval, _rePushInterval);
    }

    // Timer callback (a boundary): re-push the full snapshot so a quiet session's pushed cache stays fresh.
    // Skips when disposed, when disconnected, or when a prior re-push is still in flight (no overlap).
    private void RePushTick()
    {
        if (_disposed) return;
        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected) return;
        if (Interlocked.Exchange(ref _rePushInFlight, 1) == 1) return;
        _ = RePushAsync();
    }

    private async Task RePushAsync()
    {
        try
        {
            // ReseedAsync sends Hello + a full PushSnapshot with an incrementing sequence (the same path used
            // on connect/reconnect) and already swallows its own send faults, so a re-push is best-effort.
            await ReseedAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _rePushInFlight, 0);
        }
    }

    // Owns the connection for its whole life: build once, keep it connected, and reseed on every
    // (re)connection. Auto-reconnect handles transient drops fast; when it gives up (Closed), this loop
    // restarts the connection so a long Gateway outage self-heals without a Director restart.
    private async Task SuperviseAsync()
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(_config.Url.TrimEnd('/') + "/director-stream", options =>
            {
                var token = _config.Token;
                if (!string.IsNullOrEmpty(token))
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                // Local-name-friendly dialing (see GatewayHttp): a gateway named soren_north.local
                // resolves to a link-local IPv6 address first, and the default connect hangs on it.
                // The handler covers negotiate and the fallback transports; the websocket factory
                // covers the websocket itself, which dials outside that handler by default.
                options.HttpMessageHandlerFactory = _ => GatewayHttp.Handler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                    await GatewayHttp.ConnectWebSocketAsync(context.Uri, token, cancellationToken);
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
            })
            // Gateway Cleanup mission: MessagePack (binary) so a full-cap 48KB up-stream byte[] frame stays
            // ~48KB on the wire. Under JSON it base64-inflates to ~64KB and exceeds the hub's receive limit,
            // dropping the tunnel on any file read >48KB or a terminal burst. The hub also speaks JSON, so this
            // is safe to roll out per-Director.
            .AddMessagePackProtocol()
            .Build();

        _connection.Reconnecting += ex =>
        {
            FileLog.Write($"[GatewayStreamClient] reconnecting: {ex?.Message}");
            // Gateway Cleanup Phase 0 (Architect ruling A): the tunnel dropped, so no frame can reach the
            // Gateway - tear down every live up-stream instead of leaving a producer sending into a dead socket.
            _upStreamHandler?.CancelAll();
            _monitor?.MarkTunnelConnecting();
            return Task.CompletedTask;
        };
        _connection.Reconnected += async _ => { await ReseedAsync(); _monitor?.MarkTunnelConnected(); };
        // Also tear streams down on a full close (auto-reconnect gave up); the supervise loop then re-dials.
        _connection.Closed += _ => { _upStreamHandler?.CancelAll(); _monitor?.MarkTunnelConnecting(); return Task.CompletedTask; };

        // Issue #1176 (Phase 1b): the down-channel proof. The Gateway can call this and await the reply
        // over the same connection (SignalR client results), demonstrating request-both-ways on one
        // outbound-dialed stream. A synthetic proof, not a production command handler.
        _connection.On<string, string>("Ping", message => $"pong:{message}");

        // Issue #1177 (Phase 1): the production down-channel. The Gateway invokes "Command" with a
        // DirectorCommand and awaits the DirectorCommandResult over the same connection (SignalR client
        // results). This handler is a boundary, so it catches: a dispatcher fault becomes an Error result
        // the Gateway can fall back on, never a faulted hub invocation.
        _connection.On<DirectorCommand, DirectorCommandResult>("Command", async cmd =>
        {
            try
            {
                if (cmd is null)
                    return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "command is required");

                // Gateway Cleanup Phase 0 (Architect ruling A): the four connection-bound stream verbs branch
                // here - they need this live connection and the per-stream cancellation registry, so they are
                // NOT in the unary verb-to-area dictionary. Everything else forwards to the unary dispatcher.
                if (DirectorUpStreamHandler.IsStreamVerb(cmd.Verb))
                {
                    if (_upStreamHandler is null)
                    {
                        FileLog.Write($"[GatewayStreamClient] stream verb declined (no up-stream handler): verb={cmd.Verb}, cmdId={cmd.CommandId}");
                        return DirectorCommandResult.Fail(DirectorCommandStatus.Error, "director has no up-stream handler");
                    }
                    FileLog.Write($"[GatewayStreamClient] stream verb received: verb={cmd.Verb}, sid={cmd.SessionId}, cmdId={cmd.CommandId}");
                    var streamResult = _upStreamHandler.Handle(cmd);
                    streamResult.CommandId = cmd.CommandId;
                    return streamResult;
                }

                if (_commandDispatcher is null)
                {
                    FileLog.Write($"[GatewayStreamClient] Command declined (no dispatcher): verb={cmd.Verb}, cmdId={cmd.CommandId}");
                    return DirectorCommandResult.Fail(DirectorCommandStatus.Error, "director has no command dispatcher");
                }

                FileLog.Write($"[GatewayStreamClient] Command received: verb={cmd.Verb}, sid={cmd.SessionId}, cmdId={cmd.CommandId}");
                return await _commandDispatcher(cmd);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayStreamClient] Command FAILED: verb={cmd?.Verb}, cmdId={cmd?.CommandId}, error={ex.Message}");
                return DirectorCommandResult.Fail(DirectorCommandStatus.Error, ex.Message);
            }
        });

        while (!_disposed)
        {
            if (await TryConnectAsync())
            {
                await ReseedAsync();
                _monitor?.MarkTunnelConnected();   // tunnel up = the two-way connection is proven (green)
                await WaitUntilClosedAsync();      // returns when auto-reconnect has exhausted its attempts
            }
            if (_disposed) break;
            _monitor?.MarkTunnelConnecting();      // dropped / dialing again (yellow) until the next connect
            await Task.Delay(RestartDelay);        // long-outage restart
        }
    }

    private async Task<bool> TryConnectAsync()
    {
        if (_connection is null) return false;
        try
        {
            await _connection.StartAsync();
            FileLog.Write($"[GatewayStreamClient] connected to {_config.Url}");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayStreamClient] connect failed (will retry): {ex.Message}");
            return false;
        }
    }

    private async Task WaitUntilClosedAsync()
    {
        if (_connection is null) return;
        var closed = new TaskCompletionSource();
        Task OnClosed(Exception? _) { closed.TrySetResult(); return Task.CompletedTask; }
        _connection.Closed += OnClosed;
        try
        {
            if (_connection.State == HubConnectionState.Disconnected) return;
            await closed.Task;
        }
        finally
        {
            _connection.Closed -= OnClosed;
        }
    }

    private async Task ReseedAsync()
    {
        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected) return;
        try
        {
            var seq = Interlocked.Increment(ref _sequence);
            await conn.InvokeAsync("Hello", new DirectorStreamHello
            {
                DirectorId = _directorId,
                Version = _version,
                MachineName = Environment.MachineName,
                User = Environment.UserName,
                Pid = Environment.ProcessId,
                StartedAt = _startedAt,
            });
            await conn.InvokeAsync("PushSnapshot", seq, _snapshot().ToArray());
            FileLog.Write($"[GatewayStreamClient] reseeded full snapshot seq={seq}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayStreamClient] reseed failed (auto-reconnect will retry): {ex.Message}");
        }

        // The repository snapshot rides the same reseed cadence, in its OWN try/catch: an old Gateway
        // without the PushRepoSnapshot hub method throws a HubException here, and that must never take
        // the session reseed down with it (best-effort, no capability negotiation - see phase C notes).
        if (_repoSnapshot != null && conn.State == HubConnectionState.Connected)
        {
            try
            {
                var repoSeq = Interlocked.Increment(ref _sequence);
                await conn.InvokeAsync("PushRepoSnapshot", repoSeq, _repoSnapshot().ToArray());
                FileLog.Write($"[GatewayStreamClient] reseeded repository snapshot seq={repoSeq}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayStreamClient] repository reseed skipped (older Gateway?): {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Push the current full repository snapshot. Fire-and-forget; callers debounce. A drop is
    /// reconciled by the next reseed/re-push tick.
    /// </summary>
    public void NotifyRepoSnapshot()
    {
        if (_repoSnapshot is null) return;
        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected) return;
        var seq = Interlocked.Increment(ref _sequence);
        _ = SendAsync(() => conn.InvokeAsync("PushRepoSnapshot", seq, _repoSnapshot().ToArray()), "PushRepoSnapshot");
    }

    /// <summary>Push one changed session. Fire-and-forget; a drop is reconciled by the next snapshot.</summary>
    public void NotifyDelta(SessionDto session)
    {
        if (session is null || string.IsNullOrEmpty(session.SessionId)) return;
        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected) return;
        var seq = Interlocked.Increment(ref _sequence);
        _ = SendAsync(() => conn.InvokeAsync("PushDelta", seq, session), "PushDelta");
    }

    /// <summary>Push a session removal. Fire-and-forget; a drop is reconciled by the next snapshot.</summary>
    public void NotifyRemove(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected) return;
        var seq = Interlocked.Increment(ref _sequence);
        _ = SendAsync(() => conn.InvokeAsync("RemoveSession", seq, sessionId), "RemoveSession");
    }

    private static async Task SendAsync(Func<Task> send, string what)
    {
        try { await send(); }
        catch (Exception ex) { FileLog.Write($"[GatewayStreamClient] {what} dropped (mid-reconnect?): {ex.Message}"); }
    }

    /// <summary>Force the outbound tunnel to bounce: stop the current connection so the supervise
    /// loop re-dials (within RestartDelay). Idempotent; inert when the tunnel is disabled. This is the
    /// Director floor's POST /reconnect capability (Gateway Cleanup mission).</summary>
    public async Task ReconnectAsync()
    {
        if (!IsEnabled) return;
        var conn = _connection;
        if (conn is null) return;
        FileLog.Write("[GatewayStreamClient] ReconnectAsync: bouncing tunnel on request");
        try { await conn.StopAsync(); }
        catch (Exception ex) { FileLog.Write($"[GatewayStreamClient] ReconnectAsync stop error: {ex.Message}"); }
    }

    public async Task StopAsync()
    {
        _disposed = true;
        if (_rePushTimer is not null)
        {
            await _rePushTimer.DisposeAsync();
            _rePushTimer = null;
        }
        if (_connection is not null)
        {
            try { await _connection.StopAsync(); }
            catch (Exception ex) { FileLog.Write($"[GatewayStreamClient] StopAsync error: {ex.Message}"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_rePushTimer is not null)
        {
            await _rePushTimer.DisposeAsync();
            _rePushTimer = null;
        }
        if (_connection is not null)
        {
            try { await _connection.DisposeAsync(); }
            catch (Exception ex) { FileLog.Write($"[GatewayStreamClient] DisposeAsync error: {ex.Message}"); }
            _connection = null;
        }
    }
}
