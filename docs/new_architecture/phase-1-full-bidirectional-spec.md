# Phase 1 spec: full bidirectional stream (commands DOWN the stream)

**For:** the Phase 1 implementation worker. **Controller:** session c9f9a8e3.
**Branch:** `feat/director-gateway-stream-1a` in worktree `D:/ReposFred/dt-stream-wt`. Build/test with `dotnet`. **Do NOT commit.**
**Read first:** `OVERNIGHT-CHARTER.md`, `docs/new_architecture/OVERNIGHT-STATUS.md`, `docs/new_architecture/phase-1-director-gateway-stream-plan.md`, and `docs/CodingStyle.md`.

## Goal

Extend the existing Director<->Gateway SignalR stream so the Gateway drives the Director through it for real: session commands (prompt, interrupt, escape, hold, kill, patch, and the rest) flow DOWN the stream and take effect; state/deltas already flow UP (Phase 1a, done). Additive and flag-gated on `gateway.streamMode`; the existing HTTP path stays as the fallback. A test per command.

This reuses the down-channel template already proven in Phase 1b: `GatewayHost.PingDirectorAsync` (invoke a client method DOWN via `IHubContext<DirectorHub>` + `PushedSessionStore.GetActiveConnectionId`) and the `GatewayStreamClient` `On<string,string>("Ping", ...)` handler. SignalR `InvokeAsync<T>` correlates each request to its reply for free.

## The one design: a generic command envelope + a shared command executor

The command bodies today are inline lambdas inside `ControlEndpoints.Map(...)` (see the handler map below). There is NO shared command core yet. We create one, exactly as Phase 1a created the shared `ControlEndpoints.Map` SessionDto mapper, so the stream path and the REST path execute identical logic and cannot drift.

### 1. Contracts — new file `src/CcDirector.Gateway.Contracts/DirectorCommandMessages.cs`

```csharp
namespace CcDirector.Gateway.Contracts;

/// A command the Gateway sends DOWN a Director's stream. Verb selects the handler; PayloadJson is the
/// serialized request DTO (or "" when the verb takes none). CommandId is a correlation/idempotency id
/// (SignalR InvokeAsync<T> already correlates the reply; CommandId is for logging + future idempotency).
public sealed class DirectorCommand
{
    public string CommandId { get; set; } = "";
    public string Verb { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string PayloadJson { get; set; } = "";
}

public enum DirectorCommandStatus { Ok = 0, BadRequest = 1, NotFound = 2, Conflict = 3, Error = 4 }

public sealed class DirectorCommandResult
{
    public string CommandId { get; set; } = "";
    public DirectorCommandStatus Status { get; set; }
    public string? BodyJson { get; set; }
    public string? Error { get; set; }
    public bool Ok => Status == DirectorCommandStatus.Ok;

    public static DirectorCommandResult Success(string? bodyJson = null) => new() { Status = DirectorCommandStatus.Ok, BodyJson = bodyJson };
    public static DirectorCommandResult Fail(DirectorCommandStatus status, string error) => new() { Status = status, Error = error };
}
```

### 2. Shared executor — new file `src/CcDirector.ControlApi/SessionCommandExecutor.cs` (internal static)

One method per verb that reproduces the exact guards + underlying `Session`/`SessionManager` calls the REST lambda makes today, returning a `DirectorCommandResult` (the stream path has no HTTP status codes, so the result carries the outcome; the REST layer maps it back to `Results.*`). Plus a `DispatchAsync` that switches on `Verb`, deserializes `PayloadJson`, and calls the right method.

Verbs for increment 1..N (below). Underlying calls (from the handler map, verified):
- `prompt`  — DTO `PromptRequest` -> `PromptResponse`. Guard Exited/Failed -> Conflict(409). `AppendEnter ? session.SendTextAsync(req.Text) : session.SendInput(UTF8 bytes)`; capture `session.Buffer.TotalBytesWritten` as `BufferCursor` BEFORE sending.
- `interrupt` — no DTO. `await session.InterruptAsync()`; `NotSupportedException` -> Conflict.
- `escape` — no DTO. `await session.CancelTurnAsync()`; `NotSupportedException` -> Conflict.
- `hold` — DTO `HoldRequest` (empty body => OnHold=true). `session.OnHold = onHold`; return `{ onHold }`.
- `kill` — no DTO. `await sessionManager.KillSessionAsync(guid)` (swallow all but KeyNotFoundException) then `sessionManager.RemoveSession(guid)`; return `{ killed, removed }`; KeyNotFound -> NotFound.
- `patch` — DTO `SessionUpdateRequest` -> `SessionDto`. `sessionManager.RenameSession(guid, req?.Name)`; re-fetch + `ControlEndpoints.Map`; not found -> NotFound.

Common guards to centralize: `Guid.TryParse(sessionId)` -> BadRequest; `sessionManager.GetSession(guid)` null -> NotFound.

`create` (POST /sessions, DTO `NewSessionRequest` -> `SessionDto`) is heavier (agent parse, default-args, name validation, post-create wingman + pre-prompt). Migrate it LAST, in its own increment, and keep its inline pre-work in the shared method so REST and stream stay identical.

### 3. REST refactor (ControlEndpoints.cs)

Replace each migrated verb's inline lambda body with a call to the shared executor method, then map `DirectorCommandStatus` -> `Results.*` (Ok->Json/200 or 201 for create, BadRequest->BadRequest, NotFound->NotFound, Conflict->StatusCode(409), Error->Problem). Behaviour must stay byte-identical (existing tests + the flag-off regression suite guard this).

### 4. Director side (GatewayStreamClient.cs)

Add a constructor parameter `Func<DirectorCommand, Task<DirectorCommandResult>>? commandDispatcher`. In `SuperviseAsync`, alongside the `Ping` handler, register:
```csharp
_connection.On<DirectorCommand, DirectorCommandResult>("Command", async cmd =>
    _commandDispatcher is null ? DirectorCommandResult.Fail(DirectorCommandStatus.Error, "no dispatcher") : await _commandDispatcher(cmd));
```
Keep the `Ping` handler (the Phase 1b proof) as-is.

### 5. Director wiring (ControlApiHost.cs)

In `BuildStreamClient` (lines ~606-610), pass the dispatcher:
```csharp
return new GatewayStreamClient(gatewayConfig, DirectorId, _version, SnapshotFullSessions,
    cmd => SessionCommandExecutor.DispatchAsync(_sessionManager, DirectorId, cmd));
```
`_sessionManager` is in scope. Nothing else changes there.

### 6. Gateway side (GatewayHost.cs + the command endpoints)

- Add `GatewayHost.SendCommandAsync(string directorId, DirectorCommand cmd, CancellationToken ct = default) -> Task<DirectorCommandResult?>`, modeled exactly on `PingDirectorAsync`: resolve `PushedSessions.GetActiveConnectionId(directorId)` and the `IHubContext<DirectorHub>`, return null when either is missing, else `await hub.Clients.Client(conn).InvokeAsync<DirectorCommandResult>("Command", cmd, ct)`.
- Routing helper (flag-gated, additive): where a Gateway command endpoint currently calls `DirectorEndpointClient.PostPromptAsync/PostInterruptAsync/.../SetHoldAsync/KillSessionAsync/PatchSessionAsync`, first try the stream when `streamMode` is on AND `SendCommandAsync` returns non-null; otherwise fall back to the existing HTTP call. Encapsulate this "try stream, else HTTP" decision in ONE place so every verb routes the same way. Serialize the verb's request DTO into `DirectorCommand.PayloadJson` and deserialize `DirectorCommandResult.BodyJson` back into the verb's response DTO.

## Increments (prove each before the next; update OVERNIGHT-STATUS.md after each)

1. **Contracts + shared executor + `prompt` end-to-end.** Contract types; `SessionCommandExecutor` with the shared guards + `prompt`; refactor the REST prompt lambda to call it; Director dispatcher handler; `ControlApiHost` wiring; `GatewayHost.SendCommandAsync`; route the Gateway prompt endpoint down the stream with HTTP fallback. Tests: executor unit test (prompt happy + Exited->Conflict), Director-dispatcher round-trip over the harness, Gateway routes prompt down the stream and the Director executes it, and flag-off falls back to HTTP.
2. **interrupt + escape.** Add verbs, refactor REST, route, tests each.
3. **hold + kill.** Add verbs, refactor REST, route, tests each.
4. **patch (rename).** Add verb, refactor REST, route, test.
5. **create session** (heaviest) — own increment, keep all inline pre-work in the shared method, test.
6. Sweep the remaining per-session verbs the plan lists (queue send/enqueue, mode toggles, wingman goal/enabled, github create) if time allows; each additive + tested.

## Non-negotiables
- Additive + flag-gated: with `streamMode=off`, behaviour is byte-identical to today (REST path unchanged, no stream). The flag-off regression suite must stay green.
- CodingStyle.md: no `!` null-forgiving operator, `FileLog.Write` entry/exit/error logging on new public methods, try-catch only at boundaries (the SignalR handler + endpoint lambdas are boundaries; the executor methods are not), warnings-as-errors, tests for everything.
- Tests in `src/CcDirector.Gateway.Tests` (integration harness pattern, like `StreamIntegrationTests`) for the wire behaviour; executor unit tests wherever the ControlApi unit tests live (find the existing test project).
- Do NOT commit. Leave everything uncommitted in the worktree. Report each increment's build+test result back to the controller (session c9f9a8e3) via `cc-devthrottle message send c9f9a8e3 "..."`.

## Director command-handler map (reference — verified 2026-07-09)

All in `src/CcDirector.ControlApi/ControlEndpoints.cs` inside `Map(...)`:
- prompt: lines 1933-1966 -> `Session.SendTextAsync`(1421)/`SendInput`(1388)
- interrupt: 2087-2107 -> `Session.InterruptAsync`(1484)
- escape: 2111-2131 -> `Session.CancelTurnAsync`(1475)
- hold: 741-760 -> `session.OnHold = ...`
- kill: 2970-3011 -> `SessionManager.KillSessionAsync`(596) + `RemoveSession`(637)
- patch: 934-948 -> `SessionManager.RenameSession`(717) + re-fetch + `Map`
- create: 2784-2927 -> `SessionManager.CreateSession`(278 overload)
- shared mapper precedent: `ControlEndpoints.Map` is `internal static` (ControlEndpoints.cs:3246); `ControlApiHost.SnapshotFullSessions`(617-618) calls it.

DTO namespaces: `PromptRequest/PromptResponse/HoldRequest/SessionUpdateRequest/NewSessionRequest/GitHubSessionRequest` in `CcDirector.Gateway.Contracts`; `MobileModeRequest/VoiceModeRequest/WingmanEnabledRequest` in `CcDirector.ControlApi`.
