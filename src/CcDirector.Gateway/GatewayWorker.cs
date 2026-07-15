using CcDirector.Core.Utilities;
using Microsoft.Extensions.Hosting;

namespace CcDirector.Gateway;

/// <summary>
/// Hosts the Gateway inside the generic host for the dev console loop (<c>dotnet run</c>, Ctrl+C to stop).
///
/// It drives the SAME <see cref="GatewayService"/> the shipped tray app drives - not a parallel,
/// slightly-different copy of the lifecycle, which is what this used to be. That matters beyond tidiness:
/// the tray app is a shim that must be deletable, and the only way to KNOW it is deletable is for a second
/// host with no user interface at all to run the identical service. This is that host.
///
/// The differences from the tray app are exactly the two a host is allowed to have: it does not render
/// state (there is nothing to render to), and it ends the process with Environment.Exit rather than
/// Avalonia's Shutdown. Self-update and autostart are off - those belong to a managed install, never to
/// the dev loop.
/// </summary>
public sealed class GatewayWorker : BackgroundService
{
    private readonly GatewayService _service;

    public GatewayWorker(int port)
    {
        _service = new GatewayService(new GatewayServiceOptions
        {
            Port = port,
            Managed = false,
            RegisterAutostart = false,
            ModeLabel = "dev",
        });
        // /shutdown support, so the self-update flow is testable against a dev console gateway. The
        // service has already stopped the Gateway by the time this fires; ending the process is all that
        // is left, and only a host knows how.
        _service.ShutdownRequested += () => Environment.Exit(0);
    }

    /// <summary>The running Gateway service (exposed so a host or a test can read its state).</summary>
    public GatewayService Service => _service;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        FileLog.Write($"[GatewayWorker] ExecuteAsync: port={_service.Port}");

        await _service.StartAsync();
        FileLog.Write($"[GatewayWorker] {_service.StatusText}");

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
            await _service.StopAsync();
        }
        finally
        {
            await base.StopAsync(cancellationToken);
        }
    }

    public override void Dispose()
    {
        _service.Dispose();
        base.Dispose();
    }
}
