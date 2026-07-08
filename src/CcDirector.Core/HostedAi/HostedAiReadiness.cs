using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.HostedAi;

/// <summary>
/// The shared pre-flight "can this user use hosted AI right now?" check (issue #938, epic #937). Hung
/// on the existing centralized routing path (<see cref="TranscriptionMode"/> +
/// <see cref="TranscriptionEndpointResolver"/>), it returns ONE typed <see cref="HostedAiState"/> that
/// every voice/Wingman/TTS surface consults before it lets the user record or fires a hosted call - so
/// a feature with no way to pay is marked unavailable up front with the consistent message
/// (<see cref="HostedAiMessages.For"/>), instead of failing badly after the user has already spoken.
///
/// Pure of I/O plumbing: the mode and account balance read are injected as delegates, so this class is
/// fully unit-testable and the Gateway owns the wiring
/// (<c>GatewayHostedAi.CreateReadiness</c>). The balance delegate is invoked FRESH on every
/// <see cref="CheckAsync"/> (this class holds no cache), so adding $5 unlocks the feature on the next
/// check with no restart - the acceptance criterion the whole gate exists for.
/// </summary>
public sealed class HostedAiReadiness
{
    private readonly Func<TranscriptionMode> _modeProvider;
    private readonly Func<CancellationToken, Task<long?>> _balanceMicrosProvider;

    /// <param name="modeProvider">Supplies the current transcription/provider mode, read fresh per check
    /// so a mode change takes effect with no restart.</param>
    /// <param name="keyProvider">Legacy constructor parameter retained for compatibility; ignored.</param>
    /// <param name="balanceMicrosProvider">Reads the signed-in account's balance in micro-dollars, fresh
    /// per check. Returns null when the balance is UNKNOWN (signed out or the cloud is unreachable): the
    /// pre-flight check must not block on an unknown balance - the authoritative gate is the runtime 402
    /// (<see cref="HostedAiErrorMapper"/>), which reports the identical state.</param>
    public HostedAiReadiness(
        Func<TranscriptionMode> modeProvider,
        Func<string, string?> keyProvider,
        Func<CancellationToken, Task<long?>> balanceMicrosProvider)
    {
        _modeProvider = modeProvider ?? throw new ArgumentNullException(nameof(modeProvider));
        _ = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _balanceMicrosProvider = balanceMicrosProvider ?? throw new ArgumentNullException(nameof(balanceMicrosProvider));
    }

    /// <summary>
    /// Decide whether hosted AI can run for the configured mode right now:
    /// <list type="bullet">
    /// <item>DevThrottle hosted -> <see cref="HostedAiState.NeedsCredits"/> when the balance is a known
    /// value at or below zero, else <see cref="HostedAiState.Ready"/> (including when the balance is
    /// unknown - see <c>balanceMicrosProvider</c>). The monthly-cap state is generally only known at
    /// runtime from the 402, so it is not decided here.</item>
    /// </list>
    /// </summary>
    public async Task<HostedAiState> CheckAsync(CancellationToken ct = default)
    {
        _ = _modeProvider();

        var balance = await _balanceMicrosProvider(ct);
        var result = balance is long b && b <= 0 ? HostedAiState.NeedsCredits : HostedAiState.Ready;
        FileLog.Write($"[HostedAiReadiness] CheckAsync: mode=devthrottle, balanceMicros={(balance is null ? "unknown" : balance.ToString())} -> {result}");
        return result;
    }
}
