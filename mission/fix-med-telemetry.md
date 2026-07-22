# MTR - telemetry retry queue tenant partition (PR #2002)

Branch `fix/audit-med-telemetry`. Fixes audit MTR gap C: the two hosted `/telemetry`
writers shared one process-global durable FIFO with no tenant, so one tenant's volume
evicted another's oldest event and one tenant's poison event head-of-line-blocked every
other tenant's delivery; the startup route also accepted an arbitrary `director_id`.

## r1 (accepted by Codex, 12/12) - kept

- Every queued event is tagged with the SERVER-RESOLVED tenant of the authenticated
  request (`HostedTenantBoundary`; `TenantId.Local` on self-host, 403 deny when
  unresolved on hosted).
- The bound is PER TENANT (a caller only evicts its own oldest) and the flush skips a
  tenant whose head fails and keeps flushing every other tenant (a poison blocks only its
  own line).
- Revert-proof: neutering the per-tenant keying reddens the isolation tests.

## r2 - Codex CHANGES-NEEDED residuals, now fixed

Codex flagged two residuals; both addressed on top of r1.

### Residual 1 - startup route accepted an unowned director_id

The route rejected only a director_id positively owned by ANOTHER tenant, and ALLOWED an
unknown/blank/malformed id (to avoid over-refusing a startup report that races the tunnel
Hello). A caller could therefore mint a startup observation for any id it did not own.

Fix: on hosted the route now REQUIRES positive ownership -
`DirectorRegistry.IsDirectorOwnedByTenant(caller, id)` returns true only for an id keyed
to the caller's own tenant; a blank/malformed, another-tenant, or as-yet-unknown id all
fail and are rejected (403, never recorded, never enqueued). The accepted cost: a startup
report that arrives before its own id is registered is dropped - a best-effort, swallowed
ping is the lesser loss than accepting a forgeable cross-tenant observation. Self-host
(single tenant) never fires the gate.

- `DirectorRegistry.IsDirectorOwnedByOtherTenant` -> `IsDirectorOwnedByTenant` (positive).
- Endpoint gate flipped from "reject if owned-by-other" to "reject unless provably owned".
- Tests: unknown id -> 403 (was 202), blank id -> 403 (new), own id -> 202, other tenant
  -> 403, unbound key -> 403. Revert-proof: forcing the ownership check to `true` reddens
  the unknown/blank reject tests.

### Residual 2 - legacy pre-tag persisted events collapsed to Local

A queue file written before the tenant tag existed has untagged events; r1 defaulted them
to `TenantId.Local`. On a hosted Gateway those came from many real accounts but landed in
ONE shared Local partition, where a legacy poison event could head-of-line-block a real
Local-tenant event (and legacy events from different accounts blocked each other).

Fix: legacy untagged events are quarantined into an ISOLATED partition
(`TelemetryRetryQueue.LegacyUntaggedPartition`, a reserved key that is deliberately not a
valid `TenantId`). The bound and flush are keyed by that string, so the quarantine lane is
fully isolated: it still drains (at-least-once preserved - legacy events delivered, not
dropped) and a legacy poison blocks ONLY the quarantine lane, never any real tenant's
flush.

- Tests: legacy untagged file loads + delivers from the isolated lane; a legacy poison
  does NOT HOL-block a real Local-tenant event enqueued after it. Revert-proof: reverting
  the Load default back to Local reddens the HOL-block test (uses `TenantId.Local` as the
  real tenant on purpose - a GUID tenant would not catch the revert).

## Verification

- `dotnet build` gateway + tests: 0 warnings, 0 errors.
- Telemetry + startup tests: 101 passed.
- `DirectorStartupTelemetryTenantTests` solo: 7 passed.
- `TelemetryRetryQueueTenantTests` solo: 7 passed.
- Both r2 fixes independently proven revert-proof (revert -> the matching new test reddens).

Touches `GatewayHost.cs` (the 4-arg `DirectorStartupTelemetryEndpoint.Map` wiring) - so this
serializes at merge.
