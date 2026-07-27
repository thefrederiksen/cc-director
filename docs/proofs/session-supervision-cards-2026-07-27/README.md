# Proof record: session supervision on the cards (internal#625, phases 1 and 2)

Date: 2026-07-27. Everything below was produced live against a Gateway and a Director built
from this branch - no mock data, no hand-edited numbers.

## The rig

- Gateway: `dotnet run --project src/CcDirector.Gateway -- --port 7979` from this branch, with an
  isolated `CC_DIRECTOR_ROOT` (a scratch directory) and `CC_GATEWAY_NO_TAILSCALE=1` so it could
  not touch the production Gateway's front door.
- Director: `cc-director18.exe` built from this branch (`local-build-avalonia.ps1 -Slot 18`),
  launched through its own scheduled task, same isolated root, pointed at the rig Gateway.
- Clients: the real Cockpit and mobile dev builds, proxied at the rig Gateway, in a headless
  browser holding the rig's device key.

## What was driven

One claude session ("Supervision proof", number 102) in a scratch git repository with dirty
files. The folder-trust dialog was answered through the Cockpit terminal, then two prompts were
sent through the Gateway with a deliberate 90-second wait between them.

## What the wire reported (read from GET /sessions on the rig Gateway)

- `turnCount` climbed 1 (startup settle), 2, 3 - one flip to WaitingForInput per turn.
- `cumulativeIdleSeconds` finished at 109 - the deliberate 90-second gap plus delivery time,
  summed only from CLOSED waiting stretches.
- `waitingSince` re-stamped at the final turn end; `createdAt` real.

## What the cards showed (screenshots in this folder)

- `cockpit-rail.png` - the roster card: red needs-you state, "started 01:01  open 5m  idle 4m
  turns 3", the "3 chg" badge and the red "waiting 2m" label all coexisting. Idle reads 4m, not
  109 s: the closed total PLUS the currently open stretch, added client-side and ticking live.
- `mobile-roster.png` - the same facts on the phone card via the same shared formatter:
  "started 01:01  open 3m  idle 2m  turns 3" (taken earlier in the same run).
- `after-trust.png` - the session's terminal in the Cockpit at boot, roster card at
  "started 01:01  open 0m  idle 0m  turns 0", proving the zeros are measured, not defaults.
