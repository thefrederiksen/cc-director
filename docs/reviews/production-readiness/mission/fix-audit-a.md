# audit-a - Prompt-log shared lock serializes tenants (MED) - FIXED

## Verdict

REAL gap, reproduced on main, fixed, revert-proofed.

## The gap

`GatewayPromptLog` partitions each tenant's prompt TEXT correctly - the partition is the DIRECTORY, so
two tenants never write or read the same daily file. But every tenant's `Append` and daily-file `Read`
took ONE process-global lock (`private readonly object _gate = new()`):

- `Append`: `lock (_gate) { Directory.CreateDirectory(directory); File.AppendAllLines(path, lines); }`
- `Read`:   `lock (_gate) { lines = File.ReadAllLines(path); }`

Retention is unbounded by design ("looking back across weeks and months ... Nothing prunes this"), so a
daily file grows without limit. `File.AppendAllLines` / `File.ReadAllLines` hold the lock for the WHOLE
file operation. One tenant appending to or reading a large file therefore stalls every OTHER tenant's
unrelated prompt IO, even though they live in different directories. Correctness partitioning without
concurrency partitioning: tenant A's large or slow file blocks tenant B's `GET /prompts`.

## Reproduction (on main, before the fix)

`PromptLogTenantConcurrencyTests.One_tenant_holding_its_gate_does_not_block_another_tenants_append`,
mirroring the proven shape of `FleetSessionNumberAllocatorTenancyTests`:

1. Reach the exact lock tenant A's own appends take (reflection over the per-tenant gate).
2. Hold tenant A's gate on the test thread.
3. PROPERTY: tenant B appends on another thread and must complete without waiting.
4. DESTRUCTIBILITY CONTROL: a same-tenant A append DOES block on the held gate (proving the gate held is
   the one A's operations take, so "B proceeded" is real independence, not a lock nobody uses).

With the single shared `_gate`, the property assertion reddens: tenant B blocks on the very lock the
test holds. (Confirmed by temporarily restoring a shared gate - the test failed as expected.)

## The fix

Replaced the single `_gate` with one file-IO lock PER TENANT, keyed by `TenantId`:

```csharp
private readonly ConcurrentDictionary<TenantId, object> _gates = new();
private object GateFor(TenantId tenant) => _gates.GetOrAdd(tenant, static _ => new object());
```

`Append` and `Read` each take `GateFor(tenant)` instead of `_gate`, so a caller only ever locks its own
partition's gate. One tenant's file IO can no longer block another's. A single tenant's concurrent
appends/reads to its own files are still serialized - the only serialization actually required. This is
the same shape `FleetSessionNumberAllocator` (audit H2, #1996) uses for its per-tenant pool lock.

## Revert-proof

Restore a single shared gate (`GateFor` returns one shared object) and rebuild: the concurrency test
reddens on the tenant-B property (B blocks on the held lock). Restore the per-tenant `GetOrAdd` and it
passes again. Confirmed both directions with a full-assembly build (0/0).

## Test evidence

- `PromptLogTenantConcurrencyTests`: 1/1 pass (the reproduction).
- `PromptLogTenantIsolationTests` + `GatewayPromptLogTests` + `PromptEndpointsTests`: 20/20 pass.
- Solo tenancy filters: `OnOneRoute` 1/1, `The_roster_and_the_commands_agree` 1/1.
- Gateway + Gateway.Tests build: 0 warnings / 0 errors.

Does NOT touch GatewayHost.cs or GatewayEndpoints.cs (no merge serialization).

## Files

- `src/CcDirector.Gateway/Prompts/GatewayPromptLog.cs` - per-tenant gate dictionary; `Append`/`Read`
  take `GateFor(tenant)`.
- `src/CcDirector.Gateway.Tests/PromptLogTenantConcurrencyTests.cs` - reproduction + revert-proof.
