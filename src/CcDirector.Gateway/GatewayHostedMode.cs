using System.Reflection;

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
    /// <summary>The runtime toggle that turns hosted mode on: <c>CC_GATEWAY_HOSTED=1</c>. This is the value
    /// a slot swap or config restore can drop; the IMMUTABLE identity is <see cref="IsHostedImage"/>.</summary>
    public const string HostedEnvVar = "CC_GATEWAY_HOSTED";

    /// <summary>True when <c>CC_GATEWAY_HOSTED=1</c> - this Gateway is a hosted/container deployment.</summary>
    public static bool IsHosted =>
        string.Equals(Environment.GetEnvironmentVariable(HostedEnvVar), "1", StringComparison.Ordinal);

    /// <summary>
    /// True when the RUNNING executable is the hosted container build - the IMMUTABLE hosted identity, read
    /// from the <see cref="HostedGatewayImageAttribute"/> the hosted host (<c>CcDirector.Gateway.Host</c>)
    /// stamps onto itself at compile time. Unlike <see cref="IsHosted"/> (a runtime environment toggle), this
    /// cannot be dropped by a slot swap, a config restore, or a lost variable: it is part of the compiled
    /// artifact. <see cref="HostedStartupContract"/> uses it to decide that the FULL hosted contract must be
    /// proven at startup or the process must refuse to run - so a hosted image can never silently downgrade
    /// to Local single-tenant / no-auth semantics.
    /// </summary>
    public static bool IsHostedImage => IsHostedImageAssembly(System.Reflection.Assembly.GetEntryAssembly());

    /// <summary>
    /// Pure form of <see cref="IsHostedImage"/> for testing: true when <paramref name="entryAssembly"/> is
    /// non-null and carries <see cref="HostedGatewayImageAttribute"/>. A null entry assembly (some test
    /// hosts) is treated as NOT the hosted image, so a test run is self-host by default.
    /// </summary>
    internal static bool IsHostedImageAssembly(System.Reflection.Assembly? entryAssembly)
        => entryAssembly?.GetCustomAttribute<HostedGatewayImageAttribute>() is not null;

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
