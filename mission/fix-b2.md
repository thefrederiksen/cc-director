# MTR fix-b2 — hosted /shutdown deny

INTERNAL working note. Branch `fix/audit-b2-shutdown-deny`.

## Claim handed to this seat (audit finding, GatewayEndpoints.cs:276)

`POST /shutdown` is mapped on EVERY Gateway with NO hosted refusal and NO tenant/owner check, and
invokes a process-wide shutdown. So on the hosted Gateway — shared infrastructure serving every
tenant — any authenticated tenant could `POST /shutdown` and take the Gateway down for everyone.

## Finding: CONFIRMED — the route was reachable and process-fatal on hosted

`app.MapPost("/shutdown", ...)` (GatewayEndpoints.cs, ~276) was mapped on the ungrouped builder in
BOTH modes. It calls the host `requestShutdown` handler, which ends the whole Gateway process
(`GatewayWorker`/`GatewayService` -> `Environment.Exit(0)`), issue #880 watchdog hard-exits
regardless.

The route sits behind the global auth gate, so it is not anonymous — but on hosted the ONLY accepted
credential is a per-device key, and every per-device key is bound to SOME tenant (MH-2,
`AuthMiddleware.HasValidToken`). So "authenticated" on hosted means "any tenant", and the route had
no owner check on top of auth. A single tenant's device key could end the process for all tenants.

## Fix

`POST /shutdown` now maps through the shared refusal primitive
`Tenancy.HostedRouteDeny.ExclusiveGroup(app, "/shutdown", ShutdownHostedDenial())` — the same
boundary #1904 adopted for `/vault`:

- **On hosted** the handler is NEVER mapped; a verb-less catch-all refusal claims `/shutdown` (and
  anything beneath it) and answers 404 with `{ "error": "shutdown is not available on the hosted
  Gateway" }`. No binding step, no request shape reaches a handler.
- **Off hosted** the real handler maps byte-identically to before — the single owner's self-update
  helper `POST /shutdown` still works (it is the loopback floor for the local launcher; see
  `TunnelShutdownHandoverProofTests`, which confirms POST /shutdown stays local).

`ExclusiveGroup` (not per-route `Group`) because `/shutdown` owns its prefix outright, so the one
catch-all also covers any process-control route added beneath `/shutdown` later without a fresh deny.
The `HostedDenial` payload's `unDenyInstruction` records that lifting this needs a scoped, authorized
process-lifecycle meaning first (per-deployment operator authorization, never a per-tenant device
key) — not a bare removal.

## Sibling process-control routes scanned (reported, not this seat's to fix — all already guarded)

- `MachineEndpoints` `POST /{machine}/director/restart` and `/director/stop` — mapped through the
  `HostedRouteDeny.ExclusiveGroup` machine handle (`MapMachineRoutes(HostedDenyGroup app, ...)`):
  refused on hosted.
- `SettingsEndpoints` `POST /gateway/brain/restart` — mapped through the `HostedRouteDeny.Group`
  owner-settings handle: refused on hosted.
- `GatewayWingmanVoiceEndpoint` `POST /sessions/{sid}/wingman/voice/stop` — session-scoped narration
  stop, not process control; already tenant-scoped.
- The `Environment.Exit(0)` sites (`GatewayService`/`GatewayWorker`) are downstream of the shutdown
  signal, not separate HTTP routes.

So `/shutdown` was the only unguarded process-control route on hosted.

## Proof added

`src/CcDirector.Gateway.Tests/HostedShutdownDenyTests.cs` — drives a REAL hosted `GatewayHost` over
REAL HTTP. `OnShutdownRequested` is wired to a recorder that flips a flag WITHOUT tearing the host
down, so "did the request reach the process-wide shutdown handler" is a DIRECT assertion, not an
inference from a status code.

1. `Hosted_shutdown_is_refused_and_the_handler_is_never_reached` — an authenticated non-owner
   (device key bound to `tenant-b2`) `POST /shutdown` gets EXACTLY the refusal (404,
   `application/json`, the deny message) and the shutdown handler is never reached.
2. `Selfhost_shutdown_still_reaches_the_handler` — the control: hosted OFF, the shared token drives a
   real shutdown request through to the handler (200 `{ shuttingDown }`, recorder flips). Proves the
   deny is about the hosted branch and did not leak onto self-host.

Revert-proof: pointing the mapping back at `app.MapPost("/shutdown", ...)` turns test 1 RED (handler
maps again, 200, recorder flips) while the self-host control stays green — verified.

## Verification

- `CcDirector.Gateway` build: 0 warnings, 0 errors.
- `CcDirector.Gateway.Tests` build: 0 warnings, 0 errors.
- `HostedShutdownDenyTests`: PASS 2/2.
- Solo `Tenancy.HostedRouteDeny*`: PASS 20/20.
- Solo `HealthzTenantLeakTests` (GatewayEndpoints hosted branch): PASS 2/2.

NOTE: touches `GatewayEndpoints` — serializes at merge with other Gateway-endpoint work.
