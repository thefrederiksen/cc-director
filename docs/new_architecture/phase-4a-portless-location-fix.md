# Phase 4a fix spec: portless session location (+ push freshness)

**For:** a fresh STREAM WORKER. **Controller:** `c9f9a8e3`. Branch `feat/director-gateway-stream-1a`, worktree `D:/ReposFred/dt-stream-wt`. Build/test `dotnet`. **Do NOT commit.**

## The finding (from the controller's live Phase 4a test)

With a REAL Gateway EXE + a REAL remotely-unreachable slot-5 Director (tailscale off, `controlEndpoint=""`), over the real stream:
- CREATE works portless: `POST /directors/{id}/sessions` carries the director id in the path, routes down the stream (`DirectorCommandRouter ... stream status=Ok`), real session created. PROVEN.
- Roster works portless: `GET /sessions` serves from the pushed cache (`served=pushed-cache (2 sessions)`) with Gateway `effectiveColor`/`stateLabel`. PROVEN.
- **Per-session commands 404 portless:** `POST /sessions/{sid}/prompt` (and hold/patch/interrupt/escape/kill/wingman-goal) return 404 EVEN when the roster shows the session. Root cause: `GatewayEndpoints.LocateSessionAsync` (`GatewayEndpoints.cs:1618`) resolves the owning Director by HTTP-pulling EACH Director's `/sessions/{sid}` via `client.GetSessionAsync(d.ControlEndpoint, sid)`. Portless = empty `ControlEndpoint` -> pull fails -> `(null,null)` -> 404. So command DELIVERY (the stream) works, but session LOCATION still depends on an HTTP pull. This is the ONE control-plane portless gap.

## Fix 1 (critical): locate the session from the pushed cache, not an HTTP pull

`LocateSessionAsync` must FIRST consult the pushed cache (the Gateway already knows which Director pushed each session):
- Add access to the `PushedSessionStore` (it's in scope in `GatewayEndpoints.Map` - used at ~line 410 `pushedSessions.TryGetFresh`) and the stale window to `LocateSessionAsync` (new params; update its ~18 call sites, or capture `pushedSessions` in the `Map` closure so the signature churn is smaller).
- New logic: for each registered Director, check its FRESH pushed cache (`PushedSessions.TryGetFresh(d.DirectorId, stale)`) for a session with this `sid`; if found, return `(d, session)` WITHOUT any HTTP call. Only if no fresh pushed cache contains the session, fall back to the existing HTTP-pull loop (covers non-stream Directors and the flag-off path).
- Consider adding a small helper on `PushedSessionStore` like `TryLocate(sid) -> (directorId, SessionDto)?` that scans the fresh caches, to keep the endpoint clean.
- Result: per-session commands resolve the owner from the stream cache and route down the stream to a remotely-unreachable Director. Portless per-session commands work.

## Fix 2 (robustness): keep the push cache fresh so location never falls back to a (portless-impossible) pull

Today the Director pushes a full snapshot on connect + deltas on change; a QUIET session's cache goes stale after `staleAfterSeconds` (default 20), and `TryGetFresh` returns null -> location/roster fall back to HTTP pull, which is impossible portless.
- In the Director's `GatewayStreamClient`, add a periodic re-push (a timer that re-sends the full snapshot every N seconds where N < the stale window, e.g. every ~10s) so `TryGetFresh` stays fresh for quiet sessions. Fire-and-forget, best-effort, only while connected. Additive; inert when stream mode off.
- (Alternative/also: the Gateway could treat "stream connected" as a weaker freshness signal, but the Director-side periodic re-push is the simplest robust fix and matches the "Director keeps the Gateway current" model.)

## Constraints
- Additive + flag-gated: `streamMode` OFF = byte-identical to today (HTTP-pull location path unchanged). CodingStyle: no `!`, FileLog on new public methods, try-catch at boundaries only, warnings-as-errors, tests.
- Tests: (1) `LocateSessionAsync`/`TryLocate` finds a session from the pushed cache with the Director's endpoint empty (portless), and per-session command routing then reaches it over the stream; (2) periodic re-push keeps `TryGetFresh` non-null across the stale window for a quiet session; (3) flag-off still locates via HTTP pull unchanged.
- Do NOT commit. Report each fix to the controller (`cc-devthrottle message send c9f9a8e3 "..."`) and wait for confirmation. After both fixes land + green, the controller re-runs the live Phase 4a harness to prove the FULL command set works client->Gateway->stream->Director with zero remote reach.
