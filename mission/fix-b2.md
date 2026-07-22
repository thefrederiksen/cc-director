# MTR fix-b2 — hosted process-control deny

INTERNAL working note. Branch `fix/audit-b2-shutdown-deny`.

This seat closes THREE hosted process-control holes on the shared Gateway, not one. Part 1 (the original
`POST /shutdown` deny) is below unchanged. Parts 2 and 3 (the sibling scan's CRITICAL force-kill hole and the
`POST /directors` local-process launch) were added on top after rebasing onto current origin/main — the rebase
also picked up the Gap C+D redo that green-lights the three `SessionServingLoopIsolationTests` which failed on
the stale base (unrelated to B2; confirmed 4/4 after rebase).

## Part 2 (CRITICAL) — hosted `DELETE /directors/{id}` force-kill could kill ANY process on the shared host

### Finding: CONFIRMED

`DELETE /directors/{id}` with `force=true`, when the graceful tunnel shutdown does not succeed, ran
`Process.GetProcessById(director.Pid).Kill(entireProcessTree: true)` (GatewayEndpoints.cs, force branch).
`director.Pid` is a number the Director itself supplied in its `Hello`/`RegisterFromStream`. On the HOSTED
Gateway the Director is a REMOTE process reached over the tunnel — it is NOT a process on the Gateway host —
so that pid, resolved against the SHARED host's local process table, names whatever unrelated process happens
to hold that number: the Gateway process itself, another tenant's container, anything. The route is
tenant-scoped for RESOLUTION (`TryResolveOwnedDirector` — a caller can only name a Director in its own
partition), but the pid it then kills is a bare integer with no relationship to the caller's tenant on the
host. So any authenticated tenant could register a Director claiming an arbitrary pid and have the Gateway
force-kill that host process for them. This is strictly worse than B1's `/shutdown`: it can kill a chosen
process, not just take the whole Gateway down.

### Fix

Inside the `if (body.Force)` branch, refuse on hosted BEFORE any local process lookup:
`if (GatewayHostedMode.IsHosted) return 404 { "error": "force-killing a Director by process id is not
available on the hosted Gateway" }`. The graceful tunnel shutdown attempted first is already tenant-scoped and
is the ONLY stop a hosted caller gets — there is no host-local process for the Gateway to reach on their
behalf. Self-host is byte-identical: there the Director really is a local process and the owner's force-kill
still works.

Why an in-handler guard, not the verb-less `HostedRouteDeny` primitive: `DELETE /directors/{id}` also carries
the LEGITIMATE, tenant-scoped GRACEFUL remote shutdown (the tunnel `shutdown` verb), which must keep working
on hosted. Only the local force-kill-by-pid is unsafe, so the deny is scoped to that branch, not the route.

The real kill was extracted to a private `DefaultForceKillProcessTree(pid)` and reached through an injected
seam `forceKillDirectorTree` (wired from `GatewayHost.OnForceKillDirector`, read at REQUEST time like
`OnShutdownRequested`). Production passes null → the real kill runs. The proof injects a recorder so "did the
force-kill reach the process by that pid" is a DIRECT assertion, not an inference from a status code.

## Part 3 — hosted `POST /directors` launched a host-local process

### Finding: CONFIRMED

`POST /directors` `ShellExecute`s a `cc-director.exe` on the GATEWAY's OWN machine and polls for it to
register. On self-host that is the desktop spawning a local Director. On the shared hosted Gateway it starts an
arbitrary process on shared infrastructure at any authenticated tenant's request, with no per-tenant meaning
and no owner check.

### Fix

In-handler guard at the top of the handler: `if (GatewayHostedMode.IsHosted) return 404 { "error":
"launching a Director is not available on the hosted Gateway" }`. Self-host reaches the launch below
byte-identically. Again an in-handler guard rather than the `HostedRouteDeny` primitive, because the
tenant-scoped `GET /directors` list SHARES this exact path, and that primitive refuses EVERY verb on a path —
it would take the list route off the air on hosted too. (The `/exes/slots/{n}/build-start` process launch is
H7's scope and is deliberately untouched here.)

## Part 2+3 proof

`src/CcDirector.Gateway.Tests/HostedProcessControlDenyTests.cs` — drives a REAL hosted `GatewayHost` over REAL
HTTP.

1. `Hosted_force_kill_cannot_reach_the_process_by_client_supplied_pid` — spawns a REAL long-lived process
   standing in for "any process on the shared host", registers a `tenant-b2` Director claiming its pid, and an
   authenticated non-owner `DELETE /directors/{id} {force:true}` gets EXACTLY the 404 refusal; the injected
   force-kill seam is NEVER reached (no pid handed to a kill) and the stand-in process is still alive.
2. `Selfhost_force_kill_still_reaches_the_process_by_pid` — the control: hosted OFF, the force-kill reaches the
   seam with EXACTLY the client-supplied pid (200 `{ killed }`). Proves the deny is hosted-only.
3. `Hosted_director_launch_is_refused` — hosted `POST /directors` gets the 404 launch refusal.
4. `Selfhost_director_list_still_serves_and_is_not_shadowed_by_the_deny` — on hosted `GET /directors` (the
   tenant-scoped list, same path) still answers 200, proving the launch deny did not shadow the list route.

Revert-proof: deleting BOTH in-handler hosted guards turns tests 1 and 3 RED (seam reached / launch runs) while
the two self-host controls (2 and 4) stay GREEN — verified by disabling both guards, rebuilding, and running.

## Part 2+3 verification

- `CcDirector.Gateway` build: 0 warnings, 0 errors. `CcDirector.Gateway.Tests` build: 0 warnings, 0 errors.
- `HostedProcessControlDenyTests`: PASS 4/4. `HostedShutdownDenyTests`: PASS 2/2.
- Solo `Tenancy.HostedRouteDeny*`: PASS 20/20. Solo `DirectorRouteTenantScopingTests`: PASS 12/12.
- `DirectorRemovalTenantScopeTests`: PASS 3/3. `SessionServingLoopIsolationTests`: PASS 4/4 (post-rebase).

NOTE: touches `GatewayEndpoints`/`GatewayHost` — serializes at merge with other Gateway-endpoint work.

---

## Part 1 — hosted /shutdown deny (original finding, unchanged)

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
