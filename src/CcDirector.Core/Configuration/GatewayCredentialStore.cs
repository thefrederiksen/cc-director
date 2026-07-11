using System.Text.Json.Nodes;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Writes the credential a device received at enrollment (issue #469) to disk. After a successful
/// pairing the Director holds a unique per-device key; this store persists it to the SAME
/// credential file the Director's Control API and the local cc-* tools both read
/// (<c>%LOCALAPPDATA%\cc-director\config\director\gateway-token.txt</c>), and records the Gateway
/// URL + the key in <c>config.json</c> so the running registration/heartbeat client presents the
/// per-device key as its Bearer.
///
/// One file, two readers (per SECURITY_FLOWS.html): the Director presents the key to the Gateway,
/// and the local agents/CLI present the same key to the loopback Control API.
/// </summary>
public static class GatewayCredentialStore
{
    /// <summary>
    /// The credential file the Director Control API and local cc-* tools read. Kept in lockstep
    /// with <c>DirectorAuth.TokenFile</c> / <c>GatewayAuth.TokenFile</c> (the same path).
    /// </summary>
    public static string CredentialFile { get; } =
        Path.Combine(CcStorage.Config(), "director", "gateway-token.txt");

    /// <summary>
    /// Persist the per-device key issued at enrollment: write it to the local credential file and
    /// record the Gateway URL + key in config.json's gateway block. After this, both the Director's
    /// Gateway client and the local cc-* tools authenticate with the per-device key.
    ///
    /// Connecting to a Gateway also turns the persistent stream ON (<c>streamMode: true</c>): the
    /// stream is how a Director joins the Gateway now (issue #1176), so every enroll/connect path -
    /// the installer's account enroll, the in-app Connect-to-Gateway dialog, and the phone/Cockpit
    /// enroll - lands the same stream-enabled gateway block. <see cref="GatewayConfig.StreamMode"/>
    /// is otherwise opt-in and defaults off, so a Director that never connected is unchanged. The
    /// merge is deep (<see cref="CcDirectorConfigService.MergePatch"/>), so a hand-set
    /// <c>staleAfterSeconds</c> or other gateway keys survive.
    /// </summary>
    public static void SaveEnrolledKey(string gatewayUrl, string deviceKey)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            throw new ArgumentException("gatewayUrl is required", nameof(gatewayUrl));
        if (string.IsNullOrWhiteSpace(deviceKey))
            throw new ArgumentException("deviceKey is required", nameof(deviceKey));

        var dir = Path.GetDirectoryName(CredentialFile);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(CredentialFile, deviceKey);
        FileLog.Write($"[GatewayCredentialStore] Wrote per-device key to {CredentialFile}");

        var patch = new JsonObject
        {
            ["gateway"] = new JsonObject
            {
                ["url"] = gatewayUrl.Trim(),
                ["token"] = deviceKey,
                // The stream is the connection method (issue #1176): enabling it on connect makes a
                // freshly-enrolled Director join over the Gateway's director-stream instead of the
                // legacy pull/heartbeat path. Byte-identical for anyone who never connects.
                ["streamMode"] = true,
            },
        };
        CcDirectorConfigService.MergePatch(patch);
        FileLog.Write($"[GatewayCredentialStore] Recorded gateway url + per-device key + streamMode=true in config.json (url={gatewayUrl})");
    }
}
