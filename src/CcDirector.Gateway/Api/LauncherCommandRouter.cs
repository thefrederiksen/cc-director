using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Api;

/// <summary>
/// launcher-persistent-join: the ONE place a lifecycle command is pushed DOWN a launcher's persistent
/// stream. The machine relay routes go through <see cref="TrySendAsync"/> so the delivery decision is
/// uniform across verbs and cannot diverge. The launcher twin of <see cref="DirectorCommandRouter"/>.
///
/// The stream is the ONLY path to a launcher (remove-the-network-port mission, phase 6 - the launcher
/// listens on nothing). A null from the injected <paramref name="sendCommand"/> delegate therefore means
/// the command CANNOT BE DELIVERED right now - no hook wired, or no active connection for this
/// tenant+machine - and the caller turns that into a loud refusal
/// (<see cref="LauncherLifecycleRelay.RelayOutcomeKind.NotConnected"/>). It is never a cue to reach the
/// launcher another way; there is no other way. A non-null result - success OR a typed failure - is the
/// launcher's own answer and is authoritative.
/// </summary>
internal static class LauncherCommandRouter
{
    /// <summary>The signature of the "send a command down a launcher's stream" hook. The tenant scopes the
    /// connection lookup to the caller's own launcher (tenant, machine).</summary>
    public delegate Task<LauncherCommandResult?> SendLauncherCommandAsync(TenantId tenant, string machineName, LauncherCommand command, CancellationToken ct);

    /// <summary>
    /// Try to route a command down the CALLING TENANT's launcher stream. Returns the stream result, or null
    /// when the command could not be delivered (no hook, or the launcher is not stream-connected for this
    /// tenant+machine) - which the caller reports as a refusal, never routes around.
    /// </summary>
    public static async Task<LauncherCommandResult?> TrySendAsync(
        SendLauncherCommandAsync? sendCommand, TenantId tenant, string machineName, LauncherCommand command, CancellationToken ct)
    {
        if (sendCommand is null)
            return null;

        var result = await sendCommand(tenant, machineName, command, ct);
        FileLog.Write($"[LauncherCommandRouter] {command.Verb} tenant={tenant.Value} machine={machineName}: {(result is null ? "no stream connection - undeliverable" : $"stream status={result.Status}")}");
        return result;
    }
}
