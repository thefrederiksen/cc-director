using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Account;

/// <summary>
/// Makes DevThrottle-hosted transcription "just work" for a signed-in user with zero configuration
/// (issue #881). After sign-in (and at Gateway startup for an already-signed-in install), it ensures a
/// DevThrottle inference key is present as the hosted-AI credential:
///
///   - If the vault already holds a <c>DEVTHROTTLE_API_KEY</c>, do nothing. That covers BOTH a manual
///     key the user pasted (the manual override wins) AND a previously auto-minted key (reuse across
///     restarts, so we never mint a fresh key on every boot - no key sprawl).
///   - Otherwise, mint one for the signed-in account (the account JWT authenticates the mint) and store
///     it in the vault, where the single transcription owner already reads it.
///
/// Best-effort throughout: it is fired from the best-effort post-sign-in hook and a detached startup
/// task, so any failure (offline, mint rejected) is logged and swallowed - the user simply sees the
/// add-credits / manual-key state until connectivity returns, and sign-in itself never fails.
///
/// The key value is never logged (security rule DT-05).
/// </summary>
public sealed class TranscriptionKeyAutoProvisioner
{
    /// <summary>
    /// The vault entry that records the stable id of an AUTO-MINTED inference key. Its presence marks the
    /// stored <c>DEVTHROTTLE_API_KEY</c> as one this Gateway minted (not a manual key), so sign-out can
    /// revoke it; a manually-pasted key has no id entry and is never revoked on sign-out.
    /// </summary>
    public const string InferenceKeyIdVaultName = "DEVTHROTTLE_API_KEY_ID";

    private readonly KeyVault _vault;
    private readonly Func<string?> _accessTokenProvider;
    private readonly IInferenceKeyMinter _minter;
    private readonly string _label;

    /// <summary>
    /// Serializes provisioning within this process. <see cref="EnsureAsync"/> is fired from BOTH the
    /// post-sign-in hook and a detached startup task, and those race on a fresh sign-in during startup.
    /// Without this gate the check-then-mint window lets every concurrent caller find an empty vault and
    /// mint its own key, so one Gateway leaks several live keys in the same second (issue #1136). The gate
    /// plus a re-read of the vault after acquiring it collapse the concurrent callers to exactly one mint.
    /// Never disposed: the provisioner lives for the whole process and this semaphore allocates no
    /// unmanaged handle unless its wait handle is used (it is not).
    /// </summary>
    private readonly SemaphoreSlim _ensureGate = new(1, 1);

    /// <param name="vault">The Gateway key vault - where the transcription owner reads the hosted key.</param>
    /// <param name="accessTokenProvider">Supplies the signed-in account JWT (or null when not signed in);
    /// invoked fresh each call so it always reflects the current credential.</param>
    /// <param name="minter">Mints an inference key for the account.</param>
    /// <param name="label">A recognisable name for the minted key (defaults to the machine name).</param>
    public TranscriptionKeyAutoProvisioner(
        KeyVault vault, Func<string?> accessTokenProvider, IInferenceKeyMinter minter, string? label = null)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _accessTokenProvider = accessTokenProvider ?? throw new ArgumentNullException(nameof(accessTokenProvider));
        _minter = minter ?? throw new ArgumentNullException(nameof(minter));
        _label = string.IsNullOrWhiteSpace(label) ? $"cc-director {Environment.MachineName}" : label!;
    }

    /// <summary>
    /// Ensures a DevThrottle inference key is present as the hosted transcription credential. Returns
    /// true only when this call MINTED and stored a new key; false when nothing was needed (a key was
    /// already present) or nothing could be done (not signed in, or the mint did not return a key).
    /// </summary>
    public async Task<bool> EnsureAsync(CancellationToken ct = default)
    {
        // Serialize the whole check-then-mint sequence so concurrent callers (the sign-in hook and the
        // startup task) mint at most one key between them (issue #1136).
        await _ensureGate.WaitAsync(ct);
        try
        {
            var existing = _vault.Get(TranscriptionEndpointResolver.DevThrottleKeyName);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                FileLog.Write("[TranscriptionKeyAutoProvisioner] EnsureAsync: a DevThrottle key is already stored -> nothing to mint");
                return false;
            }

            var accessToken = _accessTokenProvider();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                FileLog.Write("[TranscriptionKeyAutoProvisioner] EnsureAsync: not signed in (no access token) -> cannot mint yet");
                return false;
            }

            FileLog.Write("[TranscriptionKeyAutoProvisioner] EnsureAsync: no DevThrottle key stored and signed in -> minting an inference key");
            var minted = await _minter.MintAsync(accessToken, _label, ct);
            if (minted is null || string.IsNullOrWhiteSpace(minted.Key))
            {
                FileLog.Write("[TranscriptionKeyAutoProvisioner] EnsureAsync: mint returned no key -> leaving unset (user sees the add-credits/manual state)");
                return false;
            }

            // SetIfAbsent (not Set) guards a race with a concurrent manual save or another Gateway process
            // sharing this account - the first writer wins and we never clobber a key that appeared meanwhile.
            var stored = _vault.SetIfAbsent(TranscriptionEndpointResolver.DevThrottleKeyName, minted.Key);
            if (stored)
            {
                if (!string.IsNullOrWhiteSpace(minted.Id))
                    // Record the id so sign-out can revoke THIS auto-minted key (a manual key has no id and
                    // is never revoked). Set (not SetIfAbsent) because the id belongs to the key we stored.
                    _vault.Set(InferenceKeyIdVaultName, minted.Id!);
                FileLog.Write($"[TranscriptionKeyAutoProvisioner] EnsureAsync: minted inference key stored, id recorded={!string.IsNullOrWhiteSpace(minted.Id)}");
                return true;
            }

            // A key appeared between our re-read and the store (a concurrent manual save, or another Gateway
            // process on the same account). We hold a freshly minted key that nothing will use, so revoke it
            // now rather than leave it as a live orphan on the account (issue #1136). Best-effort.
            FileLog.Write("[TranscriptionKeyAutoProvisioner] EnsureAsync: a key appeared first -> revoking the key we just minted to avoid an orphan");
            if (!string.IsNullOrWhiteSpace(minted.Id))
                await _minter.RevokeAsync(accessToken, minted.Id!, ct);
            return false;
        }
        finally
        {
            _ensureGate.Release();
        }
    }

    /// <summary>
    /// On sign-out, revokes the AUTO-MINTED inference key (if any) and clears it from the vault, so a
    /// signed-out install leaves no live key behind. A MANUALLY-pasted key (no recorded id) is left
    /// untouched - it is the user's own key, not ours to revoke. Best-effort: any failure is logged and
    /// swallowed so sign-out never fails; call it BEFORE the credential is cleared (it needs the JWT).
    /// The local key is cleared ONLY when the cloud revoke actually succeeded (or the key was already
    /// gone). If the revoke could not be attempted or failed, the local key is KEPT so the next start
    /// reuses it rather than minting a replacement and orphaning the still-live cloud key (issue #1136).
    /// Returns true only when an auto-minted key was revoked and cleared.
    /// </summary>
    public async Task<bool> RevokeMintedKeyAsync(CancellationToken ct = default)
    {
        var keyId = _vault.Get(InferenceKeyIdVaultName);
        if (string.IsNullOrWhiteSpace(keyId))
        {
            FileLog.Write("[TranscriptionKeyAutoProvisioner] RevokeMintedKeyAsync: no auto-minted key id -> nothing to revoke (manual key left untouched)");
            return false;
        }

        var accessToken = _accessTokenProvider();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            // No token to authenticate the revoke. Do NOT clear the local key: clearing it would make the
            // next sign-in mint a fresh key while this still-live cloud key becomes an unreachable orphan
            // (issue #1136). Keeping it means the next start reuses this same key and no orphan is created;
            // a later sign-out (with a token) can still revoke it.
            FileLog.Write("[TranscriptionKeyAutoProvisioner] RevokeMintedKeyAsync: no access token to revoke with -> keeping the local key so the next start reuses it (no orphan)");
            return false;
        }

        var revoked = await _minter.RevokeAsync(accessToken, keyId!, ct);
        if (!revoked)
        {
            // The cloud revoke failed and the key is still live. Clearing the local copy now would orphan
            // it (the next sign-in mints a replacement). Keep both local entries so the next start reuses
            // the same key instead of minting a new one (issue #1136).
            FileLog.Write("[TranscriptionKeyAutoProvisioner] RevokeMintedKeyAsync: cloud revoke failed -> keeping the local key so the next start reuses it (no orphan)");
            return false;
        }

        // The revoke succeeded (RevokeAsync also reports success for an already-gone 404): the key is no
        // longer live, so clear the local copies and a signed-out install holds no key.
        _vault.Delete(TranscriptionEndpointResolver.DevThrottleKeyName);
        _vault.Delete(InferenceKeyIdVaultName);
        FileLog.Write("[TranscriptionKeyAutoProvisioner] RevokeMintedKeyAsync: cloud revoke succeeded -> cleared the local key and id");
        return true;
    }
}
