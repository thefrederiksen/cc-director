using CcDirector.Core.Account;
using CcDirector.Core.Configuration;

namespace CcDirector.Core.HostedAi;

/// <summary>
/// The Director/desktop-side hosted-AI readiness check (issue #940, epic #937). The desktop app runs
/// transcription and text-to-speech in-process, so - unlike the Gateway - it must gather the readiness
/// inputs itself: the mode from local config and the account balance over HTTP from the Gateway (the
/// account token is Gateway-only, so the balance cannot be read locally).
///
/// It does NOT re-implement the decision - it gathers the three inputs (async: the key and balance are
/// I/O) and feeds them to the one shared, unit-tested <see cref="HostedAiReadiness"/>, so the desktop
/// resolves the identical <see cref="HostedAiState"/> the Gateway does. An unknown balance (signed out /
/// Gateway unreachable) does NOT block - the runtime 402 stays the authoritative gate - matching the
/// foundation's contract.
/// </summary>
public sealed class DirectorHostedAiReadiness
{
    private readonly Func<TranscriptionMode> _modeProvider;
    private readonly Func<CancellationToken, Task<long?>> _balanceMicrosProvider;

    /// <param name="modeProvider">Supplies the current mode (local config, read fresh per check).</param>
    /// <param name="byoKeyProvider">Legacy constructor parameter retained for compatibility; ignored.</param>
    /// <param name="balanceMicrosProvider">Reads the account balance in micro-dollars over HTTP, null when
    /// unknown (signed out / unreachable - do not block). Only consulted in DevThrottle mode.</param>
    public DirectorHostedAiReadiness(
        Func<TranscriptionMode> modeProvider,
        Func<CancellationToken, Task<string?>> byoKeyProvider,
        Func<CancellationToken, Task<long?>> balanceMicrosProvider)
    {
        _modeProvider = modeProvider ?? throw new ArgumentNullException(nameof(modeProvider));
        _ = byoKeyProvider ?? throw new ArgumentNullException(nameof(byoKeyProvider));
        _balanceMicrosProvider = balanceMicrosProvider ?? throw new ArgumentNullException(nameof(balanceMicrosProvider));
    }

    /// <summary>
    /// Wire the real desktop plumbing: the mode from <see cref="TranscriptionModeConfig"/>, the key from
    /// the balance from <see cref="GatewayAccountCreditsClient"/> against the configured Gateway.
    /// </summary>
    public static DirectorHostedAiReadiness Create(
        HostedAiKeyResolver keyResolver,
        GatewayAccountCreditsClient creditsClient,
        Func<GatewayConfig>? gatewayProvider = null,
        Func<TranscriptionMode>? modeProvider = null)
    {
        if (keyResolver is null) throw new ArgumentNullException(nameof(keyResolver));
        if (creditsClient is null) throw new ArgumentNullException(nameof(creditsClient));
        var gateway = gatewayProvider ?? GatewayConfig.Load;

        return new DirectorHostedAiReadiness(
            modeProvider ?? TranscriptionModeConfig.Get,
            _ => Task.FromResult<string?>(null),
            async ct => (await creditsClient.GetCreditsAsync(gateway(), ct)).BalanceMicros);
    }

    /// <summary>
    /// Resolve whether hosted AI can run for the configured mode right now. Gathers the mode and
    /// current account balance, then defers the decision to the shared <see cref="HostedAiReadiness"/>.
    /// </summary>
    public async Task<HostedAiState> CheckAsync(CancellationToken ct = default)
    {
        var mode = _modeProvider();
        var balance = await _balanceMicrosProvider(ct).ConfigureAwait(false);

        var readiness = new HostedAiReadiness(() => mode, _ => null, _ => Task.FromResult(balance));
        return await readiness.CheckAsync(ct).ConfigureAwait(false);
    }
}
