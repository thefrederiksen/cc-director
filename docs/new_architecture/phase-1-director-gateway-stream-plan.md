# Phase 1 implementation plan: Director-to-Gateway push stream

**Status:** PLAN -- ready to hand to a development agent. Depends on the candidate model in `portless-director-gateway-stream.html` (UNDER CONSIDERATION). This phase does NOT commit to the full portless end-state; it delivers one safe, additive increment.

**Date:** 2026-07-09 (merged with the review notes in `phase-1-director-gateway-stream-improvements.md`)

---

## 1. Goal (what "done" means)

Establish one persistent, bidirectional connection that each Director dials OUT to the Gateway, and move session-state reporting from **Gateway-pulls-Director** to **Director-pushes-Gateway** over that connection -- with **full feature parity** (everything the fleet and Director skills do today still works) and **zero regression risk** (the change is additive and flag-gated; the existing pull path stays as the fallback).

At the end of Phase 1:

- Each Director holds an open SignalR connection to the Gateway and pushes a full session snapshot on connect, a fresh snapshot on every reconnect, deltas when a session changes, and a remove when a session disappears.
- The Gateway serves `GET /sessions` for a stream-connected Director from its **pushed cache**, not by fanning out a pull to that Director -- while running the full existing post-fetch aggregation pipeline unchanged.
- The bidirectional channel is proven by a small synthetic down message (see section 4.7).
- A restart of either side self-heals: the Director auto-reconnects and re-seeds; a connection-generation guard drops stale messages.
- Everything is behind a `gateway.streamMode` flag. Off = today's behaviour, byte-for-byte.

## 2. Phase 1 vs. the eventual portless model (read this first)

The HTML candidate model describes the eventual end-state: commands, history requests, terminal bytes, and state all flow through Director-initiated outbound streams, and the Director exposes no network-facing API. **Phase 1 is deliberately much smaller.** An implementation agent must NOT build the portless end-state. Phase 1 is only:

- Move session-state reporting from Gateway pull to Director push.
- Serve `GET /sessions` from the pushed cache when fresh; keep pull as fallback.
- Prove the down direction with one small message.

Everything else stays exactly as it is today (see non-goals).

## 3. Non-goals (explicitly later phases -- do NOT do these here)

- Inverting the terminal byte path (bytes still go the current direct route).
- Removing or loopback-restricting the Director's REST API (portless). The Director stays fully reachable exactly as today.
- Moving commands / fleet-comms routing (prompt, interrupt, hold, message send/ask/spawn) onto the stream. They keep their current REST paths.
- Storing history on the Gateway.

## 4. Design and task breakdown

Split into two work items (section 4.8 explains the split). Each subsection notes the review item it addresses.

### 4.1 Contracts (`src/CcDirector.Gateway.Contracts`)
- Reuse `SessionDto` as the snapshot/delta payload (no new session shape).
- `DirectorStreamHello` record: directorId, version.
- Message envelope carries a **connection generation** and a **sequence** (see 4.4), not a bare integer epoch [review #2].
- `RemoveSession` message: generation, sequence, sessionId [review #4].

### 4.2 Config + flag
- Add `StreamMode` (bool, default false) and `StaleAfterSeconds` (default 20) to `GatewayConfig`.
- When off: no hub client is created and the Gateway ignores the cache (pure current behaviour). This is the regression safety net.

### 4.3 Gateway hub (`src/CcDirector.Gateway`)
- Add `Microsoft.AspNetCore.SignalR` (`builder.Services.AddSignalR()` near `GatewayHost.cs:709`).
- New `Streaming/DirectorHub.cs`:
  - `OnConnectedAsync` / `OnDisconnectedAsync`: register/unregister against a `directorId`; log both.
  - `Hello`, `PushSnapshot(SessionDto[] sessions)`, `PushDelta(SessionDto session)`, `RemoveSession(string sessionId)` -- all stamped with the connection generation + sequence and accepted only from the active connection (4.4).
- Map the hub: `_app.MapHub<DirectorHub>("/director-stream")` after `UseWebSockets()` (already present at `GatewayHost.cs:806`).
- On connect, call `DirectorRegistry.MarkStateReporting(directorId)` (line ~149) so the reconcile poll already skips it.

### 4.4 Connection generation + duplicate-connection ownership [review #2, #3]
Do NOT use a persistent integer epoch (a restarted Director restarts at 1 while the Gateway holds a higher value, and its snapshot would be wrongly rejected).

- Use SignalR's own `ConnectionId` as the **generation token**: it is unique per connection and changes on every reconnect and every process restart, so no client-minted GUID is needed. Add a monotonic `sequence` per connection for ordering within a generation.
- `PushedSessionStore` (singleton) per Director stores: `activeConnectionId`, latest `sequence`, `receivedAt`, and `List<SessionDto>`.
- Rules:
  - A new connection becomes the active connection for its Director; **its first full snapshot is authoritative** and replaces the cache (this is what unblocks a restarted Director).
  - Accept snapshot/delta/remove only from the `activeConnectionId`; within it, ignore any message with `sequence <= latestSequence`.
  - `Unregister(directorId, connectionId)` clears routing **only if** `connectionId` is still the active one -- a late disconnect from a superseded connection is logged and ignored [review #3].
- Every full snapshot is authoritative: **prune** cached sessions for that Director not present in the snapshot [review #4].

### 4.5 Stream identity binding [review #9]
A valid token proves the caller may connect; it does not prove it owns the claimed `directorId`.
- Resolve the Director identity from trusted registration/device context; if the client sends `directorId`, verify it is allowed for that credential / matches the registered identity.
- Reject spoofed or conflicting `directorId` claims; log accepted and rejected identity decisions.
- Authenticate the handshake with the existing token/device-key check (`GatewayHost.Devices` + `HasValidToken`, the same 3-arg check the PWA routes use).

### 4.6 Gateway aggregation dual-mode (`GatewayEndpoints.cs`, fan-out near line 414)
- Split the current fan-out into "**get sessions for this Director**" (cache-first) and the **existing downstream aggregation loop, unchanged** [review #10]. Serving from cache must replace ONLY the fetch step; every post-fetch side effect still runs: `SessionOwnerCache.RetainForDirector` / `.Remember`, query filters, machine/user/tailnet/view-url enrichment, voice/transcription overlays, `SessionOrdering.EffectiveColor` + triage, `NeedsYouSince` stamping, and envelope `machineErrors`.
- `PushedSessionStore.TryGetFresh(directorId, StaleAfterSeconds)` returns **deep copies** of the cached `SessionDto`s, never references -- the aggregation mutates them, so references would contaminate the cache and race concurrent requests [review #7].
- **Recompute time-derived fields at serve time** [addition]: cached `IdleSeconds` is a relative value frozen at push time and would stop advancing for a quiet session (the "Idle Xm" column would stall precisely when idleness matters). Recompute it from the absolute `LastActivityAt` in the cached DTO (`IdleSeconds = now - LastActivityAt`). Absolute timestamps cache correctly; only server-computed relative numbers need recomputing.
- Fall back to the existing pull when: no fresh cache, the stream is stale (> `StaleAfterSeconds`), or the query is unsupported by the cache (see `includeExited` below).
- `includeExited=true` [review #5]: full pushed snapshots carry the same default live roster the Director's `GET /sessions` returns; if `includeExited=true` is requested, **fall back to pull** until the snapshot source can produce an exited-inclusive snapshot. Never answer `includeExited=true` from a live-only cache.

### 4.7 Director stream client (`src/CcDirector.ControlApi`)
- New `GatewayStreamClient.cs`:
  - `HubConnection` to `{gateway.url}/director-stream` with `WithAutomaticReconnect(backoff)` and the auth token in the handshake.
  - On start and on `Reconnected`: push a full snapshot (the new connection makes it authoritative).
  - On session-state change (the existing `NotifySessionState` call sites): push a `PushDelta`; on session removal/exit-from-default-roster: push a `RemoveSession` [review #4].
  - Feed `GatewayConnectionMonitor` so the Director window's status dot shows healthy / reconnecting / down.
- **Reuse the exact Director `SessionDto` mapper** [review #6]: extract the Control API `/sessions` mapping into a shared method/service and call it from `ControlEndpoints` `/sessions`, the stream full snapshot, and the per-session delta. Do not write a second builder. Acceptance test compares a stream snapshot row to the local `/sessions` row for the same session.
- Wire in `ControlApiHost.BuildGatewayClient` (line 572): when `streamMode` is on, start `GatewayStreamClient` **alongside** the existing `GatewayClient` (heartbeat/doorbell stay as the reconcile floor in Phase 1).

### 4.7b Down-channel proof [review #8 -- corrected]
Confirmed: there is no live Gateway sender of assessed-state anymore (only comments remain at `GatewayEndpoints.cs:495`, `GatewayHost.cs:821`). So do NOT describe this as moving a live path.
- Add a small **synthetic proof message** (e.g. `Ping` down, `Pong`/ack up, or an `EchoAssessment` the Director applies to a log-only annotation) purely to exercise and test the down direction.
- Do NOT move a real command (hold/prompt/interrupt) onto the stream in Phase 1 -- that is command migration, a non-goal. The real down-channel migration belongs to a later phase.

### 4.8 Observability [review #11]
Structured logs/counters for: stream connected, stream disconnected, snapshot accepted, delta accepted, remove accepted, stale message dropped, `/sessions` served from pushed cache, `/sessions` fell back to pull (missing/stale/unsupported query), unauthenticated or identity-conflicting stream rejected. Runtime logs must make it obvious, per Director per request, whether the answer came from cache or pull.

## 5. Test harness (required deliverable)

Formalise the throwaway spike into a real in-process harness in the Gateway test project (create `CcDirector.Gateway.Tests` if none exists; follow existing `*.Tests` conventions). `StreamHarness` boots a real `GatewayHost` on an ephemeral port and a real `GatewayStreamClient` in-process with a fake session source. Assert observed behaviour (via a fake pull client) not internals:

1. connect + snapshot -> `GET /sessions` returns pushed sessions and the pull client was NOT called.
2. delta -> a single-session change is reflected without a full snapshot.
3. remove/tombstone -> a removed session disappears without waiting for stale fallback [review #4].
4. snapshot pruning -> a session absent from a new snapshot is dropped.
5. stale-generation / restart -> a Director "process restart" (new connection) reseeds and is not blocked by prior state [review #2].
6. late disconnect -> an old connection disconnecting after a new one is active does NOT clear the active cache [review #3].
7. stale-cache fallback -> no push within `StaleAfterSeconds` -> aggregation pulls.
8. down-proof -> the synthetic down message reaches the Director and is acked.
9. auth reject -> unauthenticated connect refused.
10. identity binding -> a valid credential claiming a different `directorId` is rejected [review #9].
11. no cache mutation -> mutating returned sessions during aggregation does not mutate the store [review #7].
12. mapper parity -> a stream snapshot row equals the local `/sessions` row for the same session [review #6].
13. clock recompute -> a quiet session's `IdleSeconds` advances across requests served from cache [addition].
14. flag off -> with `streamMode=false`, behaviour is byte-identical to today (pull only).
- Unit tests: `PushedSessionStore` generation/sequence/prune logic; `GatewayStreamClient` snapshot/delta/remove; identity binding; DTO deep-copy.

## 6. Skills update (parity verification -- mostly docs)

Phase 1 removes no endpoint, so the skills keep working; the task is to PROVE it and touch up transport wording:
- **fleet-comms skill**: with `streamMode=on`, run every verb (`session list`, `message send`, `message ask`, `session spawn`, `session rename`, `schedule list`) and confirm identical results. Update any line describing the Gateway as "pulling" Directors.
- **dev-throttle / director skill**: verify the documented Control API endpoints behave identically; update transport wording. No endpoint is removed, so nothing should break.
- Record the verification (a short pass/fail table) in the pull request as proof.

## 7. Acceptance criteria (definition of done)

- With `streamMode=on`, a live Director's sessions appear in `GET /sessions` via push, and logs confirm the Gateway did not pull that Director.
- A Director **process restart** is not blocked by prior Gateway state; it reseeds within a few seconds [review #2].
- A **late disconnect** from an old connection cannot remove the active connection or cache [review #3].
- **Session removal** is reflected without waiting for stale-cache fallback [review #4].
- `includeExited=true` is served from an equivalent cache or explicitly falls back to pull [review #5].
- Stream snapshot rows equal local `/sessions` rows for the same session (shared mapper) [review #6].
- Mutating `SessionDto`s during aggregation does not mutate the pushed cache [review #7].
- A valid credential cannot claim another Director's identity [review #9].
- All current `/sessions` aggregation side effects still run for cache-served Directors [review #10].
- A quiet session's `IdleSeconds` keeps advancing when served from cache [addition].
- Runtime logs identify, per Director per request, cache vs pull [review #11].
- Killing/restarting the Gateway shows the Director auto-reconnecting and re-seeding with no operator action; the connection indicator reflects healthy / reconnecting / down.
- All harness + unit tests pass in CI; all fleet-comms and director-skill verbs pass the parity check with the flag on; with `streamMode=off` the regression suite is green.

## 8. Recommended implementation split [review #12]

Implementable as one PR, but at the upper edge of a safe review. Preferred split:

- **Phase 1a -- pushed session state:** contracts + flag; hub + authenticated + identity-bound registration; `PushedSessionStore` with generation/sequence guard and prune; snapshot + delta + remove; `/sessions` cache-first fetch with deep-copy, side-effect preservation, clock recompute, and stale/`includeExited` fallback; shared mapper; harness tests 1-7, 9-14.
- **Phase 1b -- down-channel and polish:** synthetic down-proof message (test 8); Director monitor status integration; reconnect hardening; skill parity verification; doc wording. 

1a proves the high-value state path on its own; 1b keeps the bidirectional polish separate.

## 9. Effort estimate

| Area | Rough effort (focused human) |
|------|------------------------------|
| Contracts + config/flag | 0.5 day |
| Gateway hub + `PushedSessionStore` (generation/sequence/prune) + auth + identity binding | 3 -- 4 days |
| Aggregation dual-mode (deep copy, side-effect preservation, clock recompute, includeExited/stale fallback) | 1.5 -- 2 days |
| Director stream client + shared-mapper extraction + monitor | 3 -- 4 days |
| Down-channel synthetic proof | 0.5 day |
| Observability | 0.5 day |
| Test harness + unit/integration tests (14 integration + units) | 3 -- 4 days |
| Skill parity verification + doc touch-ups | 1 day |
| Integration, debugging, review buffer | 1 -- 2 days |
| **Total** | **~14 -- 18 days (about 3 weeks)** |

Size: roughly 2,000 -- 3,000 lines including tests. The added correctness rigor (generation model, tombstones/prune, shared mapper, DTO copies, identity binding, clock recompute, expanded tests) pushed this up from the first draft's estimate. **Strongly prefer the 1a/1b split** rather than one PR at this size.

## 10. Risks and mitigations

- **Silent stream wedge showing stale data** -> `StaleAfterSeconds` fallback to pull; the heartbeat floor still runs in Phase 1.
- **Stale/duplicate messages across reconnects and restarts** -> connection-generation + sequence guard (4.4).
- **Cache aliasing corruption** -> deep-copy on read (4.6).
- **Identity spoofing** -> stream identity binding (4.5).
- **Feature drift between pull and push shapes** -> shared mapper (4.7) + parity test.
- **Cross-platform** -> pure .NET; proven identical Windows/Mac in the spike.
- **Scope creep toward portless** -> the non-goals + section 2 fence; commands and bytes stay on their current paths.

## 11. Document history

| Date | Author | Change |
|------|--------|--------|
| 2026-07-08 | QA Agent (with the owner) | Initial Phase 1 plan: additive, flag-gated Director-push stream with pull fallback, a required test harness, and skill parity verification. |
| 2026-07-09 | QA Agent (with the owner) | Merged all review notes from `phase-1-director-gateway-stream-improvements.md`: connection-generation guard replacing int epoch (#2/#3), remove/tombstone + snapshot pruning (#4), includeExited rule (#5), shared SessionDto mapper (#6), deep-copy cached DTOs (#7), corrected dead down-channel to a synthetic proof (#8), stream identity binding (#9), preserved aggregation side effects (#10), observability (#11), 1a/1b split (#12), expanded acceptance (#13); plus the serve-time clock-recompute fix for frozen `IdleSeconds`. Effort re-estimated to ~14-18 days. |
