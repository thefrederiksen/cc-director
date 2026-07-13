# Car Mode offline resilience - Phase 4b: Gateway idempotency

Status: built 2026-07-13 by the Car Mode - Manager (session 965d43f8), design confirmed by the Car Mode -
Architect (session f44f39c0). Issue #1427. Follows Phase 4a (never lose speech + durable buffer + audible
offline state; merged in #1454/#1457 and deployed). The durability follow-up is tracked as #1458.

## Why 4b exists

Phase 4a auto-retries a held turn on reconnect, but it had to HOLD one case for the owner: a turn whose
brain call was already SENT and whose result was unknown (transcribe briefly succeeded, then the
connection dropped). A blind retry of that turn could act twice (a duplicate start / message / approve).
4b removes that hold by making POST /carmode/turn idempotent, so ANY held turn can auto-retry safely -
acting at most once.

## The mechanism

- The client sends its durable command-audio record id (the same id that keys the on-device pending-turn
  store) as the standard `Idempotency-Key` HTTP header on POST /carmode/turn. It is stable across retries.
- A new in-memory `CarModeTurnCache` (mirroring `CarModePendingStore` / `CarModeConversationStore`) does
  single-flight + cached-result dedupe, keyed by (device, key):
  - The FIRST request for a key runs the brain and caches the result.
  - A concurrent or later duplicate awaits or returns that SAME result WITHOUT re-running the tool loop, so
    the fleet action AND the per-device conversation append happen exactly once.
- The crux for the dead zone: the endpoint runs the brain on `CancellationToken.None`, NOT the request
  token. So a client that drops mid-turn does NOT abort the work - it finishes and caches - and the
  client's retry gets the cached result instead of re-acting. The endpoint awaits with the request token
  (via `WaitAsync(ct)`) so a disconnected client still returns promptly while the work continues.
- Cache policy: SUCCESS only. On a brain EXCEPTION the key is evicted so a transient error can still
  recover on the next retry (caching a failure would trap the error until the TTL). TTL is 30 minutes,
  aligned to the client's staleness cap.

## Client change

- `classifyHeldTurn` drops the `brainSent` gate: staleness (30 minutes) is now the ONLY reason a held turn
  waits for the owner, because server dedupe makes an already-sent turn safe to auto-retry. The 4a
  "ambiguous / discard-only" hold and its UI are removed; the ask-owner surface is now only for
  older-than-the-cap turns, which offer both Send (safe via dedupe) and Discard. `brainSent` is still
  recorded for diagnostics but no longer gates anything.

## Accepted residual double-act windows (v1, in-memory), documented per the Architect's decision

Both are rare and annoying-not-catastrophic (a duplicate message/start; delete is idempotent):
1. A brain exception thrown AFTER a tool already acted: the key is evicted, so the retry re-runs and may
   act again.
2. A Gateway RESTART between the original call and the retry loses this in-memory cache (and the in-memory
   conversation store), so the retry re-runs. Gateway restarts are INDEPENDENT of the owner's connection
   drops, so a real dead-zone drive (Gateway up throughout) is fully safe.
Durable persistence is deliberately not built here; it is tracked as follow-up issue #1458.

## Proof

- Server unit tests (`CarModeBrainTests`, the `TurnCache_*` cases), deterministic with counting fake
  chat + fleet:
  - `TurnCache_DuplicateKey_ActsOnceAndAppendsOnce` - the same key: the fleet action fires EXACTLY once and
    the conversation is appended once (the scripted chat has exactly one run's worth of turns, so a wrong
    re-run would also throw).
  - `TurnCache_DistinctKeys_ActTwice` - different keys act independently.
  - `TurnCache_BrainThrows_EvictsKeySoRetryReRuns` - a failure evicts so a retry recovers.
  - `TurnCache_SuccessIsRetainedForTheKey` - a success is cached and returned to a duplicate.
- The Phase 4a offline proof harness (`packages/client-core/browser-tests/carmode-offline-proof`) STILL
  PASSES under the 4b client changes (no speech lost, auto-completes on reconnect, states announced).

Coverage caveat (carried from 4a): the client harness proves the real client logic in real Chromium, NOT
the real phone microphone / mobile audio session / radio offline / live Gateway. Soren's by-hand phone
pass remains the final confirmation and is deferred to his return.
