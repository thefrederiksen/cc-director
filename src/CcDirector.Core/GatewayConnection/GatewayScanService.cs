using CcDirector.Core.Network;
using CcDirector.Core.Settings;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.GatewayConnection;

/// <summary>Where a found Gateway lives, in the issue-1233 discovery order (spec section 5, Step 1).</summary>
public enum GatewayLocationKind
{
    /// <summary>A Gateway process on this machine, reached over its own loopback. Tried first - the most direct path.</summary>
    ThisMachine,

    /// <summary>A Gateway reachable over the tailnet by its Tailscale name. The reliable cross-network path.</summary>
    Tailnet,

    /// <summary>A Gateway on the local network by machine name or LAN IP. The last-resort path.</summary>
    LocalNetwork,
}

/// <summary>One Gateway the scan found: the address to connect through, a human label for the pick, and
/// where it lives. Rendered as a one-click pick in Step 1 (spec section 5).</summary>
/// <param name="Url">The base URL to connect through, e.g. <c>http://SOREN_NORTH:7878</c>.</param>
/// <param name="Label">The one-line label the pick shows, e.g. "Gateway on SOREN_NORTH (this machine)".</param>
/// <param name="Kind">Which discovery leg found it.</param>
public sealed record FoundGateway(string Url, string Label, GatewayLocationKind Kind);

/// <summary>
/// The Step 1 automatic scan (spec section 5): look for Gateways in the issue-1233 discovery order - this
/// machine, then the tailnet, then the local network - and return EVERY reachable one as a one-click pick
/// (unlike <see cref="SettingsDetectionService.DetectGatewayAsync"/>, which returns only the first). The
/// scan replaces the retired Detect button: scanning is automatic now (decision 5).
///
/// UI-free so it is reusable across the three panel hosts and unit-testable. The candidate ORDERING is a
/// pure static (<see cref="BuildCandidates"/>) tested directly; <see cref="ScanAsync"/> layers the
/// reachability probe (reusing <see cref="SettingsDetectionService.TestGatewayAsync"/>, the same /healthz
/// gateway check the rest of the Director uses) over that order.
/// </summary>
public sealed class GatewayScanService
{
    private readonly SettingsDetectionService _detection;

    /// <param name="detection">Override the reachability prober (tests inject a stub); defaults to the shared one.</param>
    public GatewayScanService(SettingsDetectionService? detection = null) => _detection = detection ?? new SettingsDetectionService();

    /// <summary>
    /// Build the ordered candidate list in the issue-1233 discovery order (spec section 5): this machine's
    /// loopback first, then each tailnet host. Pure and side-effect free so the ordering and de-duplication
    /// are unit-tested directly, without a network. Duplicates (case-insensitive URL) are dropped, keeping
    /// the first (higher-priority) occurrence.
    /// </summary>
    /// <param name="machineName">This machine's name, used only for the "this machine" pick label.</param>
    /// <param name="tailnetHosts">Tailnet host names to probe (from <see cref="TailscaleIdentity.ListGatewayHostCandidates"/>).</param>
    /// <param name="gatewayPort">The Gateway port to build candidate URLs on.</param>
    public static IReadOnlyList<FoundGateway> BuildCandidates(
        string machineName,
        IReadOnlyList<string> tailnetHosts,
        int gatewayPort = EndpointProbe.DefaultGatewayPort)
    {
        var list = new List<FoundGateway>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. This machine, over its own loopback. Several loopback spellings map to the same pick; the
        // label is the machine name so the user recognizes it.
        var thisMachineLabel = $"Gateway on {machineName} (this machine)";
        foreach (var url in EndpointProbe.LocalGatewayCandidates(gatewayPort))
            AddUnique(list, seen, new FoundGateway(url, thisMachineLabel, GatewayLocationKind.ThisMachine));

        // 2. The tailnet (each online, non-mobile tailnet host by its Tailscale name).
        foreach (var host in tailnetHosts ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(host)) continue;
            var url = $"http://{host.Trim()}:{gatewayPort}";
            AddUnique(list, seen, new FoundGateway(url, $"Gateway at {host.Trim()}", GatewayLocationKind.Tailnet));
        }

        return list;
    }

    /// <summary>
    /// Scan for Gateways in the issue-1233 discovery order and return every reachable one as a pick. The
    /// candidates are probed concurrently for responsiveness; the result preserves discovery-order priority.
    /// Multiple reachable loopback spellings collapse to a single "this machine" pick. Never throws for an
    /// unreachable candidate - only cancellation propagates.
    /// </summary>
    public async Task<IReadOnlyList<FoundGateway>> ScanAsync(CancellationToken ct = default)
    {
        var machineName = Environment.MachineName;
        var tailnetHosts = await Task.Run(() => TailscaleIdentity.ListGatewayHostCandidates(), ct);
        var candidates = BuildCandidates(machineName, tailnetHosts);

        // Probe all candidates concurrently; Task.WhenAll preserves input order in its results, so the
        // reachable set stays in discovery-order priority.
        var probed = await Task.WhenAll(candidates.Select(async c =>
            (candidate: c, reachable: (await _detection.TestGatewayAsync(c.Url, ct)).Ok)));

        var found = new List<FoundGateway>();
        var thisMachineAdded = false;
        foreach (var (candidate, reachable) in probed)
        {
            if (!reachable) continue;
            // Collapse the several loopback spellings into one "this machine" pick.
            if (candidate.Kind == GatewayLocationKind.ThisMachine)
            {
                if (thisMachineAdded) continue;
                thisMachineAdded = true;
            }
            found.Add(candidate);
        }

        FileLog.Write($"[GatewayScanService] scan complete: {found.Count} reachable of {candidates.Count} candidate(s)");
        return found;
    }

    private static void AddUnique(List<FoundGateway> list, HashSet<string> seen, FoundGateway candidate)
    {
        if (seen.Add(candidate.Url))
            list.Add(candidate);
    }
}
