# MTR H1 - tenant-scope the hosted director lookups

Branch `fix/audit-h1-listdirectors`, pull request #2007.

## What H1 is

Three hosted, per-tenant routes read the FLEET-GLOBAL `DirectorRegistry.ListDirectors()` and then
keyed by a BARE director id / machine name, where the registry's tenant-scoped
`ListDirectors(TenantId)` overload already exists. The registry key is `(tenant, id)`; a director's
id is chosen by the client and machine names are not unique across tenants, so those collisions
turned the fleet-global reads into cross-tenant leaks and a crash:

1. `GET /sessions` - filtered the fleet-global list by bare `director=`/`machine=`, so a
   cross-tenant duplicate survived into the caller's roster and leaked its machine name and its
   "unreachable" reachability row in the `?envelope` response.
2. `POST /sessions/voice-mode/all` - built a dictionary keyed by the bare `DirectorId`, so a
   cross-tenant duplicate made `ToDictionary` throw a 500 - one tenant's director denying another
   tenant's whole voice toggle.
3. The cron target resolver - was fed the fleet-global list, so a machine-name match could select
   another tenant's director on the same machine and persist that cross-tenant `DirectorId` in the
   caller's `CronRunRecord.TargetDirectorId`.

All three now resolve through the tenant-scoped lookup, confined to the caller's own partition.
Self-host (one tenant, everything `Local`) is byte-identical.

## The Codex CHANGES-NEEDED residual (this increment)

`GET /sessions` and the cron resolver were correct and are unchanged. ONE residual remained on
`voice-mode/all`: its `machineByDirector` map was scoped to `ListDirectors(reqTenant)`, but the
shared role-universe helper `GatewayEndpoints.FleetByDirector` (also folded by `GET
/sessions/{sid}` and `/exes/list`) still looped the fleet-global `registry.ListDirectors()` AFTER
that scoped map. So a colliding cross-tenant director id still entered the fold's director
universe.

The push-store read (`PushedSessionStore.TryGetFresh`) is tenant-keyed, so it already kept another
tenant's DATA out - which is why reverting the helper alone did not redden the end-to-end
`voice-mode/all` tests (the 500 came from the already-fixed `machineByDirector.ToDictionary`). The
residual is the ID SET: iterating the fleet-global list projects every tenant's director ids into
one bare-id set, so ANOTHER tenant's registered id could decide which of the caller's own cached
rosters the fold surfaces - a cross-tenant coupling, correct only by the push store's key being the
sole boundary.

### Fix

`FleetByDirector` now builds its universe from `registry.ListDirectors(tenant)` (the `tenant`
parameter every caller already passes). The universe still spans machines - a Worker's Manager can
live on another machine - but every such director is in the caller's OWN tenant (a Worker and its
Manager belong to the same account), so spanning machines never means spanning tenants. The doc
comment (which previously argued the fleet-global universe was deliberate) is corrected.

### Coverage (revert-proof)

`FleetByDirectorTenantUniverseTests` (new, unit-level on the helper):

- `FleetByDirector_universe_is_bounded_to_the_callers_registered_directors` - the caller (Acme)
  holds a fresh pushed roster under an id its REGISTRY no longer lists while only another tenant's
  (Globex) registry lists that id. With the fix the id is absent from Acme's fold; REVERTING the
  helper to `ListDirectors()` drags Acme's orphan cache back in via Globex's registered id and this
  goes RED. Verified: reverting reddens exactly this test, the data-isolation test stays green.
- `FleetByDirector_never_folds_another_tenants_cache_under_a_shared_id` - belt: with both tenants
  owning the same id, Acme's fold surfaces only Acme's session, never Globex's (the tenant-keyed
  push read is the data boundary).

The existing `CrossTenantDuplicateDirectorIdTests` (the two H1 e2e repros) and
`VoiceModeAllEndpointProofTests` stay green.

## Verification

- Build gateway + tests: 0 warnings, 0 errors.
- Targeted run green: FleetByDirector universe, CrossTenantDuplicateDirectorId,
  VoiceModeAllEndpointProof, CronResolverCrossTenant, RegistryDirectorTargetResolver,
  WingmanVoiceSidPathTraversal (21 tests).
- Two tenancy filters run SOLO: `PushedSessionStoreTenantIsolationTests` (5),
  `DirectorRegistryTenantKeyTests` (4).
- Revert-proof confirmed by hand (revert helper -> universe test RED -> restore).

## Files

- `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` - `FleetByDirector` universe + doc.
- `src/CcDirector.Gateway/Api/GatewayWingmanVoiceEndpoint.cs` - comment on the tenant-scoped fold.
- `src/CcDirector.Gateway.Tests/FleetByDirectorTenantUniverseTests.cs` - new coverage.

Serializes on `GatewayEndpoints` / `GatewayHost`.
