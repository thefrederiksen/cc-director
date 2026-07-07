using CcDirector.Core.Account;
using CcDirector.Core.Configuration;

namespace CcDirector.Core.HostedAi;

/// <summary>
/// The Director/desktop-side hosted-AI readiness check (issue #940, epic #937). The desktop app runs
/// transcription and text-to-speech in-process, so - unlike the Gateway - it must gather the readiness
/// input itself: the account balance over HTTP from the Gateway (the account token is Gateway-only, so
/// the balance cannot be read locally).
///
/// It does NOT re-implement the decision - it gathers the balance (async I/O) and feeds it to the one
/// shared, unit-tested <see cref="HostedAiReadiness"/>, so the desktop resolves the identical
/// <see cref="HostedAiState"/> the Gateway does. An unknown balance (signed out / Gateway unreachable)
/// does NOT block - the runtime 402 stays the authoritative gate - matching the foundation's contract.
/// </summary>
public sealed class DirectorHostedAiReadiness
{
    private readonly Func<CancellationToken, Task<long?>> _balanceMicrosProvider;

    /// <param name="balanceMicrosProvider">Reads the account balance in micro-dollars over HTTP, null when
    /// unknown (signed out / unreachable - do not block).</param>
    public DirectorHostedAiReadiness(Func<CancellationToken, Task<long?>> balanceMicrosProvider)
    {
        _balanceMicrosProvider = balanceMicrosProvider ?? throw new ArgumentNullException(nameof(balanceMicrosProvider));
    }

    /// <summary>
    /// Wire the real desktop plumbing: the balance from <see cref="GatewayAccountCreditsClient"/>
    /// against the configured Gateway.
    /// </summary>
    public static DirectorHostedAiReadiness Create(
        GatewayAccountCreditsClient creditsClient,
        Func<GatewayConfig>? gatewayProvider = null)
    {
        if (creditsClient is null) throw new ArgumentNullException(nameof(creditsClient));
        var gateway = gatewayProvider ?? GatewayConfig.Load;

        return new DirectorHostedAiReadiness(
            async ct => (await creditsClient.GetCreditsAsync(gateway(), ct)).BalanceMicros);
    }

    /// <summary>
    /// Resolve whether hosted AI can run right now. Gathers the account balance, then defers the
    /// decision to the shared <see cref="HostedAiReadiness"/>.
    /// </summary>
    public async Task<HostedAiState> CheckAsync(CancellationToken ct = default)
    {
        var balance = await _balanceMicrosProvider(ct).ConfigureAwait(false);
        var readiness = new HostedAiReadiness(_ => Task.FromResult(balance));
        return await readiness.CheckAsync(ct).ConfigureAwait(false);
    }
}
