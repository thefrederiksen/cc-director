# Gateway Key Vault (PLANNED)

**Status:** PLANNED
**Date:** 2026-05-31
**Audience:** Anyone implementing centralized API-key storage + handout on the Gateway.

## Related documents

- [GATEWAY_DIRECTOR_ARCHITECTURE.md](GATEWAY_DIRECTOR_ARCHITECTURE.md) - the Gateway/Director split this extends
- `../cockpit/COCKPIT_DESIGN.md` - the always-on Gateway service this lives in

## Key-handout flow at a glance

![Gateway Key Vault: you set keys once on the Gateway; Directors pull on demand over the tailnet, cache in-memory, and call OpenAI/Anthropic](gateway-key-vault.png)

---

## 1. Why

Directors need API keys to do their work: **OpenAI** for dictation / transcription / TTS, **Anthropic** for Claude, and others over time. Today each machine carries its own copy (env vars / local config / `credentials.env`), which drifts across machines and is painful to rotate.

The Gateway is already the one always-on service the whole fleet talks to. Make it the **single home for these keys**: set a key once on the Gateway, and every Director fetches it as needed. No Director stores keys on its own disk.

This is **distinct from cc-vault** (personal data / contacts). The Key Vault holds only fleet **API keys/secrets**.

## 2. What it stores

Named secrets, opaque string values, e.g.:

- `OPENAI_API_KEY` - the user's own OpenAI key (transcription "bring your own key" mode, issue #497)
- `DEVTHROTTLE_API_KEY` - a DevThrottle-issued `dt_` key (transcription "Use DevThrottle" mode, issue #497)
- `ANTHROPIC_API_KEY`
- (any future provider key)

The transcription mode itself ("byo" vs "devthrottle") is NOT a secret; it lives in config.json as
the top-level `transcription_mode` key and selects which of the two keys above is used.

## 3. Where

On the always-on Gateway box, in a store **outside git** (same principle as today's `credentials.env`). The Gateway service owns it; it is read at request time, not baked into any build.

## 4. API (on the Gateway)

| Route | Purpose |
|---|---|
| `GET /vault/keys` | list key **names** present (never values) - discovery |
| `GET /vault/keys/{name}` | return one key's value, for a Director that needs it |
| `PUT /vault/keys/{name}` `{ value }` | set / update a key |
| `DELETE /vault/keys/{name}` | remove a key |

**SELF-HOST ONLY. Every route in the table above is refused on the hosted Gateway** (404, body
`{ "error": "the key vault is not available on the hosted gateway" }`). The store is one global vault
file at the shared storage root with no tenant in the file, the store, or the routes, and the routes sit
behind only the host-wide authentication gate - which admits any enrolled device key from any account. So
on shared hosted infrastructure the value read is credential theft, the write is tampering with another
account's paid service, and the delete disables it. The refusal is expressed through the shared hosted-refusal
primitive (`HostedRouteDeny.ExclusiveGroup`, the one boundary every deny family on this Gateway adopts): on
hosted the four handlers are never mapped and one verb-less catch-all refuses everything under `/vault/keys` -
every request shape, and any route added under the prefix later. The claim that nothing else serves beneath
`/vault/keys` is startup-validated (`HostedRefusalRouteSpace`), and the hosted decision reads
`GatewayHostedMode.IsHosted` directly rather than an argument a caller can omit. Off hosted the primitive maps
the real handlers and creates no refusal at all, so self-host behavior is byte-identical to what is documented
here.

**What this deny stops, narrowly: the HTTP ROUTE write and the HTTP ROUTE delete - NOT every write to this
vault.** An earlier revision of this document claimed "the write is stopped" outright. That was false, and
the correction is recorded here rather than quietly dropped, because the mistake is the reusable lesson: the
question asked was "do the DENIED ROUTES write?", and the question that decides an un-deny condition is
"does ANYTHING write?" plus "what is ALREADY SITTING THERE from before the deny existed?". A deny closes one
door; it says nothing about the other doors, nor about what accumulated before it was hung.

The other writers to this same global vault, enumerated exhaustively (every `Set`, `SetIfAbsent` and
`Delete` call in the codebase):

| Writer | Operation | When it fires | Hosted-gated? |
|---|---|---|---|
| `GatewayHost.SeedKeyVaultFromEnvironment` | `SetIfAbsent` | Host startup | **Yes** - no-ops on hosted |
| `TranscriptionKeyAutoProvisioner.EnsureAsync` | `SetIfAbsent`, `Set` | Every sign-in, and again at startup | **Yes** - inert on hosted |
| `TranscriptionKeyAutoProvisioner.RevokeMintedKeyAsync` | `Delete` x2 | Every sign-out | **Yes** - inert on hosted |

All three writers are gated on `GatewayHostedMode.IsHosted` (each reads the deployment signal directly, so it
cannot fail open by a caller omitting an argument), so **no new key material arrives in the global vault on
hosted while this deny is in force.** Material that **predates** this deny is a separate concern that the
un-deny condition below still owns: the deny closes the route door and the writer gates close the write doors,
but neither touches what was already in the file.

**The un-deny condition, in order, and the purge is REQUIRED:**

1. Tenant-partition every remaining producer in the table above.
2. **Quarantine, purge or migrate the pre-existing global vault root** - material predating this deny is
   already in the file, and this change never touched it.
3. Only then restore a tenant-scoped route, one at a time. **The raw-value `GET` is the last one back.**

Anyone lifting this deny needs the partition **and** the purge. Do not treat step 2 as optional; it is not
satisfied by any amount of configuration or rollout work, and it cannot be skipped on the grounds that the
routes were denied.

**This deny does not make key material on the hosted Gateway safe, and must not be read as clearance for
anything else.** It closes one route group. In particular it says nothing about the voice vault key, which
stays UNCONFIGURED on hosted until the whole voice chain is partitioned: configuring it does not deliver
voice, it arms a cross-tenant leak, because clips and the plaintext transcript beside each one land in a
shared directory until that chain is partitioned. Nothing here changes that.

## 5. How Directors get keys

**Pull on demand.** When a Director feature needs a key (dictation -> `OPENAI_API_KEY`), it `GET`s it from the Gateway and **caches it in memory only** - never writes it to local disk. On a cache miss (or a provider rejecting the key), it re-fetches.

Pull-on-demand beats push: keys live in exactly one place, and a rotation (`PUT`) propagates to every Director on its next fetch, with nothing to re-deploy.

```
Director (dictation needs OpenAI)
   -> GET https://<gateway>/vault/keys/OPENAI_API_KEY
   -> use the value in-memory for the OpenAI call
```

## 6. Trust

The **tailnet is the boundary**, as everywhere else in this system: keys travel Tailscale-encrypted between the Gateway and a Director, and the existing Gateway token gates the endpoints. No additional auth layer.

## 7. Open questions

1. **At-rest format** - a plain file outside git (simplest, matches `credentials.env`) vs OS-level encryption (DPAPI / Keychain). Default to the simple file; revisit only if wanted.
2. **Rotation freshness** - `PUT` updates immediately; Directors pick it up on next fetch. Add a short in-memory TTL (or a `key.changed` signal) if a faster pickup is ever needed.
3. **Migration** - seed the vault from the existing per-machine `credentials.env` / env vars, then have Directors stop reading local copies.
4. **Cockpit** - likely does **not** need keys (they're for Directors/agents); keep the vault Director-facing unless a concrete Cockpit need appears.

---

## Document History

| Date | Author | Change |
|---|---|---|
| 2026-05-31 | claude (cc-director assistant) | Initial PLANNED design: central API-key vault on the Gateway, pull-on-demand handout to Directors. |
