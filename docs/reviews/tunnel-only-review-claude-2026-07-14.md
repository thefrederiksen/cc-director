# Tunnel-Only Migration Review - Independent Assessment

- Date: 2026-07-14
- Reviewer: Claude (independent review session "Tunnel Review - Claude fable", machine SOREN_NORTH)
- Reviewed commit: origin/main at `ff7a571a` ("refactor(gateway-cleanup): remove the dead verify-before-advertise machinery (tunnel-only)", pull request #1511, merged 2026-07-14)
- Method: read-only review of a detached worktree checked out at origin/main. Five parallel research passes (migration completeness, dead code, connection resilience, documentation, repository hygiene), every load-bearing claim re-verified by hand against the worktree. No product code was changed and no pull request was opened.

**Important note on method:** the working checkout at `D:\ReposFred\devthrottle` was **52 commits behind origin/main** when this review started. The actual tunnel-only cut (pull requests #1486, #1488, release v1.1.0 in #1489) landed inside that gap. Every conclusion below was re-verified against a fresh worktree at `ff7a571a`; conclusions drawn from the stale checkout would have been badly wrong (for example, it still contained the full HTTP fallback paths). The stale checkout is itself a finding - see section 5.

---

## Summary of top findings

1. **The tunnel-only cut is real and holds on the Director leg.** The Gateway never dials a Director: the HTTP client class was deleted, every session verb rides the tunnel, and a Director that is not tunnel-connected produces an explicit 502. Verified by hand (details in section 1).
2. **The Launcher leg was never migrated.** The Gateway still dials cc-launcher over plain HTTP across the network when the launcher has no stream connection (`src/CcDirector.Gateway/Api/MachineEndpoints.cs:204,302,360-369`). "The Gateway never dials out" is only true for Directors.
3. **LAN addressing mode still binds the Director Control API to all interfaces** (`0.0.0.0`) when configured (`src/CcDirector.ControlApi/ControlApiHost.cs:264-272`), directly contradicting the cut's stated invariant that the inbound port stays closed on every machine. Nothing dials it anymore, so it is pure attack surface.
4. **A mid-flight tunnel drop during a command surfaces as a raw HTTP 500, and most hot-path commands are awaited with no timeout at all.** For the driving-on-mobile use case this is the single highest-value resilience fix (section 3, finding R1).
5. **The public Control API reference documents an endpoint surface that no longer exists.** `docs/public/api/01-control-api.md` still describes sessions, prompts, buffers, git, and handover endpoints on the Director; the Director floor actually has about a dozen routes. Five other documents describe worlds that never shipped and should be deleted outright (section 4).
6. **Concrete dead code is ready to delete today:** the register/heartbeat cluster inside `GatewayClient.cs`, `DirectorForwarding.cs` plus the unused YARP forwarder registration, the orphaned `DispatchContracts.cs`, the `CockpitWsUrls`/`CockpitShotUrls` contracts, the now-inert `streamMode` configuration key, and the never-assigned `_serveProvisioner` field with its dependent branches (section 2).
7. **The desktop "Verify now" button can no longer ever succeed** - it still posts to a Gateway verify endpoint that was deleted in the cut (section 1, finding M5).

---

## Section 1 - Tunnel-only migration review

### What is genuinely done (verified against code, not documents)

- **The Gateway has no HTTP path to a Director.** `src/CcDirector.Gateway/Discovery/DirectorEndpointClient.cs` (the HTTP wrapper the Gateway used to call Directors) is deleted. A repository-wide search for `client.<something>Async(director.ControlEndpoint)` call shapes returns zero hits. `src/CcDirector.Gateway/Api/DirectorCommandRouter.cs:13-16` states the new contract plainly: a null result means the Director is not tunnel-connected and the command is unroutable - the endpoint returns 502, never an HTTP dial. Spot-checked on the prompt endpoint (`src/CcDirector.Gateway/Api/GatewayEndpoints.cs:1281-1301`, error text "director not connected to the tunnel") and the buffer endpoint (`GatewayEndpoints.cs:1240-1256`).
- **The stream-mode flag is gone.** `src/CcDirector.Gateway/GatewayHost.cs:455`: the tunnel is mandatory; the parameter is retained only for existing test call sites. On the Director, `GatewayStreamClient` runs whenever a Gateway is configured.
- **The Director floor is genuinely small.** `src/CcDirector.ControlApi/ControlEndpoints.cs` (1,158 lines) maps twelve routes: `/healthz`, `/shutdown`, seven `/fleet/*` routes (sessions, send, ask, spawn, rename, done, broadcast), `/sessions/{sid}/fleet-preamble`, `/sessions/{sid}/fleet-preamble-hook-output`, and `/sessions/{sid}/claude-hook`. The old session-driving surface (list, prompt, interrupt, buffer, patch, handover, repositories, and so on) is gone from the Director.
- **The default bind is loopback only** (`ControlApiHost.cs:266,291,302` - `IPAddress.Loopback`), and at startup the Director proactively **tears down** any Tailscale Serve mapping a previous build left behind (`ControlApiHost.cs:516-524`), so upgraded machines self-heal to a closed inbound port.
- **Registration rides the tunnel.** The Director no longer posts to register or heartbeat over HTTP (`src/CcDirector.ControlApi/GatewayClient.cs:497-515` - "do NOT register, heartbeat, or run the two-way verify handshake"). The tunnel Hello carries the identity and the hub upserts the registry with `Source="stream"` and an empty control endpoint (`src/CcDirector.Gateway/Streaming/DirectorHub.cs:59-83`, `src/CcDirector.Gateway/Discovery/DirectorRegistry.cs:116-142`).
- **Two real hardening fixes are in place on the tunnel:** MessagePack framing so binary frames do not inflate by a third and trip the size limit, and a 32-megabyte ceiling for one-shot command replies (`src/CcDirector.Gateway.Contracts/DirectorUpStreamMessages.cs:78,118,126-128`, wired at `GatewayHost.cs:1016-1026`) so large turn or buffer reads no longer kill the connection (pull request #1496).
- **Web clients only ever talk to the Gateway.** The cockpit, the mobile app, and the phone client all call Gateway routes (`/directors/{id}/...` proxies that now resolve over the tunnel); none dials a Director port directly.

### What is inconsistent, half-migrated, or wrong

**M1. The Launcher leg still dials out over HTTP - and part of that path is already unreachable.** `src/CcDirector.Gateway/Api/MachineEndpoints.cs` tries the launcher stream first (`LauncherCommandRouter.TrySendAsync`, lines 193 and 290) and then falls back to building an `HttpClient` against `http://<networkAddress>:<port>/` across the network (lines 204, 302, and `BuildLauncherClient` at 360-369). This is the exact stream-first-with-HTTP-fallback pattern the Director leg just eliminated. It gets worse: the launcher's own web host now binds loopback only (`src/CcDirector.Launcher/LauncherHost.cs:74` - `o.Listen(IPAddress.Loopback, _port)`), so the remote-address arm of that fallback can never connect to a cross-machine launcher - it is dead code that still reads as a live network path. Meanwhile the launcher runs both legs at once: it still HTTP-registers and heartbeats (`src/CcDirector.Launcher/GatewayRegistrationClient.cs:121-158`, mapped at `MachineEndpoints.cs:70`) and also dials the launcher stream (`src/CcDirector.Launcher/LauncherCore.cs:64,72` start both clients). Comments at `MachineEndpoints.cs:182,280` still say "When stream mode is on", a gate that no longer exists. **Recommendation:** give the launcher the same cut the Director got - tunnel-only commands, registration on the stream, delete the HTTP fallback (its remote arm is provably unreachable already) - or write the exception down explicitly so "the Gateway never dials out" stops being true only with an asterisk.

**M2. LAN addressing mode contradicts the cut - and users can still switch it on.** `src/CcDirector.ControlApi/ControlApiHost.cs:264-272` still binds the Control API to `0.0.0.0` when `addressing_mode` is "lan" in config.json (`src/CcDirector.Core/Configuration/AddressingModeConfig.cs:8` still parses it), and the mode is still settable at runtime through the Gateway settings endpoint (`src/CcDirector.Gateway/Api/SettingsEndpoints.cs:216-229`) and the cockpit settings page (`apps/cockpit/src/settings/SettingsView.tsx`, the addressing chooser). The comment at `ControlApiHost.cs:511-515` says the whole point of the cut is that "the inbound port stays CLOSED on every client machine" - yet this mode opens it on every interface, and since the Gateway never dials Directors anymore, **no shipped caller exists for that open port**. The #1511 commit message says this is deliberately kept for a later slice. **Recommendation:** prioritize that slice, and hide the cockpit chooser in the meantime. This is not just dead code; it is a user-reachable switch whose only remaining effect is opening an unneeded network listener.

**M3. The Gateway still accepts HTTP Director registration nobody sends.** `POST /directors/register` and `POST /directors/{id}/heartbeat` are still mapped (`src/CcDirector.Gateway/Api/GatewayEndpoints.cs:424,439`), and `DirectorRegistry.Upsert` keeps the whole `Source="http"` path with `ControlEndpoint = req.TailnetEndpoint` (`DirectorRegistry.cs:79-109`). Current Directors never call these; only pre-v1.1.0 Directors would. **Recommendation:** keep them through a short compatibility window if older Directors may still be running, then delete the endpoints and the "http" source path together, with the registry sweeper simplification that follows. Record the intended removal date on an issue so it does not silently become permanent.

**M4. `GatewayClient` on the Director is now mostly a fossil.** The file still opens with a class comment describing the five-step register/heartbeat/verify lifecycle (`GatewayClient.cs:15-25`) that `Start()` explicitly no longer performs. The class survives only as the outbound caller for fleet operations plus candidate-address selection. Large parts of it are unreachable (detailed in section 2, finding D1). **Recommendation:** delete the dead members and rewrite the class comment to describe what it actually is now.

**M5. The desktop "Verify now" button can never succeed.** `GatewayClient.VerifyAsync` (`GatewayClient.cs:846`) is still reachable from the desktop Gateway panel (`src/CcDirector.Avalonia/.../GatewayConnectionPanel.axaml.cs:284` via `ControlApiHost.VerifyGatewayNowAsync:156`), but the Gateway endpoint it posts to (`POST /directors/{id}/verify`) was deleted in the cut (only a tombstone comment remains at `GatewayEndpoints.cs:505`). Every press now ends in a failure branch. **Recommendation:** remove the button or repoint it at a meaningful tunnel-health check (the tunnel state that `GatewayConnectionMonitor` already tracks is the honest signal).

**M6. Stale statements inside the code contradict the new reality.** The `ControlApiHost` class comment (`ControlApiHost.cs:19-31`) and several inline comments (lines 277-283, and the startup log line at 463: "loopback only; remote access via Tailscale Serve") still describe Tailscale Serve as the live remote path. Misleading for anyone reading the file cold. **Recommendation:** update the comments and the log line in the next touch of this file.

**M7. The Gateway-side `TailscaleServeProvisioner` still runs.** It is instantiated and started (`GatewayHost.cs:289,463`). Its Director-mapping function is obsolete (Directors no longer accept inbound traffic), though it may still serve the Gateway's own HTTPS front door. **Recommendation:** review what it still maps in practice; strip the Director-mapping logic (`src/CcDirector.Gateway/Tailscale/TailscaleServeProvisioner.cs:329-364` handles per-Director mappings) and keep only whatever fronts the Gateway itself.

**M8. The stream-mode configuration key is parsed but changes nothing.** `src/CcDirector.Core/Configuration/GatewayConfig.cs:107,169` still reads `streamMode` from config.json, but the gate that consumed it is gone: `src/CcDirector.ControlApi/GatewayStreamClient.cs:101` is now `IsEnabled => _config.IsEnabled`, and `GatewayHost.cs:455` ignores the parameter (retained only for test call sites). The XML comments in both files still describe "stream mode off" behavior that cannot happen. **Recommendation:** delete the config key and the stale comments; update the test call sites.

### Verdict

The migration is complete where it matters most - the Director command plane - and the cut was done with discipline (explicit 502 semantics, registration moved onto the Hello, proactive serve-mapping teardown, floor restores in #1498/#1509 when the cut broke fleet verbs). It is **not** complete as a system-wide statement: the launcher leg, the LAN addressing mode, the HTTP registration surface, and the verify remnants are all still standing. None of them breaks the product today; all of them will confuse the next person who reads the code and is told "tunnel-only".

---

## Section 2 - Leftover Director REST cleanup

Verdicts: **DELETE NOW** (statically unreachable or zero production references at `ff7a571a`), **DECIDE** (referenced, but the referencing path is inert or scheduled), **KEEP** (genuinely live).

### DELETE NOW

**D1. The register/heartbeat cluster in `src/CcDirector.ControlApi/GatewayClient.cs`.**
`Start()` (lines 497-515) no longer enters this code, and the heartbeat timer is never constructed (`_heartbeat` is only ever assigned null; there is no `new Timer` in the file).
- `SelectActiveUrlThenRegisterAsync` (line 563) - zero callers.
- `HeartbeatTick` (line 699) - only called by the never-created timer.
- `MaybeReRegisterOnIdentityChange` (line 792) - only called from `HeartbeatTick`.
- `MaybeKickVerify` (line 821) - only called from `HeartbeatTick`.
- The `_heartbeat` field and the `StopAsync` unregister branch guarded by `_registered` (lines 528-544) - `_registered` can never become true because `RegisterLoop` is the only place that sets it and it is no longer entered on any live path.
- The class comment at lines 15-25 describing the old lifecycle.
Reason: all unreachable from the three live roots (`Start`, `NotifySessionState`, `VerifyAsync`).

**D2. `src/CcDirector.Gateway/DirectorForwarding.cs` (whole file), plus the dead forwarder registration.** Two static constants whose consumers (`DirectorEndpointClient`, the old WebSocket proxy dial) were deleted by the cut. A repository-wide search for member access `DirectorForwarding.` finds zero hits; the only mentions left are a stale comment at `GatewayHost.cs:997` and a project-file comment. In the same breath, `GatewayHost.cs:998` still registers the YARP HTTP forwarder (`builder.Services.AddHttpForwarder()`) and nothing in the Gateway consumes `IHttpForwarder` anymore. Delete the file, the comments, and the registration (and the package reference if nothing else needs it).

**D2a. `src/CcDirector.Gateway.Contracts/DispatchContracts.cs` (whole file).** The cut deleted the Director's `/dispatch` endpoint (`DispatchEndpoint.cs` no longer exists) and no "dispatch" tunnel verb was ever created, but the contract types (`DispatchRequest`, `DispatchResultDto`) were left behind with zero references outside their own file. Delete.

**D3. `src/CcDirector.Gateway.Contracts/CockpitWsUrls.cs` and `CockpitShotUrls.cs`.** Zero production references; only their own definitions, their two dedicated test files, and one string entry in `NoCrossMachineLoopbackGuardTests.cs`. Superseded by same-origin proxying. Delete both classes, both test files, and fix the guard-list string.

**D4. The never-assigned serve-provisioner field in `src/CcDirector.ControlApi/ControlApiHost.cs`.** The `_serveProvisioner` field (line 101) is never assigned - the teardown at line 521 uses a throwaway local. Therefore the `ServeProvisioner` property (line 149) is always null, the `StopAsync` branch at lines 890-894 is dead, and the three desktop-panel rungs that read it are inert (`GatewayConnectionPanel.axaml.cs:299,387,454` - the troubleshooter auto-fix rung can never fire). Delete the field, the property, the dead branch, and the inert panel rungs. Note: keep the line-521 teardown local - that is the deliberate self-heal.

### DECIDE (inert or scheduled - do not delete blindly, do decide on an issue)

- **`RegisterLoop` / `TryRegisterAsync` / `BuildRegistrationRequest`** (`GatewayClient.cs:618,656` and below): statically reachable only from `VerifyAsync`'s 410-Gone branch (line 863), which can no longer fire because the Gateway verify endpoint is deleted. Effectively dead; classified here rather than DELETE NOW only because the reachability argument is behavioral, not static. Goes away naturally with M5.
- **`VerifyAsync` and the desktop "Verify now" button** - see M5. Removing them also unblocks deleting the `DirectorVerification` contract types (`src/CcDirector.Gateway.Contracts/DirectorVerification.cs`), which after the cut are referenced only by this inert flow.
- **`TailscaleServeSelfProvisioner`** (`src/CcDirector.ControlApi/TailscaleServeSelfProvisioner.cs`, 227 lines): only `RemoveOwnMapping` is live (the startup teardown). `EnsureMappingAsync`, `LastError`, and the reconcile logic are reachable only through the dead field of D4 and tests. Shrink the class to the teardown, or fold the teardown into a small helper and delete the rest.
- **`GatewayConnectivitySelfTest`** (301 lines): still wired to the desktop panel troubleshooter (`GatewayConnectionPanel.axaml.cs:298`), and it still checks `TailscaleServeSelfProvisioner.StatusHasMapping` (`GatewayConnectivitySelfTest.cs:137`) - a mapping the Director now never creates, so that rung will always report "no mapping" as if it were a problem. Review and rebuild the troubleshooter around tunnel health, or remove it.
- **`AddressingMode.Lan`** - see M2. Removing the mode deletes the `0.0.0.0` bind branch, the LAN resolver arm in `GatewayClient.cs:150`, and the settings plumbing in `src/CcDirector.Gateway/Api/SettingsEndpoints.cs`.
- **Gateway HTTP registration surface** (`POST /directors/register`, `POST /directors/{id}/heartbeat`, the `Source="http"` registry path) - see M3. Compatibility window, then delete together.
- **`tools/cc-director-setup-engine/TailnetResolver.cs`**: installer tooling for the Gateway tray HTTPS URL, not Director runtime. Live where it is; fold into the general de-Tailscaling only when the installer story changes.

### KEEP (checked and genuinely live)

`GatewayStreamClient`, `DirectorUpStreamHandler`, `DirectorStreamProducers` (the tunnel itself, now always on); `GatewayConnectionMonitor` (repurposed - the tunnel drives the desktop connectivity light); `GatewayEnrollmentClient` (device enrollment from the desktop panel); `InstanceRegistration` (loopback instance discovery on the same machine); `DirectorReachabilityDto`, `DoorbellRequest`, `HoldRequest` (all have live call sites); every endpoint file currently mapped by `ControlApiHost`; all web-client call sites (they target Gateway proxy routes that still exist).

---

## Section 3 - Connection resilience on bad internet

Context assessed: the owner drives while using the mobile web app over a weak phone network; the Director sits at home behind the tunnel. Three legs: phone to Gateway, Director to Gateway, and command round-trips spanning both.

### What is already good (verified)

- **Director tunnel reconnect is sound.** Automatic reconnect at 0, 2, 5, and 10 seconds (`src/CcDirector.ControlApi/GatewayStreamClient.cs:156-159`), then a supervise loop that re-dials every 5 seconds forever (line 60 and the loop at 228-239). A multi-hour Gateway outage self-heals without a Director restart. On every reconnect the Director re-sends Hello plus a full snapshot, and a quiet session's cache is kept fresh by a re-push every 10 seconds (half the 20-second staleness window, `GatewayStreamClient.cs:90,116`).
- **The tunnel got two real hardening fixes:** MessagePack framing and the 32-megabyte command-reply ceiling (section 1). Both remove classes of spurious tunnel drops under load.
- **Car Mode is now durable and idempotent** (this was rebuilt in the missing 52 commits; the pre-cut tree lost voice commands on any blip). Spoken commands are written to IndexedDB before any network work (`packages/client-core/src/carmode/pendingTurnStore.ts:24-47`), retried with exponential backoff capped at 15 seconds for the first hour and then every 5 minutes (`packages/client-core/src/carmode/turnRetry.ts:12-15`), carry an idempotency key so a re-drive acts at most once (`packages/client-core/src/carmode/carModeApi.ts:104-114`), and turns older than 30 minutes are surfaced to the owner instead of auto-fired (`turnRetry.ts:20,34-37`). The states are spoken honestly ("holding", "connection down", "Back online").
- **Dictation remains the gold standard** (durable, chunked, resumable, idempotent - `packages/client-core/src/dictation/backgroundSend.ts`), and the terminal view never blanks during an outage; it shows "reconnecting" and repaints only after a successful reopen (`packages/client-core/src/terminal/stream.ts:50,384-402`).
- **A foreground keep-warm ping** (`packages/client-core/src/net/useKeepWarm.ts:11`, every 25 seconds) keeps the direct network path warm, with an honest status pill and server-side drift detection behind it. Note this is a path warmer and an observability feature, not a liveness heartbeat - it does not detect or shorten anything about drops.

### Where it breaks, ranked by impact on the driving scenario

**R1. A mid-flight tunnel drop during a command surfaces as a raw HTTP 500, and hot-path commands have no timeout.** `GatewayHost.SendCommandAsync` (`src/CcDirector.Gateway/GatewayHost.cs:1758-1771`) awaits the hub invocation with whatever token the endpoint passed and no internal deadline and no exception boundary. The hottest endpoints pass `CancellationToken.None`: prompt (`GatewayEndpoints.cs:1281`), kill (1025), patch (1222), create (953 and 1503), interrupt (1344), escape (1358) - 19 call sites in the file. Two concrete failure modes:
  - Director drops while the command is in flight: the invocation faults, nothing catches it, the phone receives status 500 and shows "The Gateway rejected the request (error 500)" (`packages/client-core/src/api/client.ts:319-323`) - indistinguishable from a Gateway bug, and not the friendly copy that a 502 gets.
  - Director connected but wedged: with no deadline, the request hangs until the transport-level timeout (about 30 seconds by default) while the driver stares at a spinner.
  **Fix:** wrap the hub invocation in a bounded timeout (a few seconds for fire-and-forget verbs, longer for create) plus a try/catch that converts the fault into the same typed 502 the not-connected case already returns. One method, one change, every verb benefits.

**R2. Phone write calls have no timeout, so a half-open connection hangs them silently.** Only the repeating poll reads carry the 10-second cap (`client.ts:324` and the call sites at 384, 413, 905, 1069, 1101). Writes - `sendPrompt` (431), hold, kill, the queue verbs - pass no `timeoutMs`, which means on a weak network exactly the wrong calls can hang: the user-initiated ones. Worse, a hung write never flips the connection-health banner (`packages/client-core/src/connection/health.ts` only reacts to completed failures), so the app looks healthy while the send is stuck. **Fix:** give every write a timeout (15 to 20 seconds) so the failure becomes visible and the banner flips. Pair with an idempotency key (R6) before ever adding automatic retry.

**R3. The terminal WebSocket reconnects every 1.2 seconds forever, with no backoff, jitter, or cap** (`packages/client-core/src/terminal/stream.ts:39,429-433`), and has no heartbeat of its own. On a long drive through a dead zone this is the most battery- and data-hungry loop in the app, and every eventual success replays the full history from byte zero (line 397-402, full reset plus replay - correct, but expensive to do fifty times). **Fix:** exponential backoff to a ceiling of 10 to 15 seconds with jitter, reset to fast on visibility change or the health store flipping good.

**R4. Both sides of the tunnel run on default keep-alive timings.** Neither `GatewayStreamClient.cs:149-165` nor the hub options at `GatewayHost.cs:1016-1026` set keep-alive or client-timeout intervals, so the Gateway can take up to about 30 seconds to notice a silently dead Director. Combined with the 20-second cache staleness window (`src/CcDirector.Core/Configuration/GatewayConfig.cs:118`), a phone can spend half a minute sending commands that 502 one by one before the roster shows the truth. **Fix:** set explicit, slightly tighter values (for example 10-second keep-alive, 20-second client timeout) so detection roughly matches the staleness window, and consider surfacing "Director unreachable since [time]" rather than a bare 502 error string.

**R5. Car Mode attempts still have no per-attempt deadline.** The transcribe, speech, and brain-turn calls route through `gatewayFetch` (good - they now feed the health banner) but pass no `timeoutMs` (`carModeApi.ts:27,49,109`); the abort controller fires only on stop or interrupt. The durable retry (above) rescues the outcome, but a single half-open attempt can still sit in "thinking" until the driver notices. **Fix:** per-attempt timeout of 20 to 30 seconds; the existing retry machinery already handles what happens next.

**R6. Command verbs carry no idempotency key.** `DirectorCommandRouter.TrySendAsync` mints a fresh command id per attempt (`DirectorCommandRouter.cs:35-41`). Today nothing auto-retries commands, so exposure is limited to a human re-tapping after an ambiguous timeout - but that is precisely what a driver does. A re-tapped prompt double-submits. Dictation and Car Mode already solved this pattern (client-supplied idempotency key, server dedupe). **Fix:** thread a client-generated idempotency key through prompt at minimum, before implementing R1/R2 retry advice.

**R7. Structural: when the tunnel is down, sessions become unaddressable with no queue.** By design there is no fallback anymore; after the 20-second window a command gets a 502 and the roster honestly marks staleness. That is the right call versus the old silent HTTP dial, but nothing offers the driver "will retry when the Director comes back" for anything except Car Mode turns. Low urgency; worth considering a lightweight held-command pattern for prompt only, reusing the Car Mode machinery.

---

## Section 4 - Outdated documentation

Judged against the code at `ff7a571a`. The skills were partly fixed in pull request #1491; the documents below were not.

### DELETE (describe worlds that never shipped or are fully dead; no salvage value)

| Document | What is wrong |
|---|---|
| `docs/PRD-RemoteControl.md` | Describes a Supabase + Vercel + Next.js paid cloud relay with Postgres tables and WPF dialogs. None of it shipped; the product is Gateway plus tunnel, and the desktop moved to Avalonia long ago. |
| `docs/Implementation-RemoteControl.md` | The build plan for the same dead product; admits at lines 13-16 that the prototype was removed. Points at nothing recoverable. |
| `docs/CC_Gateway_Design.md` | A "Gateway" that is a Discord bot on `http://localhost:5555/api/` talking to the Director over named pipes. Shares only the name with the real Gateway. Also cross-referenced as the "original design" by `docs/architecture/gateway/GATEWAY_DIRECTOR_ARCHITECTURE.md:12`, which lends it false authority - sever that link when deleting. |
| `docs/Gateway_Dashboard.md` | A third, different "Gateway": a scheduled-jobs dashboard on `http://localhost:6060/` started by a `cc_director_service` binary that does not exist. Name-collision landmine; cross-referenced at `GATEWAY_DIRECTOR_ARCHITECTURE.md:13`. |
| `docs/plans/phase1-https-via-tailscale.md` | A plan to make Tailscale Serve the mandatory remote path - the exact opposite of what shipped - and it links to a `REMOTE_ACCESS.md` that does not exist anywhere in the tree. |

### UPDATE (living documents with confirmed-wrong sections)

**U1 (highest priority). `docs/public/api/01-control-api.md`** - the public reference linked from `docs/README.md:36`. Line 3 claims every Director hosts "the programmatic surface for everything the desktop UI can do: sessions, prompts, terminal buffers, git, handovers, repositories, settings" and that "the Cockpit, the phone clients, and agents all drive Directors through this API". Lines 27-71 document `GET/POST/DELETE/PATCH /sessions`, prompt, interrupt, buffer, git, handovers, repositories, settings, tools, dispatch, scheduler, wingman, and a WebSocket stream. The real Director floor is twelve routes (section 1). Rewrite around the floor plus "clients drive sessions through the Gateway"; the environment-variable section (lines 5-21, `CC_DIRECTOR_API` and friends) is still correct and worth keeping.

**U2. `docs/architecture/gateway/GATEWAY_DIRECTOR_ARCHITECTURE.md`** - stamped "Status: CURRENT" (line 3) and aimed at anyone touching the code, yet its thesis is the dead proxy model: "talk to each other over plain HTTP" (line 21), "proxies session-specific calls to the owning Director" (line 26), a whole proxy table (lines 98-208), "exposes that aggregate over Tailscale to your browser" (line 54). Meanwhile the correct target-state documents (`GATEWAY_DIRECTOR_TARGET.md` beside it, and `docs/architecture/DIRECTOR_DUMB_WRAPPER_TARGET.md`) describe what actually shipped. A wrong document stamped CURRENT sitting next to a right document stamped TARGET is the single most misleading state in the tree. Rewrite the transport sections around the tunnel, keep the still-true structural facts (two executables, port ranges, source layout), remove the two dead cross-references, and make it the one canonical architecture page.

**U3. `docs/public/getting-started/02-installation.md`** - line 185 says the Director "opens its own Tailscale Serve front door for remote access, and verifies its advertised address actually answers before registering"; line 189 says the single remote path is "a Tailscale Serve HTTPS mapping ... which each Director now provisions and self-heals for itself". Both false: the Director only tears mappings down (`ControlApiHost.cs:516-524`) and the verify machinery was deleted (#1511). The "Tailscale (optional) - remote access" rows at lines 7 and 15 are also misleading now that Tailscale confers no remote-access benefit (pull request #1506 already removed it from the installer preflights).

**U4. `docs/remote-experience/remote-experience-plan.md`** - the transport premise throughout (lines 5-19) is per-Director and per-session Tailscale Serve URLs, which no longer exist; clients reach the Gateway and the Gateway relays over the tunnel. The three-modes product vision (desktop, mobile supervisor, car voice - lines 22-30) is still good; update the transport framing. Review the sibling `your-suggestions.md` at the same time - it shares the premise.

**U5. `.claude/skills/cc-settings-api/SKILL.md`** - lines 56-72 still teach the advertised-address model: pass `--advertised http://THIS-HOST:7879` because "the gateway cannot call back" to loopback, plus "the Tailscale auto-detection is Windows-only". The Gateway never calls back at all now. Rewrite the "connect this Director to a Gateway" section around dial-out; the settings mechanics in the rest of the skill are fine. (For contrast, `.claude/skills/dev-throttle/SKILL.md` was already corrected by #1491 and now matches the code; its only blemish is a changelog caveat about verbs #1490 broke, which #1498/#1509 have since restored.)

**U6. Code-comment rot (small but worth a sweep):** the `ControlApiHost` class comment and startup log line still name Tailscale Serve as the remote path (section 1, M6), and `GatewayClient.cs:15-25` still describes the deleted register/heartbeat lifecycle.

### Pattern: mission scratch notes intermixed with living architecture

`docs/architecture/` mixes durable reference with dated one-run mission and handover notes (`gateway-cleanup-mission-2026-07-11.md`, `gateway-cleanup-phase0-execution-plan.md`, `gateway-cleanup-phase0-tunnel-protocol.md`, `network-diagnostics-mission-2026-07-13.md`, `car-mode-mission-2026-07-11.md`, `cockpit-improvement-mission-2026-07-10.md`, and more), plus the whole `docs/new_architecture/` phase tree. These were the working papers of the migration - valuable as history, wrong as reference. Move dated mission, phase, and handover notes into `docs/archive/` and leave exactly one authoritative tunnel-only architecture page (U2). `docs/SessionIntercommunication.md` (a design for issue #705) appears superseded by the shipped fleet messaging and deserves a status stamp.

---

## Section 5 - Repository cleanup

### Committed on origin/main (act via pull request)

1. **`tools/cc-browser-archived/` - DELETE.** 51 tracked files. Not in `tools/registry.json`; its only inbound reference is the to-do at `tools/cc-browser/docs/PRD-browser-connections.md:1056` ("Remove cc-browser-archived after confidence period"). The confidence period is over.
2. **`OVERNIGHT-CHARTER.md` (repository root) - ARCHIVE or DELETE.** A one-time overnight-run instruction sheet hard-coding a throwaway worktree path, a feature branch, and a session id. Nothing references it. Root-level clutter.
3. **Retired tools, paired registry decision:** `tools/cc-comm-queue/` (its own registry entry at line 247 says "RETIRED - no longer shipped"), `tools/cc-reddit/` (depends on the retired cc-browser), and `tools/cc-browser/` itself (superseded by cc-playwright per registry line 87). Each is dormant-but-catalogued; remove the directory and the `registry.json` entry together, or deliberately keep them listed. Do not delete the directories alone - that creates registry drift.
4. **`phone/` (the native phone client) - make the retirement decision explicit.** The project decision was to retire the native app in favor of the mobile web app, but the repository shows no trace of that: 181 tracked files, its own solution file, `scripts/deploy-phone.ps1`, and `.github/workflows/docs-drift.yml` still watches `phone/CcDirectorClient/**`. Either delete the directory plus its script and workflow trigger, or record in the tree that it is frozen. Right now a contributor cannot tell.
5. **Repository weight (note, not urgent):** the largest tracked files are test fixtures - `src/CcDirector.Core.Tests/TestData/claude-stray-today.expected.json` at 19.7 megabytes, plus two more at 13.2 and 12.2 megabytes (about 45 megabytes of expected-output JSON), and `claude-stray-today.bin` is byte-identical to `claude-session-huge-50.bin`. Candidates for trimming or generating; also `docs/features/voice-mode/REPORT.html` at 3.2 megabytes.
6. Confirmed clean: no `nul` files anywhere, no tracked `bin/`, `obj/`, `dist/`, or publish output, no tracked `.orig`/`.bak`/`.tmp` files, and `.gitignore` coverage is thorough. `docs/archive/`, `docs/spikes/`, `docs/problems/`, and `docs/mockups/` are small, intentional, and fine to keep. The root `package.json` and `eslint.config.js` are a legitimate npm workspace root, not clutter.

### The live working checkout on this machine (local cleanup, no pull request)

7. **The checkout was 52 commits behind origin/main** at review start, with two modified tracked files and 14 untracked documents sitting in `docs/architecture/`. Under the project's own convergence rule the resting state is checkout equal to origin/main and clean. Recommend a `git pull` and a sweep.
8. **`docs/architecture/car-mode-mission-2026-07-11.md.local-bak` - DELETE.** An editor backup file.
9. **13 untracked mission and cleanup scratch documents** (`gateway-cleanup-phase2-repoint-design.md`, `gateway-cleanup-phase1-*`, `*-mission-2026-07-11.md`, and others): commit the ones worth keeping (several are the best record of how the migration was designed) or delete them. Today they are neither tracked nor ignored, so they pollute the status of every session on this machine.
10. **Local-only directories never committed and already ignored** - safe to sweep for disk space: `artifacts/` (about 40 one-time test-run captures), root `mobile/` (a local debugging harness superseded by `apps/mobile` - do not confuse the two), `playground/`, `scheduler/`, `mcps/`, `tools/__pycache__/`, `tools/.pytest_cache/`, and stale numbered executables in `local_builds/` (about 190 megabytes).

---

## Suggested order of work

1. R1 and R2 (command timeout plus typed error, phone write timeouts) - small changes, largest effect on the driving experience.
2. Delete list D1-D4 plus the M5 verify remnants - one focused cleanup pull request, all statically safe.
3. U1 and U2 (the public API reference and the CURRENT-stamped architecture page) - the two documents actively misleading people today.
4. M2 (LAN addressing mode) and M3 (HTTP registration surface) - close the remaining inbound-surface questions with explicit decisions.
5. The five document deletions, the scratch-note archive sweep, and the repository cleanup items 1-4.
6. R3-R6 as a mobile-resilience follow-up mission.
