using CcDirector.Core.Utilities;
using Microsoft.Extensions.Hosting;

namespace CcDirector.Gateway;

/// <summary>
/// Hosts the Gateway's Kestrel host inside the generic host for the DEV console loop
/// (<c>dotnet run</c>, Ctrl+C to stop). The shipped Gateway is the tray app
/// (CcDirector.GatewayApp), which owns self-update in managed mode; this host deliberately has
/// neither. The React Cockpit is served in-process by <see cref="GatewayHost"/> - there is no
/// separate Cockpit process to supervise (issue #979 retired the Blazor Server Cockpit).
/// </summary>
public sealed class GatewayWorker : BackgroundService
{
    private readonly int _port;
    private GatewayHost? _host;

    public GatewayWorker(int port)
    {
        _port = port;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        FileLog.Write($"[GatewayWorker] ExecuteAsync: port={_port}");

        _host = new GatewayHost(_port);
        // /shutdown support so the self-update flow is testable against a dev console gateway.
        _host.OnShutdownRequested = () =>
        {
            FileLog.Write("[GatewayWorker] shutdown requested via /shutdown");
            _ = StopAsync(CancellationToken.None).ContinueWith(_ => Environment.Exit(0));
        };
        await _host.StartAsync();

        FileLog.Write($"[GatewayWorker] running on http://127.0.0.1:{_host.Port}");

        // Stay alive until the host signals shutdown (Ctrl+C or ProcessExit).
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        FileLog.Write("[GatewayWorker] StopAsync");
        try
        {
            if (_host is not null)
                await _host.StopAsync();
        }
        finally
        {
            await base.StopAsync(cancellationToken);
        }
    }
}
