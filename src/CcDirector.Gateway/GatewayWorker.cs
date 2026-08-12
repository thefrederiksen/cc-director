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
    /// THAT SECOND SENTENCE IS TRUE ONLY AFTER THE SITE HAS STARTED, and reading it as unconditional cost
    /// an outage on 12 August (#2585). During SITE STARTUP the platform makes no such distinction: its own
    /// log shows a container that exits and a container that never binds both reaching "Site container
    /// terminated during site startup" and then "Failed to start site. Revert by stopping site." - and the
    /// site stop tears down the healthy container serving beside it. Exiting is FASTER (103 seconds that
    /// day rather than the full 230-second probe timeout) but it is not SAFER, and this comment implied it
    /// was. The exit still belongs here; what does not belong is the belief that it removes the outage.
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
            // WHAT HAPPENS AFTERWARDS: the container exits, the platform restarts it, and if the cause is
            // still there it exits again. That restart loop is correct either way - the service recovers by
            // itself the moment the cause clears, instead of sitting dead until somebody notices - but its
            // RATE depends on which failure it is, and the two differ a lot:
            //
            // The line that separates them is whether the CONNECTION STRING PARSED, not what kind of
            // problem it is:
            //
            //  - anything that fails while OPENING OR MIGRATING the main store, once the connection string
            //    has parsed, goes through GatewayDatabase's retry window. Its catch takes every exception,
            //    so that is not only an unreachable or refusing server - a wrong password, a failed or
            //    missing migration, and a provider fault are all retried too. Those containers live at
            //    least the retry window before exiting, so restarts are paced by the window plus boot.
            //  - only a connection string that is BLANK or UNPARSEABLE, which throws above the loop, and
            //    any non-database startup failure, exit within seconds of boot. Those restart rapidly.
            //
            // So a SLOW loop does not mean the database is merely refusing connections, and reading it that
            // way sends someone with a wrong password or a broken migration off to check network
            // reachability. It means the string parsed and something after that kept failing; the Gateway
            // log's own open-attempt lines say which, and DescribeFailure carries the server's SqlState
            // when the server answered. A FAST loop means the string itself is unusable, or the failure was
            // never the database at all.
            //
            // Two earlier versions of this comment were wrong in this same place - first claiming the retry
            // window bounded every case, then claiming the slow case was reachability. Whether App Service
            // itself throttles repeated container restarts has NOT been checked, so nothing here relies on
            // it. In both cases the site is already down; what termination changes is that the platform can
            // recover it automatically instead of waiting out a live process.
            //
            // A THIRD THING THIS COMMENT USED TO GET WRONG, corrected here rather than left to be found a
            // fourth time: it ended "...and killing its healthy neighbour", implying that terminating SAVES
            // the healthy container. It does not. During SITE STARTUP the platform stops the site either
            // way - #2585's platform log shows an exiting container and a non-binding one both reaching
            // "Failed to start site. Revert by stopping site.", and that stop tears the healthy container
            // down. Terminating is FASTER, not safer. See MustTerminate above.
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
