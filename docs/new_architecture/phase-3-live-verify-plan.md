# Phase 3 plan: live verify with real binaries

**Driven by:** the controller (`c9f9a8e3`) directly - verification + safety-critical. **Do NOT commit.**

## Safety rails (NON-NEGOTIABLE)
- **`CC_GATEWAY_NO_TAILSCALE=1`** set in the environment of BOTH the test Gateway and the slot-5 Director BEFORE they start. This is checked in both provisioner constructors (`TailscaleServeProvisioner.cs:96`, `TailscaleServeSelfProvisioner.cs:79`) and hard-disables ALL `tailscale serve`. tailscale.exe IS on PATH on this machine, so WITHOUT this env var the test Gateway would grab the production `:443` front door on StartAsync - the exact outage to avoid. It is the product's own sanctioned test kill switch (`TestEnvironment.cs`).
- **`CC_DIRECTOR_ROOT=<temp>`** set for both processes -> all config/token/logs/instances relocate to the temp dir; production `%LOCALAPPDATA%\cc-director` is untouched, and the two processes discover each other + share the gateway token in the isolated root.
- Test Gateway on a **non-default port** (NOT 7878) via the DEV CONSOLE host (`dotnet run --project src/CcDirector.Gateway -- --port <p>`) - no tray, no self-update.
- **SLOT 5 ONLY** for the Director (`local_builds\cc-director5.exe`). Never touch the owner's Directors (main, slots 1-4) or the prod Gateway.
- Teardown via graceful **`POST /shutdown`** (Bearer token) to the Director then the Gateway. NEVER force-kill; NEVER `Stop-ScheduledTask` a live Director.

## The recipe
1. Build slot-5 Director: `scripts\local-build-avalonia.ps1 -Slot 5` -> `local_builds\cc-director5.exe`. (Running.)
2. Isolated env (both processes inherit): `CC_DIRECTOR_ROOT=<temp>`, `CC_GATEWAY_NO_TAILSCALE=1`.
3. Start dev Gateway: `dotnet run --project src/CcDirector.Gateway -- --port 7900` (background). Token at `<root>\config\director\gateway-token.txt`.
4. Point the Director at it: write `<root>\config\config.json` `{ "gateway": { "url": "http://127.0.0.1:7900", "streamMode": true } }` (token auto-resolves from the shared root). Launch `cc-director5.exe` via the `cc-director-launch` scheduled task (svchost parentage per CLAUDE.md rule 0b) with the isolated env; read its Control API port from its log.

## Verify checklist (the Phase 3 acceptance)
- [ ] V1 Gateway healthy: `GET http://127.0.0.1:7900/healthz` 200.
- [ ] V2 Director stream connects: Gateway log shows `[DirectorHub] Hello: director=... bound` (the Director dialed OUT and bound its connection). `GET /directors` (Bearer) lists the slot-5 Director as state-reporting.
- [ ] V3 Roster served from the pushed stream cache: `GET /sessions` (Bearer) returns the Director's sessions; Gateway log shows `served=pushed-cache` (NOT a pull) for that Director.
- [ ] V4 Command DOWN the stream - create: `POST /directors/{id}/sessions` with a HARMLESS RawCli `cmd.exe` agent (no claude grandchild). The Gateway routes it down the stream (`DirectorCommandRouter`); a real session appears on the Director. Confirm via the Gateway log (`stream status=Ok`) + the session in `/sessions`.
- [ ] V5 Command DOWN the stream - prompt: `POST /sessions/{sid}/prompt` -> routed down the stream -> lands in the cmd.exe session (echo a marker, read it back via the buffer/terminal).
- [ ] V6 Gateway owns state+color: the `/sessions` DTO carries Gateway-computed `EffectiveColor` + `StateLabel` + `TriageBucket` for the streamed session.
- [ ] V7 (stretch) mobile `/m` + Cockpit `/c` load the session's terminal via the Gateway relaying the Director stream.

## Teardown
`POST http://127.0.0.1:<directorPort>/shutdown` (Bearer) then `POST http://127.0.0.1:7900/shutdown` (Bearer). Delete the temp root. Confirm no `cc-director5.exe` and no stray Gateway remain (by path).

## Fallback / stuck-detector
The stream command + state+color logic is ALREADY proven at the library level: 1468 Gateway.Tests (incl. the in-process real-`GatewayHost` + real-SignalR `StreamCommandTests`/`StreamIntegrationTests`) + 2928 Core.Tests. If the Avalonia headless launch under Task Scheduler proves flaky (GUI app, known first-run resource-resolution issues), STOP after 3 attempts, document it, and hand the EXE-level click-verify to the owner's interactive machine - the library-level proof stands regardless.
