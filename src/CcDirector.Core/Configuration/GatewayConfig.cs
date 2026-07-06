using System.Net;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Optional configuration for the Director's HTTP registration with a CC Director
/// Gateway. Read from <c>%LOCALAPPDATA%\cc-director\config\config.json</c> under
/// the <c>gateway</c> block:
///
/// <code>
/// {
///   "gateway": {
///     "url": "http://gateway.tailnet.example:7878",
///     "token": "...",
///     "tailnetEndpoint": "http://machine-b.tailnet.example:7879"
///   }
/// }
/// </code>
///
/// If <c>url</c> is missing or empty, the Director runs in local-only mode and
/// no HTTP registration happens. Same-machine Gateways on the box can still
/// discover the Director via the filesystem-watch path.
/// </summary>
public sealed class GatewayConfig
{
    /// <summary>Gateway base URL, e.g. <c>http://gateway.tailnet.example:7878</c>.</summary>
    public string Url { get; init; } = "";

    /// <summary>
    /// Bearer token the Director/launcher present to the Gateway. Normally the enrolled per-device key
    /// or shared fleet token from config.json's <c>gateway.token</c>. When that is empty AND the
    /// configured Gateway is THIS machine's own Gateway, <see cref="Load"/> resolves it to the local
    /// shared machine token from <c>gateway-token.txt</c> (see <see cref="IsLocalGatewayHost"/>), so a
    /// Gateway-role install - which never runs the pairing step that would write a token - still
    /// authenticates its own Director. Empty means no auth header is sent.
    /// </summary>
    public string Token { get; init; } = "";

    /// <summary>
    /// Optional override for the Director's own routable URL. If unset, the
    /// <see cref="GatewayClient"/> falls back to <c>http://{MachineName}:{port}</c>.
    /// </summary>
    public string? TailnetEndpoint { get; init; }

    /// <summary>
    /// The fleet network addressing mode (issue #457). Read from the top-level
    /// <c>addressing_mode</c> key in config.json (NOT the gateway block), so it is the same
    /// value <see cref="AddressingModeConfig"/> exposes. Decides whether a Director advertises
    /// its Tailscale front door or its LAN IP. Default <see cref="AddressingMode.Tailscale"/>.
    /// </summary>
    public AddressingMode AddressingMode { get; init; } = AddressingModeConfig.Default;

    /// <summary>True when <see cref="Url"/> is configured.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(Url);

    /// <summary>
    /// Read the gateway block from <c>config.json</c>. Returns a disabled config
    /// (IsEnabled = false) when the file is missing, malformed, or has no gateway block.
    /// </summary>
    public static GatewayConfig Load()
    {
        var path = CcStorage.ConfigJson();
        try
        {
            if (!File.Exists(path)) return new GatewayConfig();
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new GatewayConfig();

            using var doc = JsonDocument.Parse(json);

            // The addressing mode is a TOP-LEVEL key (issue #457), read whether or not a
            // gateway block is present so a standalone Director still binds per the chosen mode.
            var mode = doc.RootElement.TryGetProperty("addressing_mode", out var am) && am.ValueKind == JsonValueKind.String
                ? AddressingModeExtensions.Parse(am.GetString())
                : AddressingModeConfig.Default;

            if (!doc.RootElement.TryGetProperty("gateway", out var gw))
                return new GatewayConfig { AddressingMode = mode };

            var url = (gw.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "").Trim();
            var token = (gw.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "").Trim();
            var tailnet = gw.TryGetProperty("tailnetEndpoint", out var te) ? te.GetString() : null;

            // Same-machine credential resolution. A Gateway-role install never runs the pairing step
            // (that step, and only that step, writes gateway.token - it is a Workstation-only step), so
            // the Gateway host's own Director/launcher have no configured token and, once host-wide auth
            // is enforced, get 401 on every Gateway call. When the configured Gateway is THIS machine's
            // own Gateway, present the local shared machine token from gateway-token.txt - the exact file
            // GatewayAuth writes on the Gateway host, and the same file DirectorAuth already reads for the
            // inbound Control API. It is scoped to a LOCAL Gateway URL so a remote Workstation never sends
            // this machine's token to a different Gateway. This is a credential RESOLUTION, not a fallback
            // that hides an error: it is the correct token for the local Gateway, sourced from the one
            // file that holds it.
            if (string.IsNullOrEmpty(token) && url.Length > 0 && IsLocalGatewayHost(url))
            {
                var localToken = TryReadLocalMachineToken();
                if (!string.IsNullOrEmpty(localToken))
                {
                    token = localToken;
                    FileLog.Write("[GatewayConfig] gateway.token empty and gateway.url targets this machine's own Gateway; using the local shared machine token from gateway-token.txt");
                }
            }

            return new GatewayConfig
            {
                Url = url,
                Token = token,
                TailnetEndpoint = string.IsNullOrWhiteSpace(tailnet) ? null : tailnet.Trim(),
                AddressingMode = mode,
            };
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConfig] Load FAILED, treating as disabled: {ex.Message}");
            return new GatewayConfig();
        }
    }

    /// <summary>
    /// True when <paramref name="url"/> addresses THIS machine's own Gateway: loopback, "localhost",
    /// this machine's name, or its Tailscale MagicDNS name. Tailscale lowercases the hostname and turns
    /// '_' into '-' (MACHINE_A -> machine-a), so the first DNS label is compared against the
    /// normalized machine name. Used to scope the empty-token same-machine credential resolution to a
    /// local Gateway so a remote Workstation never presents this machine's token to a different Gateway.
    /// Pure and side-effect free so it is unit-tested directly.
    /// </summary>
    internal static bool IsLocalGatewayHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host;
        if (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip))
            return true;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return true;

        // Tailscale MagicDNS: <normalized-hostname>.<tailnet>.ts.net - match the first label.
        var firstLabel = host.Split('.', 2)[0];
        var normalizedMachine = Environment.MachineName.ToLowerInvariant().Replace('_', '-');
        return firstLabel.Equals(normalizedMachine, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Read the local shared machine token that <c>GatewayAuth</c> generates on the Gateway host
    /// (<c>%LOCALAPPDATA%\cc-director\config\director\gateway-token.txt</c> - the same file
    /// <c>GatewayAuth.TokenFile</c> / <c>DirectorAuth.TokenFile</c> / <see cref="GatewayCredentialStore.CredentialFile"/>
    /// name). The path is computed fresh (not a cached static) so it honors the current config root.
    /// Null when the file is absent or empty.
    /// </summary>
    private static string? TryReadLocalMachineToken()
    {
        try
        {
            var path = Path.Combine(CcStorage.Config(), "director", "gateway-token.txt");
            if (!File.Exists(path))
                return null;
            var text = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConfig] TryReadLocalMachineToken failed: {ex.Message}");
            return null;
        }
    }
}
