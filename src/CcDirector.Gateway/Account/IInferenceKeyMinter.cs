namespace CcDirector.Gateway.Account;

/// <summary>
/// Mints a DevThrottle inference API key for the signed-in account (issue #881). The account JWT
/// authenticates the account endpoints; the inference endpoints (transcription, text-to-speech, chat)
/// want a <c>dt_live_</c> API key, so after sign-in the Gateway mints one and uses it as the hosted-AI
/// credential. The key value is returned by the cloud ONLY at mint time, so the caller must persist
/// what this returns. Abstracted so <see cref="TranscriptionKeyAutoProvisioner"/> is unit-testable
/// without a live cloud call.
/// </summary>
public interface IInferenceKeyMinter
{
    /// <summary>
    /// Mints a new inference key for the account the <paramref name="accessToken"/> (a Supabase JWT)
    /// authenticates. <paramref name="label"/> names the key (e.g. the machine name) so it is
    /// recognisable in the account's key list. Returns the <c>dt_</c> key on success, or null on any
    /// expected failure (the caller treats a null as "no key minted" and stays graceful - never throws
    /// through the best-effort sign-in hook).
    /// </summary>
    Task<string?> MintAsync(string accessToken, string label, CancellationToken ct = default);
}
