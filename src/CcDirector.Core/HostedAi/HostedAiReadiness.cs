using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.HostedAi;

/// <summary>
/// The shared pre-flight "can this user use hosted AI right now?" check (issue #938, epic #937).
///
/// SINCE THE INCLUDED AI MISSION (issue #1360) this check ALWAYS answers
/// <see cref="HostedAiState.Ready"/> in DevThrottle mode and performs NO balance read. It used to
/// block recording when the account balance was at or below zero - but the internal AI features
/// (transcription, voice, wingman, text-to-speech) are now INCLUDED with an entitled account and
/// never billed to credits, so a zero balance says nothing about whether the server will serve the
/// call. A client-side balance gate would have blocked exactly the members the mission exists to
/// serve (the acceptance test is a ZERO-balance trial account completing a dictation round-trip).
/// The runtime 402, mapped by <see cref="HostedAiErrorMapper"/>, is the ONLY gate - the Architect's
/// ruling on the phase-2 question Q2, recorded in the mission record's surfaces.md.
///
/// The class and its call sites are kept (rather than deleted) so every surface still funnels through
/// one pre-flight seam - the next condition that genuinely CAN be known up front has a home.
/// </summary>
public sealed class HostedAiReadiness
{
    private readonly Func<TranscriptionMode> _modeProvider;

    /// <param name="modeProvider">Supplies the current transcription/provider mode, read fresh per check
    /// so a mode change takes effect with no restart.</param>
    /// <param name="keyProvider">Legacy constructor parameter retained for compatibility; ignored.</param>
    /// <param name="balanceMicrosProvider">Legacy constructor parameter retained for compatibility;
    /// NEVER invoked (issue #1360 retired the client-side balance gate - see the class summary).</param>
    public HostedAiReadiness(
        Func<TranscriptionMode> modeProvider,
        Func<string, string?> keyProvider,
        Func<CancellationToken, Task<long?>> balanceMicrosProvider)
    {
        _modeProvider = modeProvider ?? throw new ArgumentNullException(nameof(modeProvider));
        _ = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _ = balanceMicrosProvider ?? throw new ArgumentNullException(nameof(balanceMicrosProvider));
    }

    /// <summary>
    /// Always <see cref="HostedAiState.Ready"/> in DevThrottle mode: the included features are gated by
    /// the server's runtime 402, never by a client-side balance read (issue #1360 - see the class summary).
    /// </summary>
    public Task<HostedAiState> CheckAsync(CancellationToken ct = default)
    {
        _ = _modeProvider();
        FileLog.Write("[HostedAiReadiness] CheckAsync: mode=devthrottle -> Ready (no pre-flight balance gate; the runtime 402 rules)");
        return Task.FromResult(HostedAiState.Ready);
    }
}
