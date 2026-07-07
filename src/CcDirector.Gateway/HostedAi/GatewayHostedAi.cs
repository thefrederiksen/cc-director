using CcDirector.Core;
using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.HostedAi;

/// <summary>
/// Wires the shared, delegate-injected <see cref="HostedAiReadiness"/> check (Core, issue #938) to the
/// Gateway's real resources: the key vault (the bring-your-own OpenAI key), the account credential
/// (<see cref="DevThrottleAccountService"/>), and the credits client (<see cref="AccountCreditsClient"/>).
/// The Gateway is the only place that holds the account token and the vault, so the wiring lives here
/// while the decision logic stays pure in Core. This is the factory the per-platform sub-issues
/// (#939-#943) call to obtain a ready-to-use check.
/// </summary>
public static class GatewayHostedAi
{
    /// <summary>
    /// Build a <see cref="HostedAiReadiness"/> that reads the DevThrottle account balance through
    /// <paramref name="credits"/> using the token <paramref name="account"/> holds.
    /// </summary>
    /// <param name="account">The Gateway-hosted account credential service, or null on a host without one
    /// (then the balance is always unknown and readiness reports Ready - the runtime 402 gates it).</param>
    /// <param name="credits">The cloud credits client (the injectable cloud-egress seam).</param>
    public static HostedAiReadiness CreateReadiness(
        DevThrottleAccountService? account,
        AccountCreditsClient credits)
    {
        if (credits is null) throw new ArgumentNullException(nameof(credits));

        return new HostedAiReadiness(
            ct => ReadBalanceMicrosAsync(account, credits, ct));
    }

    /// <summary>
    /// Read the account balance in micro-dollars for the pre-flight check, or null when it is UNKNOWN.
    /// Signed out (no token) is unknown; a cloud failure is an expected external condition mapped to
    /// unknown here at the egress boundary - the pre-flight check must never block on a balance it could
    /// not read (the runtime 402 remains the authoritative, identically-reported gate). The raw token is
    /// never logged (DT-05).
    /// </summary>
    private static async Task<long?> ReadBalanceMicrosAsync(
        DevThrottleAccountService? account, AccountCreditsClient credits, CancellationToken ct)
    {
        var token = account?.GetAccessTokenForForwarding();
        if (string.IsNullOrEmpty(token))
        {
            FileLog.Write("[GatewayHostedAi] ReadBalanceMicros: no account credential -> balance unknown");
            return null;
        }

        try
        {
            var result = await credits.GetCreditsAsync(token, ct).ConfigureAwait(false);
            FileLog.Write($"[GatewayHostedAi] ReadBalanceMicros: balanceMicros={result.BalanceMicros}");
            return result.BalanceMicros;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayHostedAi] ReadBalanceMicros FAILED (balance unknown, not blocking): {ex.Message}");
            return null;
        }
    }
}
