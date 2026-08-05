using CcDirector.Core.Account;
using CcDirector.Core.Configuration;

namespace CcDirector.Core.HostedAi;

/// <summary>
/// The Director/desktop-side hosted-AI readiness check (issue #940, epic #937).
///
/// SINCE THE INCLUDED AI MISSION (issue #1360) this performs NO balance read - it defers to the shared
/// <see cref="HostedAiReadiness"/>, which always answers Ready in DevThrottle mode. The internal AI
/// features are included with an entitled account and never billed to credits, so a client-side
/// balance gate would block exactly the members the server would serve; the runtime 402 is the only
/// gate. This also removed the desktop's pre-dictation credit read over HTTP (and the last in-product
/// consumer of the balance for a decision), per the Architect's phase-2 rulings Q1/Q2.
/// </summary>
public sealed class DirectorHostedAiReadiness
{
    private readonly Func<TranscriptionMode> _modeProvider;

    /// <param name="modeProvider">Supplies the current mode (local config, read fresh per check).</param>
    /// <param name="byoKeyProvider">Legacy constructor parameter retained for compatibility; ignored.</param>
    /// <param name="balanceMicrosProvider">Legacy constructor parameter retained for compatibility;
    /// NEVER invoked (issue #1360 retired the client-side balance gate).</param>
    public DirectorHostedAiReadiness(
        Func<TranscriptionMode> modeProvider,
        Func<CancellationToken, Task<string?>> byoKeyProvider,
        Func<CancellationToken, Task<long?>> balanceMicrosProvider)
    {
        _modeProvider = modeProvider ?? throw new ArgumentNullException(nameof(modeProvider));
        _ = byoKeyProvider ?? throw new ArgumentNullException(nameof(byoKeyProvider));
        _ = balanceMicrosProvider ?? throw new ArgumentNullException(nameof(balanceMicrosProvider));
    }

    /// <summary>
    /// Wire the real desktop plumbing: the mode from <see cref="TranscriptionModeConfig"/>. No credits
    /// client and no Gateway call - the balance is not consulted (issue #1360).
    /// </summary>
    public static DirectorHostedAiReadiness Create(
        HostedAiKeyResolver keyResolver,
        Func<TranscriptionMode>? modeProvider = null)
    {
        if (keyResolver is null) throw new ArgumentNullException(nameof(keyResolver));

        return new DirectorHostedAiReadiness(
            modeProvider ?? TranscriptionModeConfig.Get,
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<long?>(null));
    }

    /// <summary>
    /// Resolve whether hosted AI can run for the configured mode right now. Defers the decision to the
    /// shared <see cref="HostedAiReadiness"/> (always Ready in DevThrottle mode - the runtime 402 rules).
    /// </summary>
    public async Task<HostedAiState> CheckAsync(CancellationToken ct = default)
    {
        var mode = _modeProvider();

        var readiness = new HostedAiReadiness(() => mode, _ => null, _ => Task.FromResult<long?>(null));
        return await readiness.CheckAsync(ct).ConfigureAwait(false);
    }
}
