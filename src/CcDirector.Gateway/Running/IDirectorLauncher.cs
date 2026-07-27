using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Running;

/// <summary>
/// Asks a TENANT'S launcher on a machine to start a Director (epic #479, #503), behind an interface so the
/// target resolver is unit-testable. Production is <see cref="RelayDirectorLauncher"/>.
/// </summary>
public interface IDirectorLauncher
{
    /// <summary>
    /// Request a Director start on <paramref name="tenant"/>'s launcher for <paramref name="machine"/>.
    /// Returns true if the launcher accepted the request (the Director then registers asynchronously).
    ///
    /// The tenant is an ARGUMENT and not ambient state, because the caller has already resolved it and the
    /// whole point of this seam is that the resolved tenant reaches the launcher intact.
    /// </summary>
    Task<bool> StartAsync(TenantId tenant, string machine, CancellationToken ct);
}

/// <summary>
/// Production launcher: drives the shipped launcher relay IN-PROCESS, through the same
/// <see cref="LauncherLifecycleRelay"/> the HTTP route uses, so the auto-launch path and the operator's
/// explicit POST /machines/{machine}/director/start cannot drift.
///
/// IT USED TO DIAL THE GATEWAY'S OWN ROUTE OVER LOOPBACK, AND THAT LOST THE TENANT. Reusing the shipped path
/// was right; reusing it by making a fresh HTTP request to ourselves was not. A loopback request carries no
/// device key, so on the hosted Gateway the inner call resolved to no tenant and refused itself with 403 -
/// which meant "start a session on a machine whose Director is not running" could never work for a hosted
/// tenant. It failed closed rather than leaking, but had it resolved to anything at all it would have
/// resolved to a tenant unrelated to the one that asked. A hop that cannot carry identity is the wrong place
/// to share code; the sharing moved down to the relay itself, where the tenant is a parameter.
/// </summary>
public sealed class RelayDirectorLauncher : IDirectorLauncher
{
    private readonly LauncherRegistry _launchers;
    private readonly LauncherCommandRouter.SendLauncherCommandAsync? _sendLauncherCommand;

    /// <param name="launchers">The tenant-keyed launcher registry - the REST fallback arm's source of address,
    /// port and bearer token, resolved as (tenant, machine).</param>
    /// <param name="sendLauncherCommand">The persistent-stream send hook, or null when stream mode is off.</param>
    internal RelayDirectorLauncher(
        LauncherRegistry launchers,
        LauncherCommandRouter.SendLauncherCommandAsync? sendLauncherCommand)
    {
        _launchers = launchers ?? throw new ArgumentNullException(nameof(launchers));
        _sendLauncherCommand = sendLauncherCommand;
    }

    public async Task<bool> StartAsync(TenantId tenant, string machine, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(machine))
            return false;

        var outcome = await LauncherLifecycleRelay.SendDirectorVerbAsync(
            tenant, machine, verb: "start", exePath: null, confirmProtected: false,
            _launchers, _sendLauncherCommand, ct);

        FileLog.Write($"[RelayDirectorLauncher] start tenant={tenant.Value} machine={machine} -> {outcome.Kind} (accepted={outcome.Accepted})");
        return outcome.Accepted;
    }
}
