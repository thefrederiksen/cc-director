# #881 refinement: revoke-on-sign-out live proof (2026-07-02)

Signing out revokes the auto-minted inference key so a signed-out install leaves no live key behind.

## Live cycle (production)
1. **Mint** `POST /api/v1/keys` (account JWT) -> **201**; the key id is at `data.record.id` (exactly what
   `AccountInferenceKeyProvisioner.ExtractKeyId` reads).
2. **Revoke** `DELETE /api/v1/keys/{data.record.id}` -> **200**.
3. **Confirm dead** transcribe with the revoked key -> **401** (the key no longer authenticates inference).

No leftover keys (the cleanup delete of the prior run's id returned 404 = already gone).

## Code
- `IInferenceKeyMinter.MintAsync` now returns `MintedInferenceKey { Key, Id }`; new `RevokeAsync(jwt, id)`.
- `TranscriptionKeyAutoProvisioner` records the id in the vault entry `DEVTHROTTLE_API_KEY_ID` when it
  mints, and `RevokeMintedKeyAsync` revokes THAT key on sign-out and clears both vault entries. A
  MANUALLY-pasted key has no id and is left untouched (never revoked - it is the user's key).
- `POST /account/logout` runs the revoke as a best-effort pre-clear hook (the revoke needs the still-present JWT).

## Tests
22 unit tests (id extraction incl. the real `data.record.id` shape, mint returns key+id, revoke clears
both entries, and a manual key is left untouched).

## Deferred (still fast-follow)
Capturing the `dtd_` device key into the DPAPI blob and moving the minted key/id into the blob (vs the
vault, which already holds the manual key in plaintext) - both ripple into the `DevThrottleTokens` record
and its token-refresh preservation path; kept out of this increment to avoid risking the working
auto-wiring on token renewal. The id is stored in the vault alongside the key it revokes instead.
