using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Api;

/// <summary>
/// launcher-persistent-join: the ONE place the Gateway decides "push this lifecycle command DOWN the
/// launcher's persistent stream, or fall back to the HTTP relay". The machine relay routes through
/// <see cref="TrySendAsync"/> so the decision is uniform across verbs and cannot diverge. The launcher twin
/// of <see cref="DirectorCommandRouter"/>.
///
/// The decision itself lives in the injected <paramref name="sendCommand"/> delegate (the Gateway host
/// passes <c>GatewayHost.SendLauncherCommandAsync</c> when stream mode is ON, and null when it is off). That
/// delegate returns null when the launcher has no active stream connection, so a null return here means "no
/// stream - use the HTTP relay" for BOTH the flag-off and the flag-on-but-offline cases. A non-null result -
/// success OR a typed failure - is authoritative and the endpoint must not also call the HTTP relay.
/// </summary>
internal static class LauncherCommandRouter
{
    /// <summary>The signature of the "send a command down a launcher's stream" hook, non-null only when stream
    /// mode is on. The tenant scopes the connection lookup to the caller's own launcher (tenant, machine).</summary>
    public delegate Task<LauncherCommandResult?> SendLauncherCommandAsync(TenantId tenant, string machineName, LauncherCommand command, CancellationToken ct);

    /// <summary>
    /// Try to route a command down the CALLING TENANT's launcher stream. Returns the stream result, or null to
    /// signal the caller to fall back to its existing HTTP relay (stream mode off, or the launcher is not
    /// stream-connected for this tenant+machine).
    /// </summary>
    public static async Task<LauncherCommandResult?> TrySendAsync(
        SendLauncherCommandAsync? sendCommand, TenantId tenant, string machineName, LauncherCommand command, CancellationToken ct)
    {
        if (sendCommand is null)
            return null;

        var result = await sendCommand(tenant, machineName, command, ct);
        FileLog.Write($"[LauncherCommandRouter] {command.Verb} tenant={tenant.Value} machine={machineName}: {(result is null ? "no stream -> HTTP relay fallback" : $"stream status={result.Status}")}");
        return result;
    }
}
