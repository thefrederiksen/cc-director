# Phase 1 Director-Gateway stream plan improvements

**Status:** REVIEW NOTES -- companion to `phase-1-director-gateway-stream-plan.md`.

**Date:** 2026-07-09

These notes tighten the Phase 1 plan before implementation. The overall plan is sound: it is additive, flag-gated, Director-initiated, keeps pull fallback, and has the right recovery shape. The changes below reduce migration risk and remove ambiguous implementation choices.

---

## 1. Keep Phase 1 clearly smaller than the portless target

The HTML candidate model describes the eventual end-state: commands, history requests, terminal bytes, and state all flow through Director-initiated outbound streams, and the Director exposes no network-facing API.

Phase 1 should remain narrower:

- Move session-state reporting from Gateway pull to Director push.
- Serve `GET /sessions` from pushed cache when fresh.
- Keep the existing pull path as fallback.
- Prove the down direction with one small message.
- Do not move commands, terminal bytes, history, fleet-comms routing, or Director port exposure.

Recommended doc change: add a short "Phase 1 vs. eventual portless model" section near the top of the plan so an implementation agent does not accidentally build the full HTML candidate.

---

## 2. Replace simple integer epoch with connection generation

The current plan proposes a monotonic `int epoch` incremented on every reconnect. That is not enough across Director process restarts: a restarted Director can begin again at epoch `1` while the Gateway still holds a higher epoch from the previous process.

Use this model instead:

- `streamGenerationId`: a new GUID created by the Director stream client for each logical connection generation.
- `sequence`: a monotonic integer within that generation.
- Gateway state per Director stores the active `connectionId`, `streamGenerationId`, and latest `sequence`.
- Messages from a non-active generation are ignored.
- Messages from the active generation with `sequence <= latestSequence` are ignored.

This handles stale messages from old connections and process restarts without relying on a durable local counter.

---

## 3. Define duplicate connection ownership

SignalR reconnects can briefly overlap: an old connection may disconnect after a new connection is already active. `OnDisconnectedAsync` must not remove the new connection's cache or active routing entry.

Required store behavior:

- `Register(directorId, connectionId, streamGenerationId)` marks that connection as active for the Director.
- `Unregister(directorId, connectionId)` only clears connection routing if `connectionId` is still the active one.
- Snapshot, delta, and remove messages are accepted only from the active connection/generation.
- A late disconnect from an older connection is logged and ignored.

Add a unit test for "old disconnect does not clear new active connection."

---

## 4. Add remove/tombstone deltas

`PushDelta(SessionDto session)` only upserts. It cannot represent a session that disappeared from the roster.

Add one of these:

- `RemoveSession(sequence, sessionId)` for explicit deletion.
- `PushDelta(sequence, SessionDto session, bool removed)` if keeping one method is preferred.

The Director should emit a remove/tombstone when:

- A session is removed from the roster.
- A session exits and should not appear unless `includeExited=true`.

The Gateway should also treat every full snapshot as authoritative for that Director and prune cached sessions not present in the snapshot.

---

## 5. Specify `includeExited` behavior

The current `/sessions` endpoint supports `includeExited=true`. The pushed cache must define how it answers that query.

Recommended Phase 1 rule:

- Full pushed snapshots include the same default live roster that the current Director `GET /sessions` returns.
- If `includeExited=true`, the Gateway falls back to pull until the stream snapshot source can produce an equivalent exited-inclusive snapshot.

Alternative rule:

- Build `SnapshotFullSessions(includeExited: true)` and keep both live-only and include-exited cache variants.

Do not silently answer `includeExited=true` from a live-only cache.

---

## 6. Reuse the exact Director `SessionDto` mapper

Avoid creating a second `SessionDto` builder for stream snapshots. The existing Control API `/sessions` mapper already owns many details, including identity fields and display-facing fields.

Recommended implementation:

- Extract the Director-side session mapping into a shared method or small service.
- Use that same code from:
  - `ControlEndpoints` `/sessions`
  - `GatewayStreamClient` full snapshots
  - `GatewayStreamClient` per-session deltas

Acceptance criterion: a test should compare a stream snapshot row with the equivalent local `/sessions` row for the same session.

---

## 7. Copy cached DTOs before Gateway aggregation stamping

`GatewayEndpoints` mutates each `SessionDto` while aggregating:

- `DirectorId`
- `MachineName`
- `User`
- `TailnetEndpoint`
- `ViewUrl`
- voice state
- transcription state
- `EffectiveColor`
- `TriageBucket`
- `NeedsYouSince`

`PushedSessionStore.TryGetFresh` should return copies of cached `SessionDto` objects, not references to the stored cache. Otherwise one request can contaminate the cache for later requests.

Add a unit test that mutating returned sessions does not mutate the store.

---

## 8. Recheck the down-channel proof

The plan says to move the existing assessed-state down-push from HTTP `POST /sessions/{sid}/assessment` to the hub. The Director receiver exists, but current Gateway code appears to have retired the old assessed-state producer.

Before implementation, choose one:

- If an active Gateway sender exists outside the searched path, reference it explicitly in the plan.
- If no active sender exists, describe this as adding a small proof message, not moving a live production path.
- If a better live down-channel exists, use that instead so the proof validates a real workflow.

Keep HTTP fallback for any moved live path.

---

## 9. Bind stream identity, not just token validity

A valid token proves the caller is allowed to connect. It does not by itself prove the caller owns the `directorId` claimed in `DirectorStreamHello`.

Recommended rule:

- Resolve the stream's Director identity from trusted registration/device context where possible.
- If the client sends `directorId`, verify it is allowed for that credential or matches the currently registered Director identity.
- Reject spoofed or conflicting `directorId` claims.
- Log accepted and rejected stream identity decisions with enough detail to debug pairing/config problems.

Add an auth test where a valid credential attempts to claim a different `directorId`.

---

## 10. Preserve all current aggregation side effects

Serving pushed sessions must not bypass the rest of `/sessions` behavior. The cache should replace only the fetch step.

The existing post-fetch pipeline still needs to run:

- `SessionOwnerCache.RetainForDirector`
- `SessionOwnerCache.Remember`
- query filters
- machine/user/tailnet/view-url enrichment
- voice/transcription overlays
- effective color and triage classification
- `NeedsYouSince` stamping
- envelope `machineErrors`

Implementation guidance: split the current fan-out into "get sessions for Director" and keep the downstream aggregation loop unchanged as much as possible.

---

## 11. Add observability for cache-vs-pull decisions

Phase 1's main acceptance criterion is that the Gateway does not pull stream-connected Directors when the pushed cache is fresh. Make this visible.

Add structured logs or counters for:

- stream connected
- stream disconnected
- snapshot accepted
- delta accepted
- remove accepted
- stale message dropped
- `/sessions` served from pushed cache
- `/sessions` fell back to pull because cache was missing/stale/unsupported query
- unauthenticated or identity-conflicting stream rejected

The integration tests can assert through a fake pull client, but runtime logs should make field diagnosis easy.

---

## 12. Recommended implementation split

The current plan is implementable as one large PR, but it is at the upper edge of a safe review.

Recommended split:

### Phase 1a: pushed session state

- Contracts/config flag.
- Gateway hub and authenticated stream registration.
- `PushedSessionStore` with generation/sequence guard.
- snapshot, delta, remove/tombstone.
- `/sessions` cache-first fetch with stale fallback.
- integration tests for connect, snapshot, delta, remove, stale fallback, auth, flag off.

### Phase 1b: down-channel and polish

- Down-channel proof message or migrated live down path.
- Director monitor status integration.
- reconnect hardening tests.
- skill parity verification.
- docs wording updates.

This split proves the high-value state path first and keeps the riskier bidirectional polish separate.

---

## 13. Updated acceptance additions

Add these to the Phase 1 definition of done:

- A Director process restart cannot be blocked by an old higher Gateway epoch.
- A late disconnect from an old connection cannot remove the active connection or cache.
- Session deletion/removal is reflected without waiting for stale-cache fallback.
- `includeExited=true` is either correctly served from an equivalent cache or explicitly falls back to pull.
- Mutating `SessionDto`s during `/sessions` aggregation does not mutate the pushed cache.
- A valid credential cannot claim another Director's identity.
- Runtime logs identify whether each Director in `/sessions` was served from pushed cache or pull fallback.

