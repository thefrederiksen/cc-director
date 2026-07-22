using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Network;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Server-side network diagnostic (Network Diagnostics mission): the exact check an operator runs by
/// hand - "tailscale status / ping / netcheck" - turned into something an AGENT can call with no phone
/// and no app open. It answers the one question the phone speed test cannot: for each connected device,
/// is Tailscale on a DIRECT LAN path or RELAYING through a distant DERP server, and at what latency.
///
/// The parse helpers are pure and unit-tested; Collect shells the CLI through the shared
/// <see cref="TailscaleCli"/> and stitches the results together.
/// </summary>
public static class TailscaleDiagnostics
{
    /// <summary>Cap on how many peers we actively ping, so a large tailnet cannot make the endpoint slow.</summary>
    public const int MaxPeersToPing = 12;

    /// <summary>One connected device's live path, as seen from this machine.</summary>
    public sealed record PeerDiag
    {
        public string Name { get; init; } = "";
        public string? TailscaleIp { get; init; }
        public string? Os { get; init; }
        public bool Online { get; init; }
        /// <summary>True = direct LAN/peer path, false = relayed through DERP, null = could not determine (not pinged).</summary>
        public bool? Direct { get; init; }
        /// <summary>The active path: a "192.168.x.y:port" address when direct, or "DERP(region)" when relayed.</summary>
        public string? Path { get; init; }
        public double? LatencyMs { get; init; }
        public string? Note { get; init; }
    }

    /// <summary>
    /// The Gateway's finished ruling for the connection-status pill. Browser clients render these fields
    /// verbatim; they do not reinterpret diagnostic fields or invent a label when the ruling is unavailable.
    /// </summary>
    public sealed record NetworkConnectionVerdict
    {
        public string Level { get; init; } = "grey";
        public string Label { get; init; } = "Unknown";
        public string Detail { get; init; } = "Connection quality is unavailable.";
    }

    /// <summary>The whole picture an agent reads to judge network health with no phone involved.</summary>
    public sealed record NetworkDiag
    {
        public bool TailscaleAvailable { get; init; }
        public string? BackendState { get; init; }
        public string? SelfName { get; init; }
        public string? SelfTailscaleIp { get; init; }
        public bool? UdpOk { get; init; }
        public bool? MappingVariesByDestIp { get; init; }
        public string? NearestDerp { get; init; }
        public List<PeerDiag> Peers { get; init; } = new();
        public List<string> Notes { get; init; } = new();
        public DateTime CollectedAt { get; init; } = DateTime.UtcNow;
        public NetworkConnectionVerdict ConnectionVerdict { get; init; } = new();
    }

    /// <summary>
    /// Return the complete hosted-mode answer. A successful request already proves that the browser reached
    /// the public Gateway. Tailscale is not part of that browser-to-Gateway path, so no command is run and no
    /// shared tailnet inventory is included.
    /// </summary>
    internal static NetworkDiag HostedConnection() => new()
    {
        TailscaleAvailable = false,
        Notes = { "Tailscale peer diagnostics do not apply to the hosted Gateway's public internet connection." },
        ConnectionVerdict = new NetworkConnectionVerdict
        {
            Level = "green",
            Label = "Connected",
            Detail = "Connected to the hosted Gateway.",
        },
    };

    /// <summary>
    /// Stamp the self-hosted connection ruling onto a collected diagnostic. The request address identifies
    /// whether the browser arrived locally, over the local network, or through Tailscale; for a Tailscale
    /// request the collector's matching peer supplies the authoritative direct-versus-relay result.
    /// </summary>
    internal static NetworkDiag WithConnectionVerdict(
        NetworkDiag diagnostic, IPAddress? clientAddress, int consecutiveRelayObservations)
    {
        var clientPath = NetDiag.ClassifyClientIp(clientAddress);
        NetworkConnectionVerdict verdict;
        if (clientPath == "lan")
        {
            verdict = new NetworkConnectionVerdict
            {
                Level = "green",
                Label = "Direct local network",
                Detail = "Straight to the Gateway over your local network.",
            };
        }
        else if (clientPath == "local")
        {
            verdict = new NetworkConnectionVerdict
            {
                Level = "green",
                Label = "Local",
                Detail = "On the Gateway machine.",
            };
        }
        else if (clientPath == "tailscale")
        {
            verdict = TailscaleConnectionVerdict(
                diagnostic, NormalizeAddress(clientAddress), consecutiveRelayObservations);
        }
        else
        {
            verdict = new NetworkConnectionVerdict
            {
                Level = "grey",
                Label = "Unknown",
                Detail = "The Gateway cannot classify this connection path.",
            };
        }

        return diagnostic with { ConnectionVerdict = verdict };
    }

    private static NetworkConnectionVerdict TailscaleConnectionVerdict(
        NetworkDiag diagnostic, string? clientAddress, int consecutiveRelayObservations)
    {
        if (!diagnostic.TailscaleAvailable)
        {
            return new NetworkConnectionVerdict
            {
                Level = "grey",
                Label = "Checking",
                Detail = "Confirming your Tailscale path...",
            };
        }

        var peer = diagnostic.Peers.Find(candidate => candidate.TailscaleIp == clientAddress);
        if (peer is null)
        {
            return new NetworkConnectionVerdict
            {
                Level = "grey",
                Label = "Unknown",
                Detail = "The Gateway does not see this device yet.",
            };
        }

        if (peer.Direct == true)
        {
            var latency = peer.LatencyMs is null
                ? ""
                : $" ({Math.Round(peer.LatencyMs.Value, MidpointRounding.AwayFromZero)} milliseconds)";
            return new NetworkConnectionVerdict
            {
                Level = "green",
                Label = "Fast",
                Detail = $"Direct path over your local network{latency}.",
            };
        }

        if (peer.Direct == false)
        {
            if (consecutiveRelayObservations < NetworkConnectionVerdictFold.RelayObservationsBeforeSlow)
            {
                return new NetworkConnectionVerdict
                {
                    Level = "amber",
                    Label = "Warming up",
                    Detail = "Connecting - this speeds up once the direct path forms.",
                };
            }

            return new NetworkConnectionVerdict
            {
                Level = "red",
                Label = "Slow",
                Detail = "Relaying through a distant server instead of a direct path.",
            };
        }

        return new NetworkConnectionVerdict
        {
            Level = "amber",
            Label = "Checking",
            Detail = "Confirming the path...",
        };
    }

    internal static bool IsRelayObservation(NetworkDiag diagnostic, IPAddress? clientAddress)
    {
        if (NetDiag.ClassifyClientIp(clientAddress) != "tailscale" || !diagnostic.TailscaleAvailable)
            return false;

        var normalized = NormalizeAddress(clientAddress);
        return diagnostic.Peers.Find(candidate => candidate.TailscaleIp == normalized)?.Direct == false;
    }

    internal static string? NormalizeAddress(IPAddress? address)
    {
        if (address is null)
            return null;
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
    }

    /// <summary>
    /// Collect the full diagnostic. Runs "status --json" (who is connected), pings each online peer to
    /// learn its live direct-vs-relay path and latency, and "netcheck" for UDP/NAT/DERP health. Never
    /// throws for a missing CLI or an unparseable line - it records a Note and returns what it has.
    /// </summary>
    public static NetworkDiag Collect()
    {
        if (!TailscaleCli.IsAvailable)
            return new NetworkDiag { TailscaleAvailable = false, Notes = { "tailscale CLI not found on this machine" } };
        return Collect(TailscaleCli.Run);
    }

    /// <summary>
    /// Testable core: the CLI runner is injected so the whole stitch-together is unit-testable without
    /// shelling a real tailscale (the injected runner IS the CLI, so this does not re-check installation).
    /// <paramref name="run"/> maps an argument string to (ok, stdout, message).
    /// </summary>
    internal static NetworkDiag Collect(Func<string, (bool ok, string stdout, string message)> run)
    {
        var notes = new List<string>();

        var (statusOk, statusJson, statusMsg) = run("status --json");
        if (!statusOk)
            return new NetworkDiag { TailscaleAvailable = true, Notes = { $"tailscale status failed: {statusMsg}" } };

        string? backendState = null, selfName = null, selfIp = null;
        var peers = new List<PeerDiag>();
        try
        {
            (backendState, selfName, selfIp, peers) = ParseStatus(statusJson);
        }
        catch (Exception ex)
        {
            notes.Add($"could not parse tailscale status: {ex.Message}");
        }

        // Ping each online peer to learn its LIVE path (direct vs DERP) and latency. status --json's
        // CurAddr/Relay reflect the last known path; a fresh ping is the authoritative current state and
        // also nudges the direct-path upgrade - exactly what proves "warming up" vs "genuinely relaying".
        var enriched = new List<PeerDiag>();
        int pinged = 0;
        foreach (var p in peers)
        {
            if (!p.Online || string.IsNullOrEmpty(p.TailscaleIp) || pinged >= MaxPeersToPing)
            {
                enriched.Add(p);
                continue;
            }
            pinged++;
            var (pingOk, pingOut, pingMsg) = run($"ping --c 2 --timeout 3s {p.TailscaleIp}");
            var parsed = ParsePingResult(pingOut);
            enriched.Add(p with
            {
                Direct = parsed.answered ? parsed.direct : null,
                Path = parsed.path ?? p.Path,
                LatencyMs = parsed.latencyMs,
                Note = parsed.answered ? null : (pingOk ? "no direct/relay path reported" : $"ping failed: {pingMsg}"),
            });
        }

        bool? udp = null, mappingVaries = null;
        string? nearestDerp = null;
        var (ncOk, ncOut, ncMsg) = run("netcheck");
        if (ncOk)
            (udp, mappingVaries, nearestDerp) = ParseNetcheckText(ncOut);
        else
            notes.Add($"tailscale netcheck failed: {ncMsg}");

        return new NetworkDiag
        {
            TailscaleAvailable = true,
            BackendState = backendState,
            SelfName = selfName,
            SelfTailscaleIp = selfIp,
            UdpOk = udp,
            MappingVariesByDestIp = mappingVaries,
            NearestDerp = nearestDerp,
            Peers = enriched,
            Notes = notes,
        };
    }

    // ----- pure parse helpers (unit-tested) -----

    /// <summary>
    /// Parse the pong line from "tailscale ping". Returns whether it answered, whether the active path is
    /// DIRECT (via a LAN/peer address) or RELAYED (via DERP), the path string, and the latency in ms.
    /// Examples:
    ///   "pong from host (100.86.144.11) via 192.168.1.15:52091 in 11ms" -> answered, direct, 11ms
    ///   "pong from host (100.x) via DERP(tor) in 84ms"                   -> answered, relayed, 84ms
    ///   "ping ... timed out" / "no matching peer"                        -> not answered
    /// </summary>
    public static (bool answered, bool direct, string? path, double? latencyMs) ParsePingResult(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return (false, false, null, null);

        // Prefer the last pong line (ping sends several; the last reflects the settled path).
        string? pong = null;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("pong ", StringComparison.OrdinalIgnoreCase) && line.Contains(" via ", StringComparison.OrdinalIgnoreCase))
                pong = line;
        }
        if (pong is null) return (false, false, null, null);

        double? latency = null;
        var m = Regex.Match(pong, @"in\s+([0-9]+(?:\.[0-9]+)?)\s*ms", RegexOptions.IgnoreCase);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
            latency = ms;

        var viaIdx = pong.IndexOf(" via ", StringComparison.OrdinalIgnoreCase);
        var afterVia = pong[(viaIdx + 5)..].Trim();
        // Path token is up to " in " (the latency clause) or end of line.
        var inIdx = afterVia.IndexOf(" in ", StringComparison.OrdinalIgnoreCase);
        var path = (inIdx >= 0 ? afterVia[..inIdx] : afterVia).Trim();

        bool direct = !path.StartsWith("DERP", StringComparison.OrdinalIgnoreCase);
        return (true, direct, path, latency);
    }

    /// <summary>
    /// Parse the human "tailscale netcheck" output for the three fields the diagnostic cares about:
    /// UDP reachability, whether NAT mapping varies by destination (hard NAT), and the nearest DERP name.
    /// Tolerant of the leading "* " bullet and spacing. Missing fields come back null.
    /// </summary>
    public static (bool? udp, bool? mappingVaries, string? nearestDerp) ParseNetcheckText(string stdout)
    {
        bool? udp = null, mappingVaries = null;
        string? nearestDerp = null;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim().TrimStart('*').Trim();
            if (line.StartsWith("UDP:", StringComparison.OrdinalIgnoreCase))
                udp = line[4..].Trim().StartsWith("true", StringComparison.OrdinalIgnoreCase);
            else if (line.StartsWith("MappingVariesByDestIP:", StringComparison.OrdinalIgnoreCase))
                mappingVaries = line["MappingVariesByDestIP:".Length..].Trim().StartsWith("true", StringComparison.OrdinalIgnoreCase);
            else if (line.StartsWith("Nearest DERP:", StringComparison.OrdinalIgnoreCase))
            {
                var v = line["Nearest DERP:".Length..].Trim();
                nearestDerp = v.Length > 0 ? v : null;
            }
        }
        return (udp, mappingVaries, nearestDerp);
    }

    private static (string? backendState, string? selfName, string? selfIp, List<PeerDiag> peers) ParseStatus(string statusJson)
    {
        using var doc = JsonDocument.Parse(statusJson);
        var root = doc.RootElement;

        string? backendState = root.TryGetProperty("BackendState", out var bs) && bs.ValueKind == JsonValueKind.String ? bs.GetString() : null;

        string? selfName = null, selfIp = null;
        if (root.TryGetProperty("Self", out var self))
        {
            selfName = StringProp(self, "DNSName")?.TrimEnd('.');
            selfIp = FirstTailscaleIp(self);
        }

        var peers = new List<PeerDiag>();
        if (root.TryGetProperty("Peer", out var peerMap) && peerMap.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in peerMap.EnumerateObject())
            {
                var node = kv.Value;
                var curAddr = StringProp(node, "CurAddr");   // set when a direct path is up
                var relay = StringProp(node, "Relay");        // DERP region code, e.g. "tor"
                peers.Add(new PeerDiag
                {
                    Name = (StringProp(node, "DNSName") ?? StringProp(node, "HostName") ?? "?").TrimEnd('.'),
                    TailscaleIp = FirstTailscaleIp(node),
                    Os = StringProp(node, "OS"),
                    Online = !(node.TryGetProperty("Online", out var on) && on.ValueKind == JsonValueKind.False),
                    // status snapshot as a fallback before we ping: direct if CurAddr is populated.
                    Path = !string.IsNullOrEmpty(curAddr) ? curAddr : (!string.IsNullOrEmpty(relay) ? $"DERP({relay})" : null),
                });
            }
        }
        return (backendState, selfName, selfIp, peers);
    }

    private static string? StringProp(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string? FirstTailscaleIp(JsonElement node)
    {
        if (node.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array)
            foreach (var ip in ips.EnumerateArray())
                if (ip.ValueKind == JsonValueKind.String)
                {
                    var s = ip.GetString();
                    if (!string.IsNullOrEmpty(s) && s.Contains('.')) return s; // prefer IPv4 100.x
                }
        return null;
    }
}
