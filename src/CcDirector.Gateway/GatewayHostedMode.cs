namespace CcDirector.Gateway;

/// <summary>
/// The HOSTED-mode signal for a Gateway running as a hosted/container deployment (for example Azure App
/// Service), reached by its PUBLIC URL rather than a tailnet. Set the environment variable
/// <c>CC_GATEWAY_HOSTED=1</c> to enable it. Fail-safe: any missing or other value is NOT hosted, so the
/// desktop/local behavior is byte-identical to today.
///
/// When hosted:
///  - Tailscale Serve auto-provisioning (<see cref="Tailscale.TailscaleServeProvisioner"/>) and the
///    tailscale-shelling network-diagnostics monitor (<see cref="Api.NetDiagMonitor"/>) are NOT started. A
///    hosted Gateway is reached by its public URL, not a tailnet, and the container image bundles no tailscale
///    binary, so starting them only produces noise and repeated errors.
///  - The listener honors the App Service port convention (<c>WEBSITES_PORT</c>, else <c>PORT</c>, else the
///    default), so the platform's front-end can route to the container's assigned port. The bind address is
///    all-interfaces in BOTH modes (that is unchanged - it is how a tailnet client reaches a desktop Gateway),
///    with the auth gate enforcing on every non-public route, so an all-interfaces bind is
///    reachable-but-authenticated, not open.
/// </summary>
public static class GatewayHostedMode
{
    /// <summary>True when <c>CC_GATEWAY_HOSTED=1</c> - this Gateway is a hosted/container deployment.</summary>
    public static bool IsHosted =>
        string.Equals(Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED"), "1", StringComparison.Ordinal);

    /// <summary>
    /// The port a hosted Gateway should listen on: the App Service <c>WEBSITES_PORT</c>, else <c>PORT</c>, else
    /// <paramref name="fallbackPort"/> (the port the process was started with). Only consulted in hosted mode;
    /// a missing/blank/invalid value falls through to the fallback, so it never fails the listener.
    /// </summary>
    public static int ResolveHostedPort(int fallbackPort)
    {
        foreach (var name in new[] { "WEBSITES_PORT", "PORT" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value) && int.TryParse(value, out var p) && p is > 0 and <= 65535)
                return p;
        }
        return fallbackPort;
    }
}
