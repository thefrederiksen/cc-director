# MTR fix H2 — the fleet session-number allocator was process-global, not tenant-scoped

INTERNAL working note. Branch `fix/audit-h2-allocator`. Gap: audit H2 (audit-a),
`src/CcDirector.Gateway/Discovery/FleetSessionNumberAllocator.cs`.

## The gap as handed to this seat

`FleetSessionNumberAllocator` (the Gateway authority for the 100-999 rail session numbers, issue
#1292) kept one process-global store keyed by BARE session/director ids: `_bySession`, `_inUse`, the
100-999 pool, and the lock. `Allocate` / `Adopt` / `Release` / `ReleaseForDirector` took bare ids, and
the `OnDirectorRemoved` subscriber in `GatewayHost` DROPPED `DirectorRemoval.Tenant` before calling
`ReleaseForDirector(removal.DirectorId)`. A director id and a session id are unique only WITHIN a tenant,
so on the hosted multi-tenant Gateway two tenants collide.

## Verify-first — the leak reproduces on current main

Wrote two reproduction tests against the current bare-id API and ran them GREEN on main (leak is real):

- **Cross-tenant Director removal.** Two tenants each own a Director under the same id `dir-shared`,
  each with a session. `ReleaseForDirector("dir-shared")` (fired for tenant A's removal) freed BOTH
  tenants' numbers — tenant B's rail number vanished and could be re-handed, surfacing as a duplicated
  rail number for the innocent account.
- **Global pool exhaustion.** One tenant allocating all 900 numbers left a second tenant with `null`
  (no number) — the pool was one shared 900-number space, so one busy account starved every other.

The same shape also lets tenant A's `Allocate` idempotently read tenant B's assignment for a shared id,
and tenant A's `DELETE /session-numbers/{id}` free tenant B's number for an identically-named session.

## Fix — partition every piece of state by tenant

- **Allocator.** State now lives in a per-tenant partition (`Dictionary<TenantId, TenantPool>`, each
  holding its own `BySession` map, `InUse` set, and 100-999 pool). Every method takes a `TenantId` and
  only ever reads/writes that tenant's own partition. Each tenant draws from its OWN pool, so exhaustion
  is confined to the tenant that caused it. Self-host resolves to `TenantId.Local` — one partition,
  behavior unchanged. Log lines use `TenantId.ToLogString()` (no raw account id in logs).
- **Endpoints** (`GatewayEndpoints`). `POST /session-numbers/allocate` and `DELETE /session-numbers/{id}`
  resolve the caller's tenant SERVER-SIDE from the authenticated device key (`ResolveReadTenant`, never
  from the request body). No bound tenant on hosted → 403 (deny by default), never the Local partition.
  The `/sessions` aggregation's `Adopt` loop stamps the request's own tenant (`reqTenant.Value`), the
  same tenant that filtered the roster it iterates.
- **Removal subscriber** (`GatewayHost`). Threads `removal.Tenant` straight into
  `ReleaseForDirector(removal.Tenant, removal.DirectorId)` — the tenant is no longer dropped. The
  allocator's signature now REQUIRES the tenant, so it cannot be forgotten.

Server-resolved tenant only; self-host/`TenantId.Local` paths stay correct off-hosted.

## Reproduced-real / revert-proof

- `FleetSessionNumberAllocatorTenancyTests` — director-removal, release, idempotent-read, and
  pool-exhaustion isolation, each with a destructibility control (the owner's own op really frees /
  hands out / exhausts) beside the isolation assertion.
- `HostedSessionNumberRouteTenancyTests` — two device keys on two account tenants over REAL HTTP through
  the REAL auth middleware: same session-id string allocates independently per tenant, one tenant's
  DELETE never frees the other's, and an unbound-tenant allocate is 403.
- Revert-proof CONFIRMED by experiment: collapsing the allocator back to one shared pool reddened all
  four isolation assertions while the destructibility controls stayed green; restoring the partition
  greened them. The existing `FleetSessionNumberAllocatorTests` were updated to the tenant API.

## Gates

- Build `CcDirector.Gateway` + `CcDirector.Gateway.Tests`: 0 warnings, 0 errors.
- Targeted tests + the two solo tenancy filters (`OnOneRoute_TheCredentialAloneDecidesWhetherATenantScopeExists`,
  `The_roster_and_the_commands_agree`): all green.

## Merge note

Touches `GatewayHost.cs` and `GatewayEndpoints.cs` — both SERIALIZE at merge.
