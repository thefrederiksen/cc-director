using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Reads the port a started test host actually bound (issue #2161).
///
/// Tests that spin a bare <see cref="WebApplication"/> - rather than a real <see cref="GatewayHost"/> - bind
/// their own listener and then dial it. They used to compute the port in advance with a probe that released
/// it before the bind, which is the race this issue removes. Binding port 0 fixes the bind; this fixes the
/// other half, because the CLIENT still has to learn the number, and "0" is not an address anything can
/// connect to. Call it AFTER StartAsync.
///
/// It throws rather than returning 0 for the same reason <see cref="GatewayHost"/> does: a zero here becomes
/// a base address of http://127.0.0.1:0, and the failure then surfaces as an unhelpful socket error far from
/// the code that caused it.
/// </summary>
internal static class BoundPort
{
    /// <summary>The port <paramref name="app"/> is listening on. Only valid once the app has started.</summary>
    internal static int Of(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var addresses = app.Services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is not null)
        {
            foreach (var address in addresses)
            {
                if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0)
                    return uri.Port;
            }
        }

        throw new InvalidOperationException(
            "The test host reported no bound address with a usable port. Call BoundPort.Of only AFTER " +
            "StartAsync, and bind with port 0 so the operating system assigns one.");
    }

    /// <summary>The loopback base address of a started test host, ready for an HttpClient.</summary>
    internal static Uri LoopbackBase(WebApplication app) => new($"http://127.0.0.1:{Of(app)}/");
}
