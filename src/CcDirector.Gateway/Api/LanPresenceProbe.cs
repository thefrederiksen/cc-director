using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Best-effort physical LAN-presence probe (Network Diagnostics mission, Phase 1 monitor). Resolves the
/// MAC of an IPv4 on the local subnet via ARP (Windows <c>SendARP</c>). ARP is answered at layer 2 by the
/// device's NIC regardless of app/sleep state or ICMP being disabled, and regardless of the Tailscale
/// path, so it is the honest "is this device on my home LAN right now" - the signal the drift detector
/// requires (matched against the device's cached MAC) before it will ever accrue a home-relay drift.
///
/// A departed device does not answer ARP at its old home IP, so the probe returning null (or a DIFFERENT
/// MAC than cached) is what keeps the never-cry-wolf guarantee. Windows-only and best-effort: any failure
/// returns null (treated as "cannot confirm present" -> never alert), never throws.
/// </summary>
public static class LanPresenceProbe
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    [SupportedOSPlatform("windows")]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint macAddrLen);

    /// <summary>
    /// Resolve the MAC address of <paramref name="ipv4"/> on the local subnet, normalized as
    /// <c>aa-bb-cc-dd-ee-ff</c> (lower-case), or null when it does not answer / cannot be resolved.
    /// </summary>
    public static string? TryResolveMac(string ipv4)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!IPAddress.TryParse(ipv4, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return null;

        try
        {
            uint dest = BitConverter.ToUInt32(ip.GetAddressBytes(), 0); // GetAddressBytes is already network order
            var mac = new byte[6];
            uint len = 6;
            if (SendARP(dest, 0, mac, ref len) != 0 || len < 6) return null;
            // An all-zero result means "no entry" on some stacks; treat it as unresolved.
            if (mac.All(b => b == 0)) return null;
            return NormalizeMac(mac);
        }
        catch
        {
            return null; // best-effort: any P/Invoke failure is "cannot confirm present"
        }
    }

    /// <summary>Format the first six bytes as <c>aa-bb-cc-dd-ee-ff</c> (lower-case). Pure - unit-tested.</summary>
    public static string NormalizeMac(byte[] mac)
        => string.Join("-", mac.Take(6).Select(b => b.ToString("x2")));
}
