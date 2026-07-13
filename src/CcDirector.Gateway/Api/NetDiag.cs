using System.Net;
using System.Net.Sockets;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Helpers for the /diag/* network-diagnostics endpoints (auto-network-switching mission). Pure and
/// unit-testable: the client-IP classifier that lets the mobile Diagnostics page name the connection
/// path, and the throughput payload builder.
/// </summary>
internal static class NetDiag
{
    /// <summary>4 MiB: sub-second to download on a LAN, visibly slower over a DERP relay. The default download size.</summary>
    public const int DefaultPayloadBytes = 4 * 1024 * 1024;

    /// <summary>Hard cap on the payload size so the endpoint cannot be turned into a bandwidth amplifier.</summary>
    public const int MaxPayloadBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Name the caller's network path from the IP the Gateway sees (after X-Forwarded-For processing):
    /// "tailscale" for the 100.64.0.0/10 CGNAT range Tailscale assigns, "lan" for an RFC-1918 private
    /// address (or APIPA link-local), "local" for loopback, and "other" for anything else (a routable
    /// public address). A null address is "other".
    /// </summary>
    public static string ClassifyClientIp(IPAddress? ip)
    {
        if (ip is null) return "other";
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return "local";
        if (ip.AddressFamily != AddressFamily.InterNetwork) return "other"; // classify IPv4 only

        var b = ip.GetAddressBytes(); // 4 bytes for IPv4
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return "tailscale"; // 100.64.0.0/10 CGNAT
        if (b[0] == 10) return "lan";                                     // 10.0.0.0/8
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return "lan";        // 172.16.0.0/12
        if (b[0] == 192 && b[1] == 168) return "lan";                    // 192.168.0.0/16
        if (b[0] == 169 && b[1] == 254) return "lan";                    // 169.254.0.0/16 APIPA link-local
        return "other";
    }

    /// <summary>
    /// Build a payload of <paramref name="size"/> bytes filled with a non-repeating pseudo-random pattern
    /// so response compression (if ever enabled) cannot shrink it and inflate the measured throughput.
    /// A cheap linear-congruential generator keeps this allocation-free beyond the returned buffer.
    /// </summary>
    public static byte[] BuildPayload(int size)
    {
        var data = new byte[size];
        uint state = 0x9E3779B9u;
        for (int i = 0; i < size; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            data[i] = (byte)(state >> 24);
        }
        return data;
    }
}
