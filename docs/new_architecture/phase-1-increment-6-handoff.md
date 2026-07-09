# Increment 6 handoff - remaining long-tail verbs down the stream

**Author:** STREAM WORKER 1 (`05896efe`), 2026-07-09. **Status:** foundation + first verb done; the rest is a mechanical repeat of the established pattern.

All work is UNCOMMITTED in the worktree `D:/ReposFred/dt-stream-wt`. Do NOT commit. Build/test with `dotnet`; the full pass baseline is `CcDirector.Gateway.Tests` = 1475 pass / 1 fail where the ONE fail is the pre-existing environmental `DictationEndpointTests.FullPipeline...` ("no api key").

## 1. What already flows DOWN the stream (flag-gated on `gateway.streamMode`, HTTP fallback preserved, byte-identical when off)

Phase 1 core: `prompt`, `interrupt`, `escape`, `hold`, `kill`, `patch` (rename), `create`.
Increment 6 batch 1: `wingman-goal` (first SIDE-EFFECTING verb - proved the services-context pattern).

## 2. The pattern + where each piece lives

- **Contract:** `DirectorCommand` / `DirectorCommandResult` / `DirectorCommandStatus` in `src/CcDirector.Gateway.Contracts/DirectorCommandMessages.cs`.
- **Shared executor:** `src/CcDirector.ControlApi/SessionCommandExecutor.cs` - `DispatchAsync(sessionManager, directorId, command, services?)` switches on `command.Verb`; one internal method per verb reproducing the REST guards + effect, returning a `DirectorCommandResult`. Serialize/Deserialize helpers use `JsonSerializerDefaults.Web` (camelCase).
- **Director-local side effects:** `SessionCommandServices` (in the same file) carries `ProactiveExplain` + `TurnSummaryCache`. Pass it to `DispatchAsync` for verbs that warm a cache; verbs that need none ignore it (a null field skips the side effect, exactly as the REST endpoints do when the service is absent).
- **Director dispatcher:** `GatewayStreamClient` (`src/CcDirector.ControlApi/GatewayStreamClient.cs`) registers `On<DirectorCommand, DirectorCommandResult>("Command", ...)` -> the dispatcher lambda. Wired in `ControlApiHost.BuildStreamClient` - it builds `SessionCommandServices` INSIDE the per-command lambda (BuildStreamClient runs before `StartAsync` sets `_proactiveExplain`/`_turnSummaryCache`, so build it lazily, not at wire time).
- **Gateway down-channel:** `GatewayHost.SendCommandAsync(directorId, cmd, ct)` - `IHubContext<DirectorHub>` + `PushedSessions.GetActiveConnectionId(directorId)`; returns null when no active stream (-> HTTP fallback).
- **Router (ONE decision point):** `src/CcDirector.Gateway/Api/DirectorCommandRouter.cs` - `TrySendAsync(sendCommand, directorId, verb, sessionId, payload, ct)` serializes the payload and returns the stream result, or null to fall back to HTTP. `ReadBody<T>` / `DescribeFailure` map the result. `GatewayEndpoints.Map` receives `sendCommand: _streamMode ? SendCommandAsync : null`.

### To migrate one verb (the recipe)
1. Add the verb method to `SessionCommandExecutor` + a `case` in `DispatchAsync`. Reproduce the REST guards (invalid id -> BadRequest, missing session -> NotFound, etc.) and effect EXACTLY; fire any side effect through `services`.
2. Refactor the Director REST lambda (`ControlEndpoints.cs`) to build a `DirectorCommand` + call `DispatchAsync` + map `DirectorCommandStatus` back to the SAME `Results.*` it returned before. For an identity-stamped or anonymous response, re-read the session and build the response in the endpoint (like `patch`/`create`/`wingman-goal`) so it stays byte-identical.
3. Route the Gateway endpoint stream-first via `DirectorCommandRouter.TrySendAsync`, fall back to the existing `client.*` HTTP call on a null return.
4. Tests: executor unit (happy + guards), stream round-trip (`SendCommandAsync` executes on the Director), Gateway endpoint routes DOWN the stream (dispatcher-spy proof - see `StreamCommandTests.CountingDispatcher`), and flag-off stays HTTP. Build warnings-as-errors; run the `StreamCommand|SessionCommandExecutor` filter + the verb's own regression tests + the full suite.

## 3. What REMAINS (exact refs, 2026-07-09)

All in `src/CcDirector.ControlApi/ControlEndpoints.cs`. Each toggle fires `proactiveExplain?.TriggerBackgroundExplain(session)` as a side effect - route it through `SessionCommandServices.ProactiveExplain` (already wired).

- **`mobile-mode`** - `POST /sessions/{sid}/mobile-mode` ~line 682. Body `MobileModeRequest` (empty -> enabled=true). Sets `session.ViewMode = enabled ? Text : Off`; on enable fires `proactiveExplain.TriggerBackgroundExplain`. Response `{ mobileMode }`.
- **`voice-mode`** - `POST /sessions/{sid}/voice-mode` ~line 711. Body `VoiceModeRequest`. Sets `ViewMode = enabled ? Voice : Text`; ALWAYS fires `proactiveExplain.TriggerBackgroundExplain`. Response `{ voiceMode, mobileMode }`.
- **`wingman-enabled`** - `POST /sessions/{sid}/wingman-enabled` ~line 778. Body `WingmanEnabledRequest`. Sets `session.WingmanEnabled`; on enable fires `proactiveExplain.TriggerBackgroundExplain`, on disable sets `session.IsExplaining = false`. Response `{ wingmanEnabled }`. (Cleanest of the three - do it first.)
- **Per-session prompt queue** - `GET /sessions/{sid}/queue` ~1972, `POST .../queue` (enqueue), `PATCH .../queue/{itemId}` ~2010, and the queue "send". Mostly pure `session.PromptQueue` mutations (verify each for side effects).

**IMPORTANT DTO CAVEAT:** `MobileModeRequest` / `VoiceModeRequest` / `WingmanEnabledRequest` live in `CcDirector.ControlApi`, NOT `CcDirector.Gateway.Contracts`. The **Gateway project does not reference `CcDirector.ControlApi`**, so it cannot build those payload types for `DirectorCommandRouter`. Before routing these verbs at the Gateway, EITHER move the three DTOs to `Gateway.Contracts` (cleanest - they are wire contracts), OR have the Gateway endpoint serialize an inline `new { enabled = ... }`. Also FIRST confirm each verb even HAS a Gateway forwarding endpoint (`grep` `GatewayEndpoints.cs`); some may be Director-local only, in which case only the Director dispatcher + executor migration is needed (no Gateway routing).

## 4. Phase-4 push-freshness finding (blocks a clean portless roster)

In portless mode the Gateway has NO Director endpoint to pull from, so the roster works ONLY from the push cache. Today `PushedSessionStore.TryGetFresh` returns null once the last push is older than `streamStaleAfter` (~20s), and the `/sessions` aggregation then FALLS BACK TO PULL. Portless removes the pull floor, so a quiet Director's cache goes stale and its sessions vanish from the roster. **Phase 4 must make the Director keep the cache fresh** - a periodic re-push / heartbeat `PushSnapshot` (e.g. every ~10s, or on a timer alongside the existing snapshot) so `TryGetFresh` never goes stale for a live stream. (Confirmed by the controller's live Phase-3 test: `served=pushed-cache` worked because the push was fresh; a stale one would have needed the now-absent pull.)

## 5. Phase-4b byte-relay design

The terminal-bytes-over-the-stream relay design (so mobile `/m` + Cockpit `/c` read a session's terminal portless, via the Gateway relaying the Director's stream) is captured in `docs/new_architecture/OVERNIGHT-STATUS.md` (Phase 4 / portless notes) and `docs/new_architecture/portless-director-gateway-stream.html`. Start there.
