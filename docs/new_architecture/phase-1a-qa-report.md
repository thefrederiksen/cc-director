# Phase 1a QA report (issue #1176)

Living QA report for the Phase 1a implementation on branch `feat/director-gateway-stream-1a`. One section per verified increment, newest first. Every increment is built with warnings-as-errors and its tests run before it is committed.

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
