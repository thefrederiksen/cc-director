using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// Decides which forwarding proxies the Gateway will believe when they say "this request arrived over HTTPS".
///
/// The Gateway always runs behind a TLS-terminating front end, and that front end forwards plaintext. So
/// <c>ctx.Request.Scheme</c> is only correct if <c>X-Forwarded-Proto</c> is honoured - and honouring it from
/// the wrong sender is how anything on the network gets to claim it is HTTPS. The two deployments have
/// genuinely different front ends, so they get genuinely different answers:
///
/// <list type="bullet">
/// <item><b>Self-hosted</b> sits behind Tailscale Serve, which terminates TLS at :443 and forwards to
/// LOOPBACK. Loopback is therefore the only sender that may be believed, and nothing else on the tailnet or
/// the LAN can spoof its way to HTTPS.</item>
/// <item><b>Hosted</b> sits behind the Azure App Service front end, which terminates TLS and forwards from a
/// PLATFORM address that is not loopback and is not a fixed, documentable value. Restricting to loopback
/// there means <c>X-Forwarded-Proto: https</c> is discarded on every request, so the Gateway believes it is
/// serving plain HTTP on an HTTPS-only host.</item>
/// </list>
///
/// The hosted container is not addressable except through that front end - inbound traffic cannot reach it
/// any other way - so accepting the platform's forwarded headers there does not widen anything a caller
/// could exploit. This is the standard configuration for ASP.NET Core on App Service, and it is applied
/// ONLY when hosted: the self-host restriction is deliberately left exactly as it was.
///
/// What went wrong without this (issue #1870): every hosted <c>ViewUrl</c> was minted as
/// <c>gw=http://...</c> on an HTTPS-only host, handing clients a plaintext address for the API base. The
/// same discarded scheme also reached the remote sign-in URL builder and the Director base-URL helper, so
/// this is fixed once here at the transport layer rather than patched at each of the three call sites.
/// </summary>
public static class ForwardedHeadersPolicy
{
    /// <summary>The forwarded headers the Gateway reads, in both deployments.</summary>
    public const ForwardedHeaders Headers =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

    /// <summary>
    /// Apply the proxy-trust policy for this deployment. When <paramref name="isHosted"/> is false the known
    /// proxy set is loopback and nothing else; when it is true the set is left empty, which is how
    /// <see cref="ForwardedHeadersMiddleware"/> is told to accept the platform front end it cannot enumerate.
    /// </summary>
    /// <param name="options">The options instance to configure.</param>
    /// <param name="isHosted">True on the hosted, multi-tenant Gateway; false for a self-hosted Gateway.</param>
    public static void Apply(ForwardedHeadersOptions options, bool isHosted)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.ForwardedHeaders = Headers;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        if (isHosted)
            return;

        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
    }
}
