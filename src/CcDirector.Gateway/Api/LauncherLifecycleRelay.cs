using System.Net.Http.Json;
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
/// BOTH DISPATCH ARMS ARE SCOPED, INCLUDING THE FALLBACK. The stream arm resolves the launcher connection as
/// (tenant, machine) through <see cref="LauncherCommandRouter"/>; the REST fallback resolves the launcher's
/// stored address, port and bearer token as (tenant, machine) through <see cref="LauncherRegistry"/>. Neither
/// arm can be reached with a machine name alone, so a caller can only ever drive a launcher its OWN tenant
/// registered. This matters more than it looks: the fallback is the arm taken when the stream is absent, so
/// it is the FAILURE path, and an ungated failure path is a gate that opens exactly when something is already
/// going wrong.
/// </summary>
internal static class LauncherLifecycleRelay
{
    /// <summary>How the relay attempt ended. The HTTP routes map this onto a status code and body; the
    /// in-process auto-launcher only asks whether the launcher accepted.</summary>
    internal enum RelayOutcomeKind
    {
        /// <summary>The launcher answered (over the stream or over REST). <see cref="LauncherRelayOutcome.RelayStatus"/>
        /// carries its verdict - which may itself be a failure the launcher reported.</summary>
        Relayed,

        /// <summary>This tenant has no launcher registered for that machine name. Another tenant's launcher of
        /// the same bare name is NOT a match and is not consulted.</summary>
        NoLauncher,

        /// <summary>The entry exists but carries no bearer token - a registry the Gateway cannot use.</summary>
        NoToken,

        /// <summary>The launcher is registered but could not be reached at its stored address.</summary>
        Unreachable,
    }

    /// <summary>The result of one relay attempt.</summary>
    /// <param name="Kind">How the attempt ended.</param>
    /// <param name="RelayStatus">The launcher's own status when <see cref="RelayOutcomeKind.Relayed"/>, else 0.</param>
    /// <param name="Payload">The launcher's response body when relayed, else null.</param>
    /// <param name="Detail">The transport error when unreachable, else null.</param>
    /// <param name="Port">The launcher's registered port, for an unreachable message that names a real address.</param>
    /// <param name="DialHost">The address actually dialed, for the same reason.</param>
    internal sealed record LauncherRelayOutcome(
        RelayOutcomeKind Kind,
        int RelayStatus = 0,
        string? Payload = null,
        string? Detail = null,
        int Port = 0,
        string? DialHost = null)
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
            streamCommand: new LauncherCommand
            {
                Verb = $"director/{verb}",
                Path = exePath,
                ConfirmProtected = confirmProtected,
            },
            restPath: $"/director/{verb}",
            // The lifecycle verbs carry NO body on the REST arm - the launcher reads the verb from the path.
            restBody: null, sendsJsonBody: false,
            launchers, sendLauncherCommand, ct);

    /// <summary>
    /// Run a generic launch on the CALLING TENANT's launcher for <paramref name="machine"/>, forwarding the
    /// caller's body verbatim on the REST arm.
    /// </summary>
    public static Task<LauncherRelayOutcome> SendLaunchAsync(
        TenantId tenant, string machine, LaunchRelayBody? body,
        LauncherRegistry launchers, LauncherCommandRouter.SendLauncherCommandAsync? sendLauncherCommand,
        CancellationToken ct)
        => SendAsync(
            tenant, machine,
            streamCommand: new LauncherCommand
            {
                Verb = "launch",
                Path = body?.Path,
                Args = body?.Args,
                Cwd = body?.Cwd,
                Headless = body?.Headless ?? false,
            },
            restPath: "/launch",
            // The launch body is forwarded VERBATIM, and it is sent as JSON even when it is null - a null body
            // serialises to "null" and the launcher answers its own 400, which is the behaviour the route has
            // always had for an unparsable body. Posting nothing instead would be a different request.
            restBody: body, sendsJsonBody: true,
            launchers, sendLauncherCommand, ct);

    /// <summary>
    /// The shared two-arm dispatch. The persistent stream is preferred; a null from it means "no stream"
    /// (stream mode off, or this tenant's launcher for this machine is not joined) and ONLY then is the REST
    /// relay dialed. A non-null stream result - success or a typed failure - is authoritative and the REST arm
    /// is not touched, so one command can never be executed twice.
    /// </summary>
    private static async Task<LauncherRelayOutcome> SendAsync(
        TenantId tenant, string machine, LauncherCommand streamCommand, string restPath, object? restBody,
        bool sendsJsonBody,
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
            var streamPayload = streamResult.IsOk
                ? System.Text.Json.JsonSerializer.Serialize(new { ok = true, via = "stream" })
                : System.Text.Json.JsonSerializer.Serialize(new { error = streamResult.Error, via = "stream" });
            FileLog.Write($"[LauncherLifecycleRelay] {streamCommand.Verb} tenant={tenant.Value} machine={machine} via=stream -> {streamStatus}");
            return new LauncherRelayOutcome(RelayOutcomeKind.Relayed, streamStatus, streamPayload);
        }

        // The REST fallback, resolved in the CALLER'S partition. A machine name alone reaches nothing here.
        var launcher = launchers.Get(tenant, machine);
        if (launcher is null)
        {
            FileLog.Write($"[LauncherLifecycleRelay] {streamCommand.Verb}: no launcher registered for tenant={tenant.Value}, machine={machine}");
            return new LauncherRelayOutcome(RelayOutcomeKind.NoLauncher);
        }

        var token = launchers.GetToken(tenant, machine);
        if (string.IsNullOrEmpty(token))
        {
            FileLog.Write($"[LauncherLifecycleRelay] {streamCommand.Verb}: launcher token missing for tenant={tenant.Value}, machine={machine}");
            return new LauncherRelayOutcome(RelayOutcomeKind.NoToken);
        }

        var networkAddress = launchers.GetNetworkAddress(tenant, machine) ?? "";
        var dialHost = string.IsNullOrWhiteSpace(networkAddress) ? "127.0.0.1" : networkAddress;

        using var http = BuildLauncherClient(launcher.Port, token, networkAddress);
        try
        {
            var response = sendsJsonBody
                ? await http.PostAsJsonAsync(restPath, restBody, ct)
                : await http.PostAsync(restPath, content: null, ct);
            var payload = await response.Content.ReadAsStringAsync(ct);
            FileLog.Write($"[LauncherLifecycleRelay] {streamCommand.Verb} tenant={tenant.Value} machine={machine} host={dialHost} -> {(int)response.StatusCode}");
            return new LauncherRelayOutcome(RelayOutcomeKind.Relayed, (int)response.StatusCode, payload,
                Port: launcher.Port, DialHost: dialHost);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherLifecycleRelay] {streamCommand.Verb} tenant={tenant.Value} machine={machine} host={dialHost} FAILED: {ex.Message}");
            return new LauncherRelayOutcome(RelayOutcomeKind.Unreachable, Detail: ex.Message,
                Port: launcher.Port, DialHost: dialHost);
        }
    }

    /// <summary>
    /// Build a short-lived HttpClient pointed at the launcher's REST API.
    ///
    /// When <paramref name="networkAddress"/> is non-empty the launcher is on a REMOTE machine: dial
    /// http://&lt;networkAddress&gt;:&lt;port&gt;/ over the tailnet. When it is empty the launcher is
    /// co-located with the Gateway: dial http://127.0.0.1:&lt;port&gt;/ on loopback.
    /// </summary>
    private static HttpClient BuildLauncherClient(int port, string token, string networkAddress)
    {
        var host = string.IsNullOrWhiteSpace(networkAddress) ? "127.0.0.1" : networkAddress;
        var http = new HttpClient
        {
            BaseAddress = new Uri($"http://{host}:{port}/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        return http;
    }
}
