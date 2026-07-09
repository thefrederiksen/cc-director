using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

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

    private HubConnection? _connection;
    private long _sequence;
    private int _started;
    private volatile bool _disposed;

    /// <summary>Reconnect backoff between long-outage restart attempts once auto-reconnect has given up.</summary>
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    public GatewayStreamClient(GatewayConfig config, string directorId, string version, Func<List<SessionDto>> snapshot)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _directorId = string.IsNullOrWhiteSpace(directorId) ? throw new ArgumentException("directorId is required", nameof(directorId)) : directorId;
        _version = version ?? "";
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    /// <summary>True when stream mode is enabled for a configured Gateway. When false, <see cref="Start"/> is a no-op.</summary>
    public bool IsEnabled => _config.IsEnabled && _config.StreamMode;

    /// <summary>Start dialing the Gateway. Idempotent; inert when <see cref="IsEnabled"/> is false.</summary>
    public void Start()
    {
        if (!IsEnabled) return;
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        FileLog.Write($"[GatewayStreamClient] Start: dialing {_config.Url} for director {_directorId}");
        _ = SuperviseAsync();
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
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
            })
            .Build();

        _connection.Reconnecting += ex => { FileLog.Write($"[GatewayStreamClient] reconnecting: {ex?.Message}"); return Task.CompletedTask; };
        _connection.Reconnected += async _ => await ReseedAsync();

        // Issue #1176 (Phase 1b): the down-channel proof. The Gateway can call this and await the reply
        // over the same connection (SignalR client results), demonstrating request-both-ways on one
        // outbound-dialed stream. A synthetic proof, not a production command handler.
        _connection.On<string, string>("Ping", message => $"pong:{message}");

        while (!_disposed)
        {
            if (await TryConnectAsync())
            {
                await ReseedAsync();
                await WaitUntilClosedAsync();      // returns when auto-reconnect has exhausted its attempts
            }
            if (_disposed) break;
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
            await conn.InvokeAsync("Hello", new DirectorStreamHello { DirectorId = _directorId, Version = _version });
            await conn.InvokeAsync("PushSnapshot", seq, _snapshot().ToArray());
            FileLog.Write($"[GatewayStreamClient] reseeded full snapshot seq={seq}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayStreamClient] reseed failed (auto-reconnect will retry): {ex.Message}");
        }
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

    public async Task StopAsync()
    {
        _disposed = true;
        if (_connection is not null)
        {
            try { await _connection.StopAsync(); }
            catch (Exception ex) { FileLog.Write($"[GatewayStreamClient] StopAsync error: {ex.Message}"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_connection is not null)
        {
            try { await _connection.DisposeAsync(); }
            catch (Exception ex) { FileLog.Write($"[GatewayStreamClient] DisposeAsync error: {ex.Message}"); }
            _connection = null;
        }
    }
}
