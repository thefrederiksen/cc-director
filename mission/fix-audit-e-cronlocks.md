# Fix: audit-e - process-global admission guards keyed by a bare / tenant-relative id

Gap: MED (audit-e). Two process-global in-memory admission guards on the hosted Gateway were keyed by a
bare, caller-owned or tenant-relative id, so one tenant's admission could refuse (or leak into) another
tenant's. Reproduced first, then partitioned both guards by the server-resolved tenant.

## Root cause

Both guards live on singletons that every tenant's request shares, yet each keyed its state by an id that
is NOT globally unique across tenants:

1. **Cron overlap guard** (`CronEngine`). The in-flight set was `HashSet<string>` keyed by the bare cron
   job `Id`. A cron job's id is tenant-relative: `CronJobStore` mints a short `cj_` id whose uniqueness it
   checks only THROUGH the tenant query filter, and the legacy import preserves caller-supplied ids, so two
   tenants can hold a job with the SAME id (the database identity is `(TenantId, Id)`). With `PreventOverlap`
   on, tenant A's in-flight run held the key `cj_xxxx`; tenant B's run-now of B's OWN `cj_xxxx` job then hit
   `TryEnterFlight("cj_xxxx")` = false and was rejected as `SkippedOverlap`. On hosted the scheduled sweep is
   skipped, so the reachable path is run-now, which runs inside the request's tenant scope.

2. **Work-list machine drain slot** (`WorkListRunnerManager`). The single-drain slot was
   `Dictionary<string,string>` keyed by the caller-controlled `machineKey` (a request body field, or a cron
   job's target machine). Two tenants can present the SAME key. Tenant B draining `machineKey` "shared-name"
   made tenant A's own drain on "shared-name" refuse with `RefusedMachineBusy`, and the 409's `ActiveList`
   returned B's list name to A - a mutual denial plus a cross-tenant name leak.

## Fix

Both guards are now PARTITIONED BY the server-resolved tenant; a caller's admission only ever conflicts
within its own tenant, and no 409 / `ActiveList` ever names another tenant's resource.

- **`CronEngine`**: in-flight set is now `HashSet<(TenantId, string)>`. The engine takes a
  `resolveTenant: () => _tenantPass.Current` seam - the same background-loop/request tenant seam its notifier
  already reads, and exactly the tenant the store scoped the job read to. The tenant is resolved once per
  fire (up front, before the first await) and used for both enter and exit; an unresolved scope fails loud
  (deny-by-default), never defaults.
- **`WorkListRunnerManager`**: state is now `Dictionary<TenantId, Dictionary<string,string>>` (inner map
  keeps the machine key's case-insensitive comparison). `TryAdmit` / `Complete` / `ActiveList` take the
  tenant. The REST caller (`WorkListRunnerEndpoints`) passes the request's already-server-resolved
  `reqTenant`; the cron caller (`DirectorCronWorkListRunner`) reads the same `() => _tenantPass.Current` seam.
- Log lines use `TenantId.ToLogString()` (no raw account tenant id in logs).

Self-host is one tenant (`TenantId.Local`) - one partition, behaviour byte-identical. Both new seams default
to `() => TenantId.Local` when omitted, so the existing single-tenant tests and wiring are unchanged.

## GatewayHost.cs touched (NOTE - serializes at merge)

`GatewayHost.cs` was edited at two construction sites only (the `CronEngine` and `DirectorCronWorkListRunner`
`resolveTenant` wiring). `GatewayEndpoints.cs` was NOT touched.

## Verify-first + revert-proof

Reproduced on a single engine / single manager (production shape - one singleton serves every tenant):

- `CronEngineTenancyTests.Run_now_for_one_tenant_is_not_blocked_by_another_tenants_same_id_job` - two tenants
  seeded (via the legacy import, which preserves the id) with the SAME `cj_shared` over one database and one
  ambient tenant context; A parks in flight, B fires its own same-id job -> `Fired`.
- `WorkListRunnerManagerTenancyTests` - two tenants admit the SAME machine key without refusing each other,
  and `ActiveList` never reveals another tenant's list.

Revert-proof: collapsing each guard's tenant back to a constant (the bare-id behaviour) reddens exactly the
4 cross-tenant assertions while the 2 same-tenant guard tests stay green (the within-tenant overlap /
same-machine refusal is preserved).

## Gates

- Build: `CcDirector.Gateway` 0/0, `CcDirector.Gateway.Tests` 0/0.
- Targeted: CronEngineTenancyTests + WorkListRunnerManagerTenancyTests + WorkListRunnerManagerTests +
  CronWorkListTriggerTests + CronEngineTests = 25/25 passed.
- Solo tenancy filters: OnOneRoute + The_roster_and_the_commands_agree passed.
