namespace CcDirector.Gateway.Account;

/// <summary>A minted inference key: the <c>dt_</c> value (returned once at mint) plus the key's stable
/// id, which is what revokes it later (issue #881 revoke-on-sign-out).</summary>
/// <param name="Key">The plain <c>dt_</c> inference key. Persisted, never logged.</param>
/// <param name="Id">The key's stable id used to revoke it, or null when the cloud omits it.</param>
public sealed record MintedInferenceKey(string Key, string? Id);

/// <summary>
/// Mints and revokes a DevThrottle inference API key for the signed-in account (issue #881). The account
/// JWT authenticates the account endpoints; the inference endpoints (transcription, text-to-speech, chat)
/// want a <c>dt_live_</c> API key, so after sign-in the Gateway mints one and uses it as the hosted-AI
/// credential, and revokes it on sign-out. The key value is returned by the cloud ONLY at mint time, so
/// the caller must persist what this returns. Abstracted so <see cref="TranscriptionKeyAutoProvisioner"/>
/// is unit-testable without a live cloud call.
/// </summary>
public interface IInferenceKeyMinter
{
    /// <summary>
    /// Mints a new inference key for the account the <paramref name="accessToken"/> (a Supabase JWT)
    /// authenticates. <paramref name="label"/> names the key (e.g. the machine name) so it is
    /// recognisable in the account's key list. Returns the key (value + id) on success, or null on any
    /// expected failure (the caller treats a null as "no key minted" and stays graceful - never throws
    /// through the best-effort sign-in hook).
    /// </summary>
    Task<MintedInferenceKey?> MintAsync(string accessToken, string label, CancellationToken ct = default);

    /// <summary>
    /// Revokes the inference key with <paramref name="keyId"/> (DELETE the account key), authenticated by
    /// the account <paramref name="accessToken"/>. Returns true when revoked (or already gone), false on
    /// an expected failure. Best-effort: never throws through the sign-out path.
    /// </summary>
    Task<bool> RevokeAsync(string accessToken, string keyId, CancellationToken ct = default);
}
