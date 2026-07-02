# #881 auto-wiring live proof: signed-in user -> mint -> transcribe (2026-07-02)

Closes the gap where a fresh signed-in user (no manually-pasted key) could not transcribe.
Proven end-to-end against the production cloud, exactly the chain the new code drives.

## Chain (one process; JWT never left memory, no secrets printed)
1. Decrypted the signed-in account JWT from the DPAPI credential blob
   (`config\gateway\devthrottle-credential.bin`, CurrentUser scope).
2. **Mint** `POST https://devthrottle.com/api/v1/keys` (`Authorization: Bearer <JWT>`, body `{"name":"..."}`)
   -> **HTTP 201**. Response shape `{ data: { key: "<full 51-char dt_live_ key>", record: { masked, prefix, last4, ... } } }`.
3. **Transcribe** `POST /api/v1/audio/transcriptions` (multipart clip + model `whisper-large-v3`) with the minted key
   -> **HTTP 200 `{"text":"Testing 1234."}`**.

## What the code does (matches this chain)
- `AccountInferenceKeyProvisioner.MintAsync` POSTs `/keys` with the account JWT and reads `data.key`
  (the FULL key). A parser bug found during this proof - a narrow regex truncated the base64url key at
  the first `-`/`_`, yielding an invalid 32-char key that 401'd - is fixed: read the structured
  `data.key`, never the `record.masked` display value, and broaden the fallback charset to include `-`/`_`.
- `TranscriptionKeyAutoProvisioner.EnsureAsync` mints and stores the key in the vault as
  `DEVTHROTTLE_API_KEY` only when none is present (manual key or a previously minted key short-circuits
  it: manual override + reuse across restarts, no key sprawl). Wired into the Gateway's post-sign-in
  hook and a detached startup pass (covers an already-signed-in install).

## Cleanup owed
Three throwaway keys were minted on the real account during this proof/debug (labels
`cc-director-autowire-*`); last4 = `uA7v`, `B8Eg`, `wk7A`. Revoke them on the account keys page.
