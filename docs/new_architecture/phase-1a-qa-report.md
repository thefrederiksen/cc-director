# Phase 1a QA report (issue #1176)

Living QA report for the Phase 1a implementation on branch `feat/director-gateway-stream-1a`. One section per verified increment, newest first. Every increment is built with warnings-as-errors and its tests run before it is committed.

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
