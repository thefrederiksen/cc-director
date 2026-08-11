using CcDirector.Core.Utilities;
using CcDirector.Gateway.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// <c>GET /account/mobile-qr.png</c> (devthrottle_internal #1508): the scannable code the Cockpit's
/// Phone panel shows, encoding the address of the mobile app on THIS Gateway.
///
/// It exists because nothing in the Cockpit pointed at the mobile app: a person who wanted DevThrottle
/// on their phone had to already know the address and type it in by hand. Scanning a code off the
/// screen they are already looking at is the shortest path there is.
///
/// The QR carries ONLY the plain mobile address - never a device key, a token, or any other secret.
/// <see cref="DeviceSignInQrCode"/> enforces that shape (absolute http/https only) and is the same
/// renderer the Add-a-device window uses, so there is one QR implementation on this host.
///
/// The address comes from the REQUEST's own scheme and host, which is the address the person reached
/// the Cockpit on and therefore the one their phone has to reach too. It is never read from a header a
/// caller controls beyond that, and never guessed from configuration.
///
/// LOOPBACK FAILS LOUDLY. A Cockpit opened on localhost would otherwise produce a perfectly valid QR
/// code for an address no phone can ever reach - it scans, it opens, it times out, and the person is
/// left debugging their network instead of their address. So a loopback host is answered 409 with the
/// reason, and the panel says it in words rather than showing a code that cannot work.
///
/// Authentication: the path sits under <c>/account/</c>, so it inherits the host-wide Gateway token
/// middleware exactly like <c>/account/status</c> - it is not on the public-paths allow-list, and an
/// uncredentialed request is answered 401 before this delegate runs.
/// </summary>
internal static class MobileQrEndpoint
{
    /// <summary>The app's own path on this Gateway - the one the Gateway serves the mobile bundle at.</summary>
    private const string MobilePath = "/mobile";

    /// <summary>Module size in pixels. Large enough to scan off a desktop screen at arm's length.</summary>
    private const int PixelsPerModule = 8;

    /// <summary>Maps <c>GET /account/mobile-qr.png</c>.</summary>
    /// <param name="app">The route builder.</param>
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/account/mobile-qr.png", (HttpContext ctx) =>
        {
            var host = ctx.Request.Host;
            if (!host.HasValue)
            {
                FileLog.Write("[MobileQrEndpoint] GET /account/mobile-qr.png: the request carried no host, so there is no address to encode");
                return Results.Json(new { error = "this request carried no host, so there is no address to put in the code" },
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (IsLoopback(host.Host))
            {
                FileLog.Write($"[MobileQrEndpoint] GET /account/mobile-qr.png: REFUSED - the Cockpit was reached on the loopback host '{host.Host}', which no phone can reach");
                return Results.Json(new
                {
                    error = "this Cockpit is open on " + host.Host + ", an address that only exists on this machine. "
                          + "Open the Cockpit on the address your phone can reach and the code will appear."
                }, statusCode: StatusCodes.Status409Conflict);
            }

            var url = $"{ctx.Request.Scheme}://{host.Value}{MobilePath}";
            FileLog.Write($"[MobileQrEndpoint] GET /account/mobile-qr.png: encoding the mobile address on host={host.Host}");

            var png = DeviceSignInQrCode.RenderPng(url, PixelsPerModule);
            FileLog.Write($"[MobileQrEndpoint] GET /account/mobile-qr.png: rendered {png.Length} PNG byte(s)");
            return Results.File(png, "image/png");
        });
    }

    /// <summary>
    /// Whether a host is one that only this machine can reach.
    ///
    /// This file therefore carries a loopback literal on purpose, and it is the OPPOSITE of the thing the
    /// no-cross-machine-loopback policy exists to stop: nothing here dials a loopback address, it
    /// RECOGNIZES one so the endpoint can refuse to hand it to a phone.
    ///
    /// The addresses are left to <see cref="System.Net.IPAddress.IsLoopback"/>, which covers the whole
    /// 127/8 block and the version-6 loopback without this file naming any of them - so only the NAME
    /// needs a literal. Square brackets are stripped first because a version-6 host arrives in a Host
    /// header wrapped in them.
    /// </summary>
    internal static bool IsLoopback(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim().Trim('[', ']');
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return System.Net.IPAddress.TryParse(h, out var ip) && System.Net.IPAddress.IsLoopback(ip);
    }
}
