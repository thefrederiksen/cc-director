using CcDirector.Core;
using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.HostedAi;

/// <summary>
/// Wires the shared, delegate-injected <see cref="HostedAiReadiness"/> check (Core, issue #938) to the
/// Gateway's resources. Since the Included AI mission (issue #1360) the check consults NO account
/// balance - the included features are gated by the runtime 402 only - so the wiring is just the vault
/// key reader and the mode; the account and credits parameters are kept so call sites compile
/// unchanged, and are no longer used.
/// </summary>
public static class GatewayHostedAi
{
    /// <summary>
    /// Build the pre-flight <see cref="HostedAiReadiness"/>. No balance read is wired (issue #1360
    /// retired the client-side balance gate; the runtime 402 rules).
    /// </summary>
    /// <param name="vault">The Gateway key vault.</param>
    /// <param name="account">Retained for call-site compatibility; no longer read.</param>
    /// <param name="credits">Retained for call-site compatibility; no longer called.</param>
    /// <param name="modeProvider">Supplies the current transcription mode; defaults to
    /// <see cref="TranscriptionModeConfig.Get"/> so a Cockpit mode change takes effect with no restart.</param>
    public static HostedAiReadiness CreateReadiness(
        KeyVault vault,
        DevThrottleAccountService? account,
        AccountCreditsClient credits,
        Func<TranscriptionMode>? modeProvider = null)
    {
        if (vault is null) throw new ArgumentNullException(nameof(vault));
        if (credits is null) throw new ArgumentNullException(nameof(credits));

        return new HostedAiReadiness(
            modeProvider ?? TranscriptionModeConfig.Get,
            name => vault.Get(name),
            _ => Task.FromResult<long?>(null));
    }
}
