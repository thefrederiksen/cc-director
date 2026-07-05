using CcDirector.Core.Account;
using CcDirector.Core.Configuration;

namespace CcDirector.Core.HostedAi;

/// <summary>
/// The Director/desktop-side hosted-AI readiness check (issue #940, epic #937). The desktop app runs
/// transcription and text-to-speech in-process, so - unlike the Gateway - it must gather the readiness
/// inputs itself: the mode from local config, the bring-your-own key through the same resolver its
/// voice surfaces already use, and the account balance over HTTP from the Gateway (the account token is
/// Gateway-only, so the balance cannot be read locally).
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
    private readonly Func<CancellationToken, Task<string?>> _byoKeyProvider;
    private readonly Func<CancellationToken, Task<long?>> _balanceMicrosProvider;

    /// <param name="modeProvider">Supplies the current mode (local config, read fresh per check).</param>
    /// <param name="byoKeyProvider">Resolves the bring-your-own OpenAI key (local vault or the Gateway),
    /// returning null/empty when none is set. Only consulted in bring-your-own mode.</param>
    /// <param name="balanceMicrosProvider">Reads the account balance in micro-dollars over HTTP, null when
    /// unknown (signed out / unreachable - do not block). Only consulted in DevThrottle mode.</param>
    public DirectorHostedAiReadiness(
        Func<TranscriptionMode> modeProvider,
        Func<CancellationToken, Task<string?>> byoKeyProvider,
        Func<CancellationToken, Task<long?>> balanceMicrosProvider)
    {
        _modeProvider = modeProvider ?? throw new ArgumentNullException(nameof(modeProvider));
        _byoKeyProvider = byoKeyProvider ?? throw new ArgumentNullException(nameof(byoKeyProvider));
        _balanceMicrosProvider = balanceMicrosProvider ?? throw new ArgumentNullException(nameof(balanceMicrosProvider));
    }

    /// <summary>
    /// Wire the real desktop plumbing: the mode from <see cref="TranscriptionModeConfig"/>, the key from
    /// the shared <see cref="OpenAiKeyResolver"/> (the same one the voice surfaces use), and the balance
    /// from <see cref="GatewayAccountCreditsClient"/> against the configured Gateway.
    /// </summary>
    public static DirectorHostedAiReadiness Create(
        OpenAiKeyResolver keyResolver,
        GatewayAccountCreditsClient creditsClient,
        Func<GatewayConfig>? gatewayProvider = null,
        Func<TranscriptionMode>? modeProvider = null)
    {
        if (keyResolver is null) throw new ArgumentNullException(nameof(keyResolver));
        if (creditsClient is null) throw new ArgumentNullException(nameof(creditsClient));
        var gateway = gatewayProvider ?? GatewayConfig.Load;

        return new DirectorHostedAiReadiness(
            modeProvider ?? TranscriptionModeConfig.Get,
            ct => keyResolver.ResolveAsync(ct),
            async ct => (await creditsClient.GetCreditsAsync(gateway(), ct)).BalanceMicros);
    }

    /// <summary>
    /// Resolve whether hosted AI can run for the configured mode right now. Gathers the mode, and only
    /// the input the mode needs (the key in bring-your-own mode, the balance in DevThrottle mode), then
    /// defers the decision to the shared <see cref="HostedAiReadiness"/>.
    /// </summary>
    public async Task<HostedAiState> CheckAsync(CancellationToken ct = default)
    {
        var mode = _modeProvider();
        var key = mode == TranscriptionMode.Byo ? await _byoKeyProvider(ct).ConfigureAwait(false) : null;
        var balance = mode == TranscriptionMode.DevThrottle ? await _balanceMicrosProvider(ct).ConfigureAwait(false) : (long?)null;

        var readiness = new HostedAiReadiness(() => mode, _ => key, _ => Task.FromResult(balance));
        return await readiness.CheckAsync(ct).ConfigureAwait(false);
    }
}
