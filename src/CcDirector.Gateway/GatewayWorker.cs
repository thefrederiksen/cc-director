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

    /// <summary>
    /// Exit code for a Gateway that could not start. Non-zero on purpose: the container platform decides
    /// what to do from the EXIT CODE, and zero would read as a clean, intentional shutdown.
    /// </summary>
    public const int StartFailureExitCode = 1;

    /// <summary>
    /// Whether a Gateway that has finished <see cref="GatewayService.StartAsync"/> in this state must end
    /// the PROCESS rather than stay resident.
    ///
    /// This is the fix for the 2 August outage (issue #2383), and it is not in the database code at all.
    /// <see cref="GatewayService.StartAsync"/> catches every startup exception, logs it, and does not
    /// rethrow; this worker then waited on <c>Task.Delay(Timeout.Infinite)</c>. So a Gateway whose database
    /// could not be opened stayed ALIVE with nothing listening on its port. From the platform's outside
    /// view that is indistinguishable from an application still starting, so it waited out the full
    /// 230-second container start limit, concluded no listening port existed, and STOPPED THE SITE - which
    /// also tore down the healthy container that had been serving beside it. The 38.5 seconds of downtime
    /// was that stop, not the swap.
    ///
    /// A container that EXITS is restarted. A container that is alive and silent is waited out and then
    /// takes the healthy one with it. So the failed start must end the process.
    ///
    /// Pure and internal so the policy is unit-testable without starting a Gateway or ending a test run.
    /// </summary>
    internal static bool MustTerminate(GatewayServiceState state) => state == GatewayServiceState.Failed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        FileLog.Write($"[GatewayWorker] ExecuteAsync: port={_service.Port}");

        await _service.StartAsync();
        FileLog.Write($"[GatewayWorker] {_service.StatusText}");

        if (MustTerminate(_service.State))
        {
            // Environment.Exit, not a thrown exception: a BackgroundService that faults leaves the generic
            // host deciding whether to stop, and a faulted task inside a process that stays up looks
            // identical from outside to the silent hang this replaces. The process must actually END.
            //
            // Ending the process is already this host's established way of stopping (see the constructor's
            // ShutdownRequested handler); this is the same act with a failure code.
            //
            // WHAT HAPPENS DURING A LONG DATABASE OUTAGE: the container exits, the platform restarts it, it
            // retries the database for the bounded window in GatewayDatabase and exits again. That is a
            // restart loop, and it is the correct behaviour - the service recovers by itself the moment the
            // database answers, instead of sitting dead until somebody notices. It cannot spin: each attempt
            // spends the full retry window before giving up, so a container lives at least that long, which
            // bounds restarts to roughly one per retry-window-plus-boot rather than a tight loop.
            FileLog.Write($"[GatewayWorker] Gateway FAILED to start ({_service.StatusText}); ending the process "
                + $"with exit code {StartFailureExitCode} so the platform restarts this container instead of "
                + "waiting out a live process that will never bind a port.");
            Environment.Exit(StartFailureExitCode);
        }

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
