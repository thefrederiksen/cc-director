using CcDirector.Core.Utilities;

namespace CcDirector.Core.Network;

/// <summary>
/// One attempt to reach a candidate gateway address: the address tried, whether it answered,
/// and - when it did not - a human-readable reason. The full ordered list of attempts is kept
/// so the caller (installer test panel, Director connect log) can show exactly what was tried
/// and why each earlier candidate was skipped.
/// </summary>
public sealed record GatewayEndpointAttempt(string Url, bool Reachable, string? Reason);

/// <summary>
/// The outcome of walking an ordered candidate list: the first address that answered
/// (<see cref="ChosenUrl"/>, null when none did) and every attempt made, in order.
/// </summary>
public sealed record GatewayEndpointSelection(string? ChosenUrl, IReadOnlyList<GatewayEndpointAttempt> Attempts)
{
    /// <summary>True when a reachable gateway address was found.</summary>
    public bool Found => ChosenUrl is not null;
}

/// <summary>
/// Picks the first reachable gateway address from an ordered candidate list (issue #1233).
///
/// A gateway now advertises several ways to be reached (machine name plus port, its Tailscale
/// address when Tailscale is available, and its local network IP plus port). A joining client -
/// the installer connect step and the Director's own registration - is handed that ordered list
/// and must connect through the FIRST candidate that actually answers, in priority order:
///
///   1. machine name (the local-network path, and the most stable name)
///   2. Tailscale (the reliable cross-network path)
///   3. raw IP address (last resort - it can change)
///
/// This walker keeps that policy in ONE place. It probes each candidate in the order given and
/// stops at the first that answers, so callers never invent their own ordering. The reachability
/// probe is injected so the selection logic is unit-tested without a live gateway; production
/// callers pass <see cref="ProbeHealthzAsync"/>, the same GET /healthz check the rest of the
/// Director-to-Gateway client uses.
/// </summary>
public static class GatewayEndpointSelector
{
    /// <summary>
    /// Walk <paramref name="candidates"/> in order and return the first that the
    /// <paramref name="probe"/> reports reachable. The probe returns null when the address
    /// answered, otherwise a reason string. Blank candidates are recorded and skipped, never
    /// probed. Every candidate examined before the winner is recorded in
    /// <see cref="GatewayEndpointSelection.Attempts"/>; candidates AFTER the winner are not
    /// probed (first reachable wins). Returns a selection with a null <c>ChosenUrl</c> when no
    /// candidate answered.
    /// </summary>
    public static async Task<GatewayEndpointSelection> SelectAsync(
        IReadOnlyList<string> candidates,
        Func<string, CancellationToken, Task<string?>> probe,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(probe);

        var attempts = new List<GatewayEndpointAttempt>();
        foreach (var raw in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var url = (raw ?? string.Empty).Trim();
            if (url.Length == 0)
            {
                attempts.Add(new GatewayEndpointAttempt(raw ?? string.Empty, false, "blank candidate address"));
                continue;
            }

            string? reason;
            try
            {
                reason = await probe(url, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A probe that throws is a failed attempt, not a fatal error: record the reason
                // and keep walking so one broken candidate never hides a reachable later one.
                reason = $"probe threw: {ex.Message}";
            }

            attempts.Add(new GatewayEndpointAttempt(url, reason is null, reason));
            if (reason is null)
            {
                FileLog.Write($"[GatewayEndpointSelector] chose {url} (attempt {attempts.Count} of {candidates.Count})");
                return new GatewayEndpointSelection(url, attempts);
            }

            FileLog.Write($"[GatewayEndpointSelector] {url} not reachable: {reason}");
        }

        FileLog.Write($"[GatewayEndpointSelector] no reachable gateway among {attempts.Count} candidate(s)");
        return new GatewayEndpointSelection(null, attempts);
    }

    /// <summary>
    /// The production reachability probe: GET &lt;url&gt;/healthz and treat a 2xx as reachable.
    /// This is the same health check <see cref="GatewayClient.ProbeAdvertisedEndpointAsync"/>
    /// uses for a Director's own endpoint, applied here to a candidate GATEWAY address. Returns
    /// null when the gateway answered, otherwise a reason. Never throws - a transport failure or
    /// timeout becomes a reason string so <see cref="SelectAsync"/> moves to the next candidate.
    /// The caller supplies the <see cref="HttpClient"/> so its timeout governs how long each
    /// candidate is given before we fall through to the next.
    /// </summary>
    public static async Task<string?> ProbeHealthzAsync(string url, HttpClient http, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        try
        {
            using var resp = await http.GetAsync($"{url.TrimEnd('/')}/healthz", ct);
            return resp.IsSuccessStatusCode ? null : $"healthz answered HTTP {(int)resp.StatusCode}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "healthz probe timed out";
        }
        catch (Exception ex)
        {
            return $"healthz probe failed: {ex.Message}";
        }
    }
}
