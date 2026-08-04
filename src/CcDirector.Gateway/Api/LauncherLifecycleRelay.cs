using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The ONE tenant-scoped path from "this tenant wants a lifecycle verb run on this machine" to the machine's
/// launcher. Both callers go through it:
///
///   * the HTTP relay routes - POST /machines/{machine}/director/start|stop|restart and
///     POST /machines/{machine}/launch - which resolve the calling tenant from the authenticated device key;
///   * <see cref="Running.RelayDirectorLauncher"/>, the in-process auto-launch the target resolver uses when
///     a tenant asks for a session on a machine whose Director is not running.
///
/// WHY THE SECOND CALLER EXISTS AT ALL, AND WHY IT NO LONGER DIALS THE GATEWAY. The auto-launcher used to
/// reach the launcher by dialing the Gateway's OWN /machines/{machine}/director/start over loopback, carrying
/// the host-wide shared token. That reused the shipped path, which was the point - but it DESTROYED THE
/// CALLER'S IDENTITY on the way: a fresh inbound request carries no device key, so on the hosted Gateway the
/// tenant resolved to nothing and the inner call refused itself. Worse, had it resolved to anything, it would
/// have resolved to a tenant unrelated to the one that asked. A loopback hop cannot carry a tenant, so the
/// shared code moved DOWN here where the tenant is an argument and cannot be lost.
///
/// THERE IS EXACTLY ONE DISPATCH ARM: the persistent stream the launcher opened to the Gateway
/// (remove-the-network-port mission, phase 6). There used to be a second - an HTTP relay that dialed the
/// launcher's loopback REST interface with a stored address, port and bearer token whenever the stream was
/// absent - and it is deliberately GONE, not switched off. The launcher no longer listens on anything, so a
/// dial would reach nothing; and a fallback that runs exactly when the primary path is already failing is
/// the second door this mission exists to remove. A launcher whose stream is down is REFUSED loudly
/// (<see cref="RelayOutcomeKind.NotConnected"/>), never reached another way.
///
/// The stream arm resolves the launcher connection as (tenant, machine) through
/// <see cref="LauncherCommandRouter"/>, and the registered-at-all check resolves the registry entry as
/// (tenant, machine) through <see cref="LauncherRegistry"/>. Neither can be reached with a machine name
/// alone, so a caller can only ever drive a launcher its OWN tenant registered.
/// </summary>
internal static class LauncherLifecycleRelay
{
    /// <summary>How the relay attempt ended. The HTTP routes map this onto a status code and body; the
    /// in-process auto-launcher only asks whether the launcher accepted.</summary>
    internal enum RelayOutcomeKind
    {
        /// <summary>The launcher answered over the stream. <see cref="LauncherRelayOutcome.RelayStatus"/>
        /// carries its verdict - which may itself be a failure the launcher reported.</summary>
        Relayed,

        /// <summary>This tenant has no launcher registered for that machine name. Another tenant's launcher of
        /// the same bare name is NOT a match and is not consulted.</summary>
        NoLauncher,

        /// <summary>This tenant's launcher for that machine is registered (it has heartbeated) but has no
        /// active stream connection right now, so there is no way to deliver the command. The stream is the
        /// ONLY path - this is a refusal, not a cue to try something else.</summary>
        NotConnected,
    }

    /// <summary>The result of one relay attempt.</summary>
    /// <param name="Kind">How the attempt ended.</param>
    /// <param name="RelayStatus">The launcher's own status when <see cref="RelayOutcomeKind.Relayed"/>, else 0.</param>
    /// <param name="Payload">The launcher's response body when relayed, else null.</param>
    internal sealed record LauncherRelayOutcome(
        RelayOutcomeKind Kind,
        int RelayStatus = 0,
        string? Payload = null)
    {
        /// <summary>True when the launcher answered AND its answer was a success. This is the whole question
        /// the in-process auto-launcher asks.</summary>
        public bool Accepted => Kind == RelayOutcomeKind.Relayed && RelayStatus is >= 200 and < 300;
    }

    /// <summary>
    /// Run a Director lifecycle verb ("start", "stop", "restart") on the CALLING TENANT's launcher for
    /// <paramref name="machine"/>. The slot guard is NOT applied here: it reads the caller's request body and
    /// so belongs to the HTTP route, which runs it before calling this.
    /// </summary>
    public static Task<LauncherRelayOutcome> SendDirectorVerbAsync(
        TenantId tenant, string machine, string verb, string? exePath, bool confirmProtected,
        LauncherRegistry launchers, LauncherCommandRouter.SendLauncherCommandAsync? sendLauncherCommand,
        CancellationToken ct)
        => SendAsync(
            tenant, machine,
            new LauncherCommand
            {
                Verb = $"director/{verb}",
                Path = exePath,
                ConfirmProtected = confirmProtected,
            },
            isQuery: false, launchers, sendLauncherCommand, ct);

    /// <summary>
    /// Run a generic launch on the CALLING TENANT's launcher for <paramref name="machine"/>.
    /// </summary>
    public static Task<LauncherRelayOutcome> SendLaunchAsync(
        TenantId tenant, string machine, LaunchRelayBody? body,
        LauncherRegistry launchers, LauncherCommandRouter.SendLauncherCommandAsync? sendLauncherCommand,
        CancellationToken ct)
        => SendAsync(
            tenant, machine,
            new LauncherCommand
            {
                Verb = "launch",
                Path = body?.Path,
                App = body?.App,
                Args = body?.Args,
                Cwd = body?.Cwd,
                Headless = body?.Headless ?? false,
            },
            isQuery: false, launchers, sendLauncherCommand, ct);

    /// <summary>
    /// Run a QUERY verb - "apps" or "files" - on the CALLING TENANT's launcher for <paramref name="machine"/>
    /// and return the launcher's answer.
    ///
    /// It differs from the action verbs in the only way a question differs from an instruction: the
    /// launcher's answer is carried back to the caller rather than reduced to whether it worked. Everything
    /// else - tenant scoping, the failure outcomes - is the shared path, because a query that could reach a
    /// machine the action verbs could not would be a second, weaker boundary.
    /// </summary>
    public static Task<LauncherRelayOutcome> SendQueryAsync(
        TenantId tenant, string machine, string verb, string? query, int limit, int timeoutMilliseconds,
        LauncherRegistry launchers, LauncherCommandRouter.SendLauncherCommandAsync? sendLauncherCommand,
        CancellationToken ct)
        => SendAsync(
            tenant, machine,
            new LauncherCommand
            {
                Verb = verb,
                Query = query,
                Limit = limit,
                TimeoutMilliseconds = timeoutMilliseconds,
            },
            isQuery: true, launchers, sendLauncherCommand, ct);

    /// <summary>
    /// The single-arm dispatch: push the command down the calling tenant's launcher stream. A null from the
    /// router means the command could not be DELIVERED (no hook wired, or no active connection for this
    /// tenant+machine); the registry then decides which honest refusal that is - "never registered" or
    /// "registered but not connected". Nothing is dialed in either case.
    /// </summary>
    private static async Task<LauncherRelayOutcome> SendAsync(
        TenantId tenant, string machine, LauncherCommand streamCommand, bool isQuery,
        LauncherRegistry launchers, LauncherCommandRouter.SendLauncherCommandAsync? sendLauncherCommand,
        CancellationToken ct)
    {
        var streamResult = await LauncherCommandRouter.TrySendAsync(sendLauncherCommand, tenant, machine, streamCommand, ct);
        if (streamResult is not null)
        {
            var streamStatus = streamResult.Status switch
            {
                LauncherCommandStatus.Ok => 200,
                LauncherCommandStatus.BadRequest => 400,
                _ => 502,
            };
            // A QUERY answers with data, so its payload is passed through exactly as the launcher wrote it.
            // Synthesising {ok:true} here - which is right for an action verb, whose only news is that it
            // worked - would throw away the entire answer and hand the caller a success with no result in it.
            var streamPayload = isQuery && streamResult.IsOk && streamResult.Payload is not null
                ? streamResult.Payload
                : streamResult.IsOk
                ? System.Text.Json.JsonSerializer.Serialize(new { ok = true, via = "stream" })
                : System.Text.Json.JsonSerializer.Serialize(new { error = streamResult.Error, via = "stream" });
            FileLog.Write($"[LauncherLifecycleRelay] {streamCommand.Verb} tenant={tenant.Value} machine={machine} via=stream -> {streamStatus}");
            return new LauncherRelayOutcome(RelayOutcomeKind.Relayed, streamStatus, streamPayload);
        }

        // Undeliverable. Decide WHICH refusal, in the CALLER'S partition - a machine name alone reaches
        // nothing here either.
        if (launchers.Get(tenant, machine) is null)
        {
            FileLog.Write($"[LauncherLifecycleRelay] {streamCommand.Verb}: no launcher registered for tenant={tenant.Value}, machine={machine}");
            return new LauncherRelayOutcome(RelayOutcomeKind.NoLauncher);
        }

        FileLog.Write($"[LauncherLifecycleRelay] {streamCommand.Verb}: launcher registered but NOT stream-connected for tenant={tenant.Value}, machine={machine} - refused (the stream is the only path)");
        return new LauncherRelayOutcome(RelayOutcomeKind.NotConnected);
    }
}
