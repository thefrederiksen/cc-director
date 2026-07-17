using CcDirector.Core.Configuration;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace CcDirector.Launcher;

/// <summary>
/// launcher-persistent-join: the launcher's outbound client for the Gateway's LauncherHub. It dials the
/// Gateway (never the other way), authenticates with the same token the HTTP registration uses, and keeps a
/// persistent stream open so the Gateway can PUSH a lifecycle command DOWN it - start/stop/restart the
/// installed Director, or a generic launch - instead of dialing this launcher's loopback REST API.
///
/// It is the launcher twin of the Director's <c>GatewayStreamClient</c>, but simpler: a launcher pushes no
/// session state, so on connect/reconnect it sends only <see cref="LauncherStreamHello"/>, then waits for
/// commands on the down-channel and executes them through the SAME <see cref="DirectorSupervisor"/> /
/// <see cref="LaunchService"/> actions the REST endpoints use.
///
/// It runs ALONGSIDE the existing <see cref="GatewayRegistrationClient"/> (which stays for registration and
/// heartbeat metadata). It is inert unless the config has a Gateway URL and <see cref="GatewayConfig.StreamMode"/>
/// is on, so a launcher with stream mode off behaves exactly as today and the Gateway relays over REST.
/// </summary>
public sealed class LauncherStreamClient : IAsyncDisposable
{
    private readonly GatewayConfig _config;
    private readonly int _port;
    private readonly string _version;
    private readonly DirectorSupervisor _supervisor;
    private readonly LaunchService _launchService;

    private HubConnection? _connection;
    private int _started;
    private volatile bool _disposed;

    /// <summary>Reconnect backoff between long-outage restart attempts once auto-reconnect has given up.</summary>
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    public LauncherStreamClient(GatewayConfig config, int port, string version, DirectorSupervisor supervisor, LaunchService launchService)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _port = port;
        _version = version ?? "";
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _launchService = launchService ?? throw new ArgumentNullException(nameof(launchService));
    }

    /// <summary>True when stream mode is enabled for a configured Gateway. When false, <see cref="Start"/> is a no-op.</summary>
    public bool IsEnabled => _config.IsEnabled && _config.StreamMode;

    /// <summary>Start dialing the Gateway. Idempotent; inert when <see cref="IsEnabled"/> is false.</summary>
    public void Start()
    {
        if (!IsEnabled) return;
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        FileLog.Write($"[LauncherStreamClient] Start: dialing {_config.Url}/launcher-stream for machine {Environment.MachineName}");
        _ = SuperviseAsync();
    }

    // Owns the connection for its whole life: build once, keep it connected, and re-Hello on every
    // (re)connection. Auto-reconnect handles transient drops fast; when it gives up (Closed), this loop
    // restarts the connection so a long Gateway outage self-heals without a launcher restart.
    private async Task SuperviseAsync()
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(_config.Url.TrimEnd('/') + "/launcher-stream", options =>
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
            .Build();

        _connection.Reconnecting += ex => { FileLog.Write($"[LauncherStreamClient] reconnecting: {ex?.Message}"); return Task.CompletedTask; };
        _connection.Reconnected += async _ => await SayHelloAsync();

        // The production down-channel. The Gateway invokes "Command" with a LauncherCommand and awaits the
        // LauncherCommandResult over the same connection (SignalR client results). This handler is a
        // boundary, so it catches: a dispatch fault becomes an Error result the Gateway can fall back on,
        // never a faulted hub invocation.
        _connection.On<LauncherCommand, LauncherCommandResult>("Command", cmd => DispatchAsync(cmd));

        while (!_disposed)
        {
            if (await TryConnectAsync())
            {
                await SayHelloAsync();
                await WaitUntilClosedAsync();      // returns when auto-reconnect has exhausted its attempts
            }
            if (_disposed) break;
            await Task.Delay(RestartDelay);        // long-outage restart
        }
    }

    // Execute one command through the same supervisor/launch actions the loopback REST endpoints use, so
    // the stream path and the REST relay path cannot drift. A boundary: any fault becomes an Error result.
    private async Task<LauncherCommandResult> DispatchAsync(LauncherCommand cmd)
    {
        try
        {
            if (cmd is null)
                return LauncherCommandResult.Fail(LauncherCommandStatus.BadRequest, "command is required");

            FileLog.Write($"[LauncherStreamClient] Command received: verb={cmd.Verb}, path={cmd.Path ?? "(none)"}, confirmProtected={cmd.ConfirmProtected}");
            switch (cmd.Verb)
            {
                case "director/start":
                    _supervisor.Start();
                    return LauncherCommandResult.Ok();

                case "director/stop":
                    await _supervisor.StopAsync();
                    return LauncherCommandResult.Ok();

                case "director/restart":
                    await _supervisor.RestartAsync();
                    return LauncherCommandResult.Ok();

                case "launch":
                    if (string.IsNullOrWhiteSpace(cmd.Path))
                        return LauncherCommandResult.Fail(LauncherCommandStatus.BadRequest, "path is required for launch");
                    _launchService.Launch(
                        new LaunchRequest { Path = cmd.Path!, Args = cmd.Args, Cwd = cmd.Cwd, Headless = cmd.Headless },
                        caller: "launcher-stream");
                    return LauncherCommandResult.Ok();

                default:
                    FileLog.Write($"[LauncherStreamClient] Command declined (unknown verb): {cmd.Verb}");
                    return LauncherCommandResult.Fail(LauncherCommandStatus.BadRequest, $"unknown verb: {cmd.Verb}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherStreamClient] Command FAILED: verb={cmd?.Verb}, error={ex.Message}");
            return LauncherCommandResult.Fail(LauncherCommandStatus.Error, ex.Message);
        }
    }

    private async Task<bool> TryConnectAsync()
    {
        if (_connection is null) return false;
        try
        {
            await _connection.StartAsync();
            FileLog.Write($"[LauncherStreamClient] connected to {_config.Url}");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherStreamClient] connect failed (will retry): {ex.Message}");
            return false;
        }
    }

    private async Task SayHelloAsync()
    {
        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected) return;
        try
        {
            await conn.InvokeAsync("Hello", new LauncherStreamHello
            {
                MachineName = Environment.MachineName,
                Port = _port,
                Version = _version,
            });
            FileLog.Write($"[LauncherStreamClient] Hello sent: machine={Environment.MachineName}, port={_port}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherStreamClient] Hello failed (auto-reconnect will retry): {ex.Message}");
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

    public async Task StopAsync()
    {
        _disposed = true;
        if (_connection is not null)
        {
            try { await _connection.StopAsync(); }
            catch (Exception ex) { FileLog.Write($"[LauncherStreamClient] StopAsync error: {ex.Message}"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_connection is not null)
        {
            try { await _connection.DisposeAsync(); }
            catch (Exception ex) { FileLog.Write($"[LauncherStreamClient] DisposeAsync error: {ex.Message}"); }
            _connection = null;
        }
    }
}
