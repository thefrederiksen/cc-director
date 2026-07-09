# Phase 1a QA report (issue #1176)

Living QA report for the Phase 1a implementation on branch `feat/director-gateway-stream-1a`. One section per verified increment, newest first. Every increment is built with warnings-as-errors and its tests run before it is committed.

---

## Increment 3: /sessions aggregation dual-mode + end-to-end harness

**Date:** 2026-07-09
**Status:** PASS

### What was built

- **Cache-first fetch** in the `GET /sessions` fan-out (`GatewayEndpoints`): if a Director's stream is
  connected and its last push is within the stale window, its sessions are served from
  `PushedSessionStore.TryGetFresh` (deep copies with recomputed idle clocks) and the pull is skipped
  entirely. Every downstream side effect (owner-cache retain/remember, filters, machine/user/tailnet/
  view-url enrichment, voice/transcription overlays, EffectiveColor + triage, NeedsYouSince,
  machineErrors) runs **unchanged** on the cached sessions - only the fetch step changed (review #10).
- **`includeExited` fallback** (review #5): cache-first is gated on `!includeExited`, so an
  exited-inclusive query always pulls (a live-only snapshot never answers it).
- **Observability** (review #11): the fan-out logs `served=pushed-cache` on a hit and
  `served=pull (stream connected but cache stale/empty)` on a fallback, per Director per request, for
  any stream-participating Director.
- **Gateway kill-switch**: `GatewayHost` reads `gateway.streamMode` (or an explicit constructor
  override); when off, the hub is not mapped and `/sessions` never consults the cache - byte-identical
  to today.
- **Integration harness** (`StreamIntegrationTests`): boots a real `GatewayHost` (stream mode on) and
  dials the hub with a real SignalR client over HTTP.

### End-to-end tests (over real HTTP + SignalR)

| Test | Proves | Review item |
|------|--------|-------------|
| `StreamPush_IsServedFromCache_WithoutPullingTheDirector` | The Director is registered with an unreachable endpoint; a pushed snapshot appears in `/sessions` with no machine error, so it can only have come from cache | core #1, #10 |
| `PushDelta_IsReflectedInSessions` | A single-session delta changes the served state | #4 groundwork |
| `RemoveSession_IsReflectedInSessions` | A remove drops the session from `/sessions` | #4 |
| `UnauthenticatedConnect_IsRejected` | A hub connection with no token fails the handshake | auth #9 |
| `StreamModeOff_DoesNotMapTheHub` | With the flag off, `/director-stream/negotiate` is 404 | flag-off parity |

This confirms the whole Gateway side (hub auth, store, aggregation) works on the wire, and that the .NET
SignalR client's Bearer token satisfies the existing host-wide auth middleware with no middleware change.

### Test result

```
dotnet test --filter "StreamIntegrationTests|DirectorHubTests|PushedSessionStoreTests"
Passed!  Failed: 0, Passed: 28, Skipped: 0, Total: 28
```

Build: clean, `TreatWarningsAsErrors=true`.

### Still remaining for Phase 1a

The Director-side `GatewayStreamClient` (increment 4: dial out, auto-reconnect, snapshot/delta/remove
from a shared session mapper, feed the connection monitor). The integration harness above already covers
the Gateway side of the plan's test list; the remaining harness cases (stale-cache fallback, restart
reseed over the wire) fold in with the client.

---

## Increment 2: the SignalR hub (DirectorHub)

**Date:** 2026-07-09
**Status:** PASS

### What was built

- `DirectorStreamHello` contract - the identity-declaring first message.
- `CcDirector.Gateway.Streaming.DirectorHub` - the SignalR hub each Director dials out to. It receives
  `Hello` (binds the connection to one Director), `PushSnapshot`, `PushDelta`, and `RemoveSession`, and
  records them in the `PushedSessionStore`. It also marks the Director state-reporting in the
  `DirectorRegistry` so the reconcile poll skips it.
- Wired into `GatewayHost`: a shared `PushedSessions` store instance, `AddSignalR()`, singleton
  registration of the store and registry, and `MapHub<DirectorHub>("/director-stream")` mapped after the
  host-wide auth middleware so the handshake is token-gated like every other route.

### Review items covered

| Item | How | Evidence (test) |
|------|-----|-----------------|
| #9 identity binding (Phase 1a form) | The connection is bound to one Director at `Hello`; every later message uses the bound id, so a connection can only ever affect the Director it declared. A message before `Hello` is rejected; an empty id aborts the connection. | `Hello_WithEmptyDirectorId_AbortsAndDoesNotBind`, `PushSnapshot_BeforeHello_ThrowsHubException`, `TwoConnectionsBoundToDifferentDirectors_DoNotCrossContaminate` |
| auth (transport) | The hub is mapped after the host-wide token middleware; the .NET SignalR client presents its Bearer token on the handshake (verified end-to-end in the integration harness increment). | wired in `GatewayHost` |
| state-reporting integration | `Hello` marks the Director state-reporting so the reconcile poll skips it | `Hello_BindsConnection_AndMarksStateReporting` |

Also covered: snapshot/delta/remove apply to the bound Director, disconnect unregisters the connection,
and a restarted Director (new connection, same Director) reseeds.

**Note on the residual for #9:** binding a credential *cryptographically* to a specific Director id needs
per-Director credentials from the account/device epic; Phase 1a enforces connection-to-Director binding
(a connection cannot push to a Director other than the one it declared) on top of the existing token gate.

### Test result

```
dotnet test --filter "DirectorHubTests|PushedSessionStoreTests"
Passed!  Failed: 0, Passed: 23, Skipped: 0, Total: 23
```

Build: clean, `TreatWarningsAsErrors=true` (Gateway project compiles with the hub wired in).

---

## Increment 1: correctness core (config flag + PushedSessionStore)

**Date:** 2026-07-09
**Status:** PASS

### What was built

- `GatewayConfig.StreamMode` (bool, default false) and `GatewayConfig.StreamStaleAfterSeconds` (int, default 20), parsed from the `gateway` block of `config.json`. Opt-in: only a JSON boolean `true` enables stream mode, so a missing/malformed key leaves the Director on the existing pull path (the regression safety net).
- `SessionDto.Clone()` - a copy safe to hand out from the pushed cache; re-creates the `DriverCapabilities` list so the aggregation cannot contaminate the cache.
- `CcDirector.Gateway.Streaming.PushedSessionStore` - the Gateway's per-Director cache of pushed session state, enforcing the four correctness rules from plan section 4.4.

### Review items covered

| Item | How | Evidence (test) |
|------|-----|-----------------|
| #2 connection generation (not int epoch) | A restarted Director's brand-new connection is authoritative at any sequence | `RestartedDirector_NewConnectionFirstSnapshot_IsAuthoritative` |
| #3 active-connection ownership | A late disconnect from a superseded connection is ignored | `LateDisconnectFromOldConnection_DoesNotClearTheActiveConnection` |
| #4 remove/tombstone + snapshot prune | Explicit remove drops a session; a snapshot prunes anything absent | `ApplyRemove_DropsTheSession`, `ApplySnapshot_PrunesSessionsAbsentFromTheSnapshot` |
| #7 deep-copy on read | Mutating a served session does not mutate the cache | `TryGetFresh_ReturnsDeepCopies_MutatingResultDoesNotAffectStore` |
| clock recompute (plan addition) | Idle seconds advance from absolute LastActivityAt at serve time | `TryGetFresh_RecomputesIdleSecondsFromLastActivityAt` |
| stale-cache fallback (#5 groundwork) | A push older than the stale window returns null (caller pulls) | `TryGetFresh_WhenLastPushIsOlderThanStaleWindow_ReturnsNull` |

Also covered: stale-sequence drop, non-active-connection drop, unregister-clears-active, unknown-director, and pre-registration rejection.

### Test result

```
dotnet test --filter PushedSessionStoreTests
Passed!  Failed: 0, Passed: 14, Skipped: 0, Total: 14
```

Build: clean, `TreatWarningsAsErrors=true`.

### Not yet built (later increments)

SignalR hub + auth/identity binding (#9), Director stream client, `/sessions` aggregation dual-mode (#10), the down-channel proof (#8), observability wiring (#11), and the in-process integration harness (plan section 5). Tracked toward Phase 1a completion, then Phase 1b (#1177).
