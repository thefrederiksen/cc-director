# Remove-the-Network-Port: Independent Inspection 3 (phases 3-7)

**Mission tip:** `ac359ec00415adb5baab8c21b6aa7c67effe621c` (detached HEAD)  
**Verdict:** **FAIL**  
**Counts:** **10 proved defects**: 1 critical, 3 high, 6 medium. **1 additional hypothesis** is recorded separately.

This inspection treated mission documents and prior reports as claims, not evidence. It reviewed the current tree, phase history, active built-in instructions, registration and stream seams, and the relevant tests. “Proved” below means the behavior follows directly from the current source, a current test, a runnable counterexample, or a combination of those. Consequences that require a particular deployment state are labelled as hypotheses.

## Proved findings

### 1. [CRITICAL] Phase 3 replaces a session-bound authorization check with a forgeable filename

**Law:** An agent may change its behavior, but not who is allowed in.

`SessionManager` gives a Claude process its own `CC_SESSION_POINTER_FILE` at `src/CcDirector.Core/Sessions/SessionManager.cs:725-736`. All pointer files are ordinary siblings named only as `<session-guid>.json` (`SessionHookFiles.cs:41-56`). `SessionPointerWatcher.Apply` derives the target session exclusively from that filename and, after parsing the body, mutates that session and its Claude-id routing map (`SessionPointerWatcher.cs:154-208`). There is no per-session secret, ownership proof, handle capability, ACL distinction, or body binding.

The test intended to prove isolation instead proves only that body fields do not override the filename: `SessionPointerDropTests.cs:168-186` writes the attack body to the *authorized session’s* path. It never attempts to write a sibling live session’s path. A same-user agent can derive the sibling directory from its own environment path and write `<victim-session-id>.json`; the watcher then applies it to the victim. The old route’s session-bound credential is not preserved.

**Proved impact:** one agent process can retarget another live session’s Claude session id, transcript pointer, and routing-map entry on the same Director. No adversarial authorization test covers this.

### 2. [HIGH] A current Gateway cannot command an otherwise healthy pre-phase-6 launcher

**Scope:** Phase 6 mixed-version fleets.

The launcher immediately before the phase-6 cut (`git show f2c022e06^:src/CcDirector.Launcher/LauncherStreamClient.cs`) enables its stream only when both the Gateway is configured and `GatewayConfig.StreamMode` is true. In that version the config key is opt-in and defaults false when absent. Such a launcher can continue its REST registration/heartbeat and appear registered.

At the inspected tip, `LauncherLifecycleRelay.cs:25-31,141-176` has exactly one command arm and returns `NotConnected` for a registered launcher without a stream. `GatewayHost.cs:4076-4089` likewise returns null when no active stream exists. The REST relay is correctly gone, but there is no version gate, incompatible-state marker, or rolling-upgrade bridge.

**Proved impact:** after the Gateway is upgraded first, any older launcher whose legacy `streamMode` is absent/false remains discoverable but loses start, stop, restart, status, launch, app-search, and file-search commands. Current tests exercise current/current components; they do not execute an old launcher binary against the new Gateway.

### 3. [HIGH] The launcher accepts and can force-kill a single non-installed process claimant

**Scope:** Phase 4 registration identity and lifecycle control.

`DirectorInstanceLocator.Resolve` checks only that a registration PID is alive and that its process start time falls in a broad registration window (`DirectorInstanceLocator.cs:185-224`; ten minutes before through two seconds after). When exactly one process survives, it is returned without consulting the executable path (`:234-237`). The suite explicitly locks this in with `ASingleClaimant_IsResolvedEvenWhenItIsNotTheInstalledDirector` (`DirectorInstanceLocatorTests.cs:189-202`).

`DirectorSupervisor.StopAsync` trusts that result, raises the claimed Director signal, and force-kills the PID if delivery or graceful exit fails (`DirectorSupervisor.cs:165-204`).

**Proved impact:** a lone registration can authorize lifecycle action against a process that is not the installed Director. A stale/forged registration plus a compatible PID start-time window can end in a force-kill of the wrong process. Whether PID reuse or a forged file occurs on a given machine is deployment-dependent; the acceptance and kill path are proved.

### 4. [HIGH] Gateway session-number allocation still falls back to a second authority

**Law:** Nothing may try the Gateway and then something else.

`SessionManager` asks the Gateway asynchronously and calls `AssignOfflineNumber` when the Gateway returns null, throws, exhausts its pool, or returns an already-reserved number (`SessionManager.cs:263-282`). `GatewayClient.AllocateSessionNumberAsync` converts HTTP failures and exceptions to null and logs the path as “offline fallback” (`GatewayClient.cs:444-476`). `SessionManagerTests.cs:392-419` explicitly canonizes the fallback.

**Proved impact:** a configured Gateway can fail and the Director then mints the same logical identity from a local allocator. This is an exact Gateway-then-other-authority path, not a platform choice or cached retry.

### 5. [MEDIUM] Gateway dictionary retrieval still falls back to local disk

**Law:** Nothing may try the Gateway and then something else.

`DictionaryResolver.ResolveAsync` first calls the configured Gateway and, on an unreachable/error result, loads the local file (`DictionaryResolver.cs:65-100`). The log says “falling back to cached local dictionary.” `DictionaryResolverTests.Connected_but_unreachable_falls_back_to_cached_local_dictionary` and `Connected_http_error_status_falls_back_to_cache` (`DictionaryResolverTests.cs:112-140`) pass and preserve this behavior.

**Proved impact:** the effective dictionary authority changes from Gateway to disk after a failed Gateway attempt, allowing stale local policy/data to become active. This is another literal standing-law violation.

### 6. [MEDIUM] The Codex SessionStart hook is Windows-only on a cross-platform Director

**Scope:** Phase 3 preamble hooks.

`CodexHookInstaller.EnsureInstalled` unconditionally writes `report-preamble.ps1` and registers a command invoking `powershell` (`CodexHookInstaller.cs:55-72`). There is no OS branch. The adjacent Claude implementation makes the required distinction: Windows uses PowerShell; non-Windows uses `/bin/sh` (`ClaudeHookInstaller.cs:153-162`). PowerShell Core’s normal executable on macOS/Linux is `pwsh`, not `powershell`.

**Proved impact:** the installed Codex hook command is not a native runnable command on macOS/Linux unless the machine has an extra compatibility shim/alias. The current tests assert the PowerShell form and therefore cannot detect the platform defect.

### 7. [MEDIUM] Named instances accumulate duplicate global Codex preamble hooks

**Scope:** Phase 3 named-instance behavior.

The hook file is global (`~/.codex/hooks.json`), while the script directory comes from the current named instance. Idempotence compares the complete command string only (`CodexHookInstaller.cs:87-124`), so each instance-specific script path is treated as a distinct hook.

A runnable proof invoked `EnsureInstalled` twice against one hooks file using `instances/default/...` and `instances/work/...` script roots. It returned `True,True`; the resulting `SessionStart` array contained two entries and two PowerShell commands, one for each instance. Both commands read the same process-level `CC_SESSION_PREAMBLE_FILE`, so a Codex launch receives duplicate preamble output. Removed/renamed instances also leave stale commands behind.

### 8. [MEDIUM] The built-in `move-session` skill still instructs agents to use the deleted listener

**Scope:** Phases 5-6 active operational surface.

The Gateway embeds `Skills/Content/*.skill.md` (`CcDirector.Gateway.csproj:95`) and publishes `move-session` through `BuiltInSkills.cs:51,78-86`; this is active product content, not archival documentation. Yet `move-session.skill.md` says the Director loopback `/healthz` still exists (`:61-69`), directs target selection by probing Director ports (`:157-159`), and sets `CC_DIRECTOR_API` for spawn and buffer operations (`:162,190,241`). Its changelog repeats the deleted mechanism (`:421`). It also describes Gateway REST as a fallback (`:55-59,415-416`).

**Proved impact:** the current Gateway distributes instructions for a door and environment override that phase 5 deleted. Following the built-in workflow reaches nothing or violates the Gateway-only/no-fallback laws. The `.claude/skills/move-session/SKILL.md` copy has the same stale commands.

### 9. [MEDIUM] Session `ViewUrl` derivation still depends on the deleted Director endpoint

**Scope:** Phase 5 wire contract and desktop/Gateway consumers.

The stream snapshot calls `ControlEndpoints.Map(session, DirectorId)` without identity endpoints (`ControlApiHost.cs:824-831`). The mapper defaults `tailnetEndpoint` to empty and emits empty `TailnetEndpoint`/`ViewUrl` (`ControlEndpoints.cs:187-200,406-410`). Phase 5 registration deliberately sets `ControlEndpoint = ""` (`InstanceRegistration.cs:44-60`). The Gateway’s compatibility enrichment derives its Director base URL from `TailnetEndpoint` or `ControlEndpoint`; with both empty it returns an empty string (`GatewayEndpoints.cs:4552-4594`) and constructs a relative `/sessions/{id}/view?...` link (`:1367-1372`).

No current Director session-view route exists, and a source scan found no Gateway/Cockpit route that consumes this legacy path as a session view. The aggregation tests mask the seam by manually assigning a fake Director `BaseUrl`/tailnet endpoint (`SessionsAggregationTests.cs:911-969`). `GatewayCronNotifier` also still selects `TailnetEndpoint ?? ControlEndpoint` (`GatewayHost.cs:1490-1502`), which is empty for current registrations.

**Proved impact:** new tunnel-only Directors emit no valid session-view origin; link enrichment produces a relative legacy contract rather than a working view endpoint, and cron notification cannot derive a Director link.

### 10. [MEDIUM] `NoListenerDependencyGuardTests` does not guard against listeners

**Scope:** Phase 7 guard.

The guard claims a process cannot listen without ASP.NET hosting machinery, but `IsListenSurface` checks only `Microsoft.AspNetCore`, hosting, and Kestrel assemblies (`NoListenerDependencyGuardTests.cs:55-63`). A listener needs none of those: .NET’s base class library supplies `TcpListener` and `HttpListener`. The repository already demonstrates both in Core (`LoopbackLoginListener.cs:36-50,234`; `AutomationBrowserRegistry.cs:309`), a project referenced by Director/launcher code.

A runnable counterexample project with only normal SDK references created `new TcpListener(IPAddress.Loopback, 0)`, started it, and successfully bound `127.0.0.1:50578`. Its referenced assemblies were `System.Console`, `System.Linq`, `System.Net.Primitives`, `System.Net.Sockets`, and `System.Runtime`; ASP.NET reference count was zero. The guard’s premise is therefore false even though the guard test passes.

**Proved impact:** a raw TCP listener, `HttpListener`, or equivalent BCL listener can be introduced into the Director or launcher while every phase-7 assertion remains green.

## Unproved hypothesis (not included in defect count)

`LauncherStreamClient.SuperviseAsync` sends `Hello` once and then waits for the connection to close (`LauncherStreamClient.cs:109-118`). `SayHelloAsync` catches every invocation failure and claims auto-reconnect will retry (`:199-215`), but retry happens only on `Reconnected`. A Hub/protocol error while SignalR remains connected could therefore leave the launcher stream open but never registered for commands until a later disconnect. The static control flow supports the risk, but this inspection did not inject a connected-state `Hello` failure against a real Hub, so it remains a hypothesis.

## Controls that were proved present

- Phase 3 no longer stamps `CC_DIRECTOR_API` or `CC_DIRECTOR_TOKEN` into a session, and the focused hook contract tests pass.
- Phase 4 lifecycle requests choose exactly one platform mechanism and do not call the Gateway. On Unix/macOS, `LifecycleSignal.UnixRequestPath` uses `InstanceContext.SharedRoot/config/lifecycle-signals` (`LifecycleSignal.cs:120-151`). Cross-process lifecycle/path tests pass.
- Phase 5 removed the Director HTTP listener itself. Product code no longer starts `ControlApiHost` as a network host; current session tooling is wired to the Gateway. The surviving `CC_DIRECTOR_API` occurrences in active source are stale comments/instructions, not an environment stamp.
- Phase 6 removed the launcher HTTP listener and the Gateway REST relay. Current/current launcher commands have one stream arm and refuse when it is unavailable. Installer, self-update, stopper, registration identity/probe, and macOS path-focused tests pass.
- Phase 7’s guard test passes, but finding 10 proves that green result is not evidence of the claimed invariant.

## Runnable evidence

Before any trusted build, a recursive worktree scan found **zero** `obj` or `bin` directories, so there was nothing stale to delete. Builds/tests then ran from that clean state.

Targeted test executions:

| Area | Passed | Skipped | Failed |
|---|---:|---:|---:|
| Core unit hook/pointer contracts | 28 | 0 | 0 |
| Core phase 3-4/Gateway reachability contracts | 47 | 0 | 0 |
| Launcher registration/autostart (two target frameworks) | 40 | 0 | 0 |
| Setup engine installer/self-update/stopper/macOS contracts | 39 | 0 | 0 |
| Gateway unit guard/tunnel/launcher registry contracts | 23 | 0 | 0 |
| Gateway integration stream/aggregation/control-host contracts | 48 | 0 | 0 |
| Focused session-number and dictionary fallback contracts | 36 | 1 | 0 |
| **Total executions** | **261** | **1** | **0** |

The one skipped test was `SessionManagerTests.CreateAndKillSession_StatusChanges`, outside the reviewed fallback behavior. The fully green targeted suite is not a pass verdict: several tests explicitly preserve defects 3-5, and the phase-7 test cannot observe defect 10.

## Final verdict

**FAIL — 10 proved defects (1 critical, 3 high, 6 medium), plus 1 unproved hypothesis.** The listener deletions and lifecycle platform split are substantially present, but the mission laws are not met and the phase-7 guard cannot enforce the central invariant.
