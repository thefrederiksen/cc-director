using System.Collections.Concurrent;
using System.Net;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway-owned state for the connection-status pill. A newly observed relay is a normal Tailscale
/// warm-up, so the first observation is amber; a relay that remains on the next poll is red. Any direct or
/// indeterminate observation clears the sequence. Hosted requests bypass this fold completely.
/// </summary>
internal sealed class NetworkConnectionVerdictFold
{
    internal const int RelayObservationsBeforeSlow = 2;

    private readonly ConcurrentDictionary<string, int> _relayObservations = new(StringComparer.Ordinal);

    internal TailscaleDiagnostics.NetworkDiag Fold(
        TailscaleDiagnostics.NetworkDiag diagnostic, IPAddress? clientAddress)
    {
        var key = TailscaleDiagnostics.NormalizeAddress(clientAddress);
        var consecutiveRelayObservations = 0;
        if (key is not null && TailscaleDiagnostics.IsRelayObservation(diagnostic, clientAddress))
        {
            consecutiveRelayObservations = _relayObservations.AddOrUpdate(
                key,
                addValue: 1,
                (_, prior) => Math.Min(prior + 1, RelayObservationsBeforeSlow));
        }
        else if (key is not null)
        {
            _relayObservations.TryRemove(key, out _);
        }

        return TailscaleDiagnostics.WithConnectionVerdict(
            diagnostic, clientAddress, consecutiveRelayObservations);
    }
}
