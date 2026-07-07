using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.HostedAi;

/// <summary>
/// The shared pre-flight "can this user use hosted AI right now?" check (issue #938, epic #937). It
/// returns ONE typed <see cref="HostedAiState"/> that every voice/Wingman/TTS surface consults before it
/// lets the user record or fires a hosted call - so a feature with no way to pay is marked unavailable
/// up front with the consistent message (<see cref="HostedAiMessages.For"/>), instead of failing badly
/// after the user has already spoken.
///
/// All AI is DevThrottle-hosted, so readiness is purely the account balance. Pure of I/O plumbing: the
/// account balance read is injected as a delegate, so this class is fully unit-testable and the Gateway
/// owns the wiring (<c>GatewayHostedAi.CreateReadiness</c>). The balance delegate is invoked FRESH on
/// every <see cref="CheckAsync"/> (this class holds no cache), so adding $5 unlocks the feature on the
/// next check with no restart - the acceptance criterion the whole gate exists for.
/// </summary>
public sealed class HostedAiReadiness
{
    private readonly Func<CancellationToken, Task<long?>> _balanceMicrosProvider;

    /// <param name="balanceMicrosProvider">Reads the signed-in account's balance in micro-dollars, fresh
    /// per check. Returns null when the balance is UNKNOWN (signed out or the cloud is unreachable): the
    /// pre-flight check must not block on an unknown balance - the authoritative gate is the runtime 402
    /// (<see cref="HostedAiErrorMapper"/>), which reports the identical state.</param>
    public HostedAiReadiness(Func<CancellationToken, Task<long?>> balanceMicrosProvider)
    {
        _balanceMicrosProvider = balanceMicrosProvider ?? throw new ArgumentNullException(nameof(balanceMicrosProvider));
    }

    /// <summary>
    /// Decide whether hosted AI can run right now: <see cref="HostedAiState.NeedsCredits"/> when the
    /// balance is a known value at or below zero, else <see cref="HostedAiState.Ready"/> (including when
    /// the balance is unknown - see <c>balanceMicrosProvider</c>). The monthly-cap state is generally
    /// only known at runtime from the 402, so it is not decided here.
    /// </summary>
    public async Task<HostedAiState> CheckAsync(CancellationToken ct = default)
    {
        var balance = await _balanceMicrosProvider(ct);
        var result = balance is long b && b <= 0 ? HostedAiState.NeedsCredits : HostedAiState.Ready;
        FileLog.Write($"[HostedAiReadiness] CheckAsync: balanceMicros={(balance is null ? "unknown" : balance.ToString())} -> {result}");
        return result;
    }
}
