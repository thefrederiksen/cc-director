# Hosted Gateway unsafe-collection ingestion map

**Audit snapshot:** `f5bb926f18c0023e86fc7b212ab02d425ff9cc50`

**Parent census:** [Hosted Gateway process-global collection census](hosted-gateway-process-global-collection-census-2026-07-20.md), commit `a294d770`

**Scope:** remedy regrouping for the census's 40 active cross-tenant unsafe rows

**Status:** fix plan only; no row is declared fixed by this document

## Result

The 40 unsafe rows reduce to eleven identifier or no-identifier work units. The hoped-for single
prefix boundary does not exist for every unit:

- caller-supplied upload identifiers have one clean registration seam, provided every follow-up route
  derives the same internal key;
- statistics identity fields have one authoritative source family, but two aggregation call paths;
- session identifiers, Director ring keys, and machine names enter through several independent paths;
- job identifiers and stream identifiers are server-minted rather than caller-supplied;
- session-number reservations, unkeyed diagnostic and car-mode lists, and hour-keyed concurrency state
  cannot be made tenant-safe merely by prefixing a caller identifier.

That distinction is the useful output of the regrouping. A blanket “prefix identifiers” change would
leave active unsafe rows behind.

## Proven pattern to extend

`HostedEnrollmentEndpoint.Enroll` resolves the authenticated account to a tenant, hashes that tenant
with Secure Hash Algorithm 256, and stores `tenant hash | caller device identifier` in `DeviceRegistry`.
The raw device identifier never becomes a process-global key. Census row 45 is clean for that reason.

The same mechanism is suitable where all of these are true:

1. the tenant comes from authenticated request or bound-connection state, never the payload;
2. one canonical helper derives a domain-tagged internal key such as
   `tenant hash | session | raw identifier`;
3. every write, read, removal, expiry pass, and disconnect path derives the identical key;
4. external protocols keep the raw identifier, while only internal storage receives the namespaced key.

The fourth rule prevents a storage repair from changing browser links, Director commands, idempotency
tokens, or persisted user-facing identifiers.

## Regrouping by identifier and ingestion point

The row numbers refer to the parent census. Every active unsafe row appears exactly once.

| Identifier or address family | Ingestion point where it enters the Gateway | Census rows covered | Boundary-prefix verdict | Blast radius if wrong or missed |
| --- | --- | --- | --- | --- |
| **Session identifier** | `DirectorHub.PushSnapshot`, `PushDelta`, and `RemoveSession`; `GatewayEndpoints` heartbeat and doorbell callbacks, `/fanout`, `/sessions/{sid}/transcribing`, roster folding, and session-number routes; `GatewayDictationEndpoint.Map`; `TurnBriefGatewayEndpoints.Map` | 1, 3, 4, 5, 11, 26, 49-53, 65, 92, 93 **(14)** | **No single clean ingestion point.** Introduce one `TenantSessionKey` derivation helper after `DirectorHub.RequireBoundTenant` or `GatewayEndpoints.ResolveReadTenant`, and require the affected collection methods to accept that internal key. Keep raw session identifiers in `SessionDto`, route parameters, links, and tunnel commands. | A missed writer preserves a cross-tenant collision. A mismatched read creates orphaned state. Mutating the raw identifier breaks session lookup, tunnel routing, deletion, dictation progress, governance history, brief file paths, and browser links. |
| **Dictation upload identifier** | `GatewayDictationEndpoint.Map`, at `POST /dictation/upload`, after device authentication; the caller's `Idempotency-Key` reaches `VoiceUploadStore.Register`, and later chunk, complete, acknowledge, and abandon routes present it again | 19, 20, 73 **(3)** | **One clean registration seam, but all follow-up routes must normalize.** Keep the external globally unique identifier shape; use a `TenantUploadKey` for `_completes` and `_uploadSids`, and a tenant-specific record directory or gate key in `VoiceUploadStore`. | Inconsistent normalization loses resume and acknowledgement, splits chunks from completion, strands tombstones, permits duplicate prompt injection, or lets two tenants share a record lock. Changing the external identifier format also breaks the browser's durable idempotency record. |
| **Director identifier or synthetic event-ring key** | `GatewayEndpoints` doorbell and event routes after tenant-aware `DirectorRegistry.Get`; `GatewayCronNotifier.NotifyRunCompletedAsync`; non-hosted `NetDiagAlertService` synthetic network ring | 23 **(1)** | **No single clean ingestion point.** Change `DirectorEventLog` to require tenant plus ring key. Background notifiers must carry the owning tenant explicitly; synthetic rings need a deliberate system or tenant scope. | A missed producer files an event into another tenant's ring. A mismatched reader makes notifications disappear. Cron failures and network alerts are especially easy to misfile because their fallback keys are not Director identifiers. |
| **Machine name** | `LauncherHub.Hello`; `MachineEndpoints` launcher registration, heartbeat, removal, listing, and machine commands; `WorkListRunnerEndpoints` caller `machineKey` or resolved Director machine; Director-supplied `SessionDto.MachineName` in concurrency folding | 14, 42, 66, 69 **(4)** | **No single clean ingestion point.** Bind a tenant in `LauncherHub` as `DirectorHub` already does, introduce `TenantMachineKey`, and pass it through launcher registry, connection registry, list-run admission, and concurrency observation. | A mismatch can route a command to another tenant's launcher, disconnect the wrong connection, suppress a list drain, allow two drains on one tenant machine, or corrupt distinct-machine statistics. Prefixing only the registration path leaves command and disconnect paths unsafe. |
| **Cron job identifier** | `CronJobStore.Create` mints `cj_` plus six hexadecimal digits under the ambient tenant; `CronRunEndpoints` later accepts the identifier; `CronEngine.EvaluateDueAsync` obtains it from the store | 13 **(1)** | **Not caller-prefixable.** Key `_inFlight` by tenant plus job identifier. The scheduled sweep must enumerate and enter tenant scopes rather than run without a tenant pass. | A collision suppresses another tenant's run. A wrong tenant on completion can release the wrong overlap slot. Fixing only run-now leaves scheduled execution wrong; changing the public job identifier breaks history and notification links. |
| **Tunnel stream identifier** | `TunnelStreamLegs.ServeTerminalAsync` and the file-stream leg each mint a full globally unique identifier; `DirectorHub.StreamUp` later presents it from a tenant-bound Director connection | 15 **(1)** | **Not caller-prefixable.** Store tenant and expected Director ownership at registration and require the bound `DirectorHub` tenant and Director to match on every up-frame and close. A tenant-prefixed internal key is optional defense, not a substitute for the owner check. | A missed check permits frame injection, stream claim, or teardown. A wrong owner binding drops valid terminal or file frames, leaks sinks until timeout, or closes the wrong browser stream. |
| **Fleet session-number compound state** | `POST /session-numbers/allocate`, `DELETE /session-numbers/{sessionId}`, roster-time `sessionNumbers.Adopt`, and `DirectorRegistry.OnDirectorRemoved` calling `ReleaseForDirector` | 24 **(1)** | **Not one identifier prefix.** The row couples session and Director identifiers to an integer reservation pool. Partition the allocator itself by tenant and require tenant on allocate, adopt, release, and Director removal. | Prefixing the session map alone leaves `_inUse` global. A missed release can free another tenant's number; a missed allocation can exhaust the shared pool or produce duplicates within one tenant. |
| **Diagnostic result and hour bucket with no tenant key** | `GatewayEndpoints` `POST /diag/result` writes `NetDiagResultStore.Add` and `NetDiagRollupStore.Fold`; `GET /diag/results` and `GET /diag/rollup` read the same global stores | 21, 22 **(2)** | **No prefixable caller identifier.** Resolve the request tenant, stamp it on every result, partition the result store, and key rollups by tenant plus hour. Filter both reads by the authenticated tenant. | A write-only fix still leaks on reads; a read-only fix still permits aggregate poisoning. Persisted files need migration or quarantine rules, or old global data will be silently attributed to a tenant. |
| **Car-mode telemetry record with no storage key** | `CarModeEndpoint` `POST /carmode/telemetry` derives `DeviceHash` from the caller credential and appends to one list; the two telemetry reads return the global list | 40 **(1)** | **No collection key to prefix.** The route already derives a caller credential hash, but the store and reads ignore it as a partition. Store a trusted tenant or device partition and filter both data and page reads. | Partitioning only new writes leaves old records globally visible. Filtering by caller-supplied `TurnId` is unsafe. A wrong device-versus-tenant choice can hide a user's telemetry when they switch devices or disclose another device's records. |
| **Session statistics identity bundle: repository, agent, model, and checkout** | Authoritative values enter in `SessionDto` through `DirectorHub.PushSnapshot` and `PushDelta`; `GatewayInputStatsAggregator.ObserveSnapshot` and `Observe` fold them immediately, and the `/sessions` roster fold calls the aggregator and concurrency tracker again | 54-63, 67 **(11)** | **One source family, two aggregation seams.** Pass authenticated tenant into both aggregator call paths. Namespace internal identity keys or add tenant to their database and memory keys; preserve raw display strings. Row 62 belongs here rather than the session group because prefixing its session component leaves repository identity maps unsafe; row 63 is analogous for agents. | Incorrect namespacing double-counts high-water deltas, fragments one repository within a tenant, merges unrelated repositories, exposes tenant hashes in the user interface, or makes reverse identity maps disagree with forward maps. Legacy database and in-memory mirrors must migrate together. |
| **Concurrency hour and process-wide scalar totals** | `GatewayEndpoints` calls `GatewaySessionConcurrencyStats.Observe` after assembling the authenticated tenant's `/sessions` roster; the store derives the hour from server time | 64 **(1)** | **No caller identifier.** Partition the entire tracker by tenant, not merely `_hours`. Its current and all-time scalar fields are also process-global but fell outside the collection-only census. | Prefixing only the hour key still shares current and all-time values. A partial migration produces plausible but wrong peaks, and a tenant-filtered read over a global scalar remains a disclosure. |

## Coverage reconciliation

| Work unit | Row count |
| --- | ---: |
| Session identifier | 14 |
| Dictation upload identifier | 3 |
| Director or synthetic event-ring key | 1 |
| Machine name | 4 |
| Cron job identifier | 1 |
| Tunnel stream identifier | 1 |
| Fleet session-number compound state | 1 |
| Diagnostic result and hour bucket | 2 |
| Car-mode telemetry | 1 |
| Statistics identity bundle | 11 |
| Concurrency hour and scalar totals | 1 |
| **Total active unsafe rows covered** | **40** |

The union is exact: the work units contain all 40 active unsafe row numbers from the census, with no
duplicate assignment and no omission. The separate policy-unsafe broadcast-grant row 2 is not in this
table because it has no current cross-tenant target lookup; it still needs authorization ownership work.

## Completion predicate for the fix

For the prefixable work units, the remedy is complete if and only if every named ingestion path derives
the same tenant-scoped internal key, no affected collection API still accepts the raw identifier, and all
reads, removals, expiry passes, disconnects, and background work use that same key.

For the non-prefixable work units, completion instead requires the specific tenant partition or owner
binding named in the table. Tests must exercise two authenticated tenants using the same raw identifier,
the same machine name, the same short job identifier, and overlapping time buckets, then prove that one
tenant cannot read, mutate, suppress, delete, or contend with the other's state.

This map closes remedy coverage for the finite collection census. It does not assert that the remedies
are implemented, that open-ended shapes have passed two clean dry rounds, or that instrumentation is
complete.
