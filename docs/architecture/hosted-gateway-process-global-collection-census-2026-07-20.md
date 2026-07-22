# Hosted Gateway process-global collection census

**Audit snapshot:** `f5bb926f18c0023e86fc7b212ab02d425ff9cc50` (`origin/main` on 20 July 2026)

**Scope:** retained collection state declared by the Gateway, including singleton instance fields and static fields

**Status:** enumeration audit only; this document does not claim that the findings are fixed

## Headline

**Forty independently addressable rows are actively unsafe across hosted tenants.**

This is a finite census, not a sample. It found 102 retained collection declarations in 54 declaring
types. Applying the collapse rule below produces 94 independently addressable rows: 46 clean, seven
latent, 40 actively cross-tenant unsafe, and one separately unsafe authorization-policy row.

| Reconciliation | Count |
| --- | ---: |
| Raw retained collection declarations | 102 |
| One-row reductions from the eight named collapses | -8 |
| Independently addressable rows | 94 |
| Clean rows | 46 |
| Latent rows | 7 |
| Active cross-tenant unsafe rows | 40 |
| Authorization-policy unsafe rows | 1 |
| Disposition total | 94 |

## Counting rule

Two collections collapse into one logical row only when no caller can address one without the other.
Collections remain separate when a caller can arguably address, mutate, or observe either one without
the other. This deliberately favors separate rows at an uncertain boundary.

The dispositions mean:

- **Clean:** the hosted path includes a trusted tenant discriminator, contains only server-owned
  configuration, or cannot mix tenant-owned state in the inspected composition.
- **Latent:** the collection has an unsafe global key shape, but its current hosted composition has no
  producer, no consumer, or no construction path that can complete the cross-tenant interaction.
- **Active unsafe:** a hosted path can currently cause one tenant to read, write, suppress, delete, or
  contend with another tenant's state.
- **Policy unsafe:** the isolation boundary is not crossed by the current lookup chain, but the
  authorization object is ownerless or can be minted by a caller who should not have that authority.

## Full census

Paths in the source column are relative to `src/CcDirector.Gateway` at the audited snapshot.

| # | Declaring state | Source | Lifetime | Effective address and supplier | Trusted tenant discriminator | Disposition and effect |
| ---: | --- | --- | --- | --- | --- | --- |
| 1 | `BroadcastGovernor._sends` | `Api/BroadcastGovernor.cs` | singleton | Bare sender session identifier from `POST /fanout` | No | **Active unsafe:** tenants sharing a session identifier share the send-rate budget and can deny one another broadcasts. |
| 2 | `BroadcastGovernor._grants` | `Api/BroadcastGovernor.cs` | singleton | Server-minted grant token returned to any authenticated caller, then presented to `POST /fanout` | No owner is stored | **Policy unsafe:** the current target lookup remains tenant-scoped, but any authenticated caller can mint the supposedly human-only grant. |
| 3 | `NeedsYouClock._since` | `Briefing/NeedsYouClock.cs` | singleton | Bare session identifier from the tenant roster; the Director originally supplies it | No | **Active unsafe:** one tenant can read or delete another tenant's derived needs-you timestamp through a colliding session identifier. |
| 4 | `TranscribingSessions._lastProgress` | `Transcription/TranscribingSessions.cs` | singleton | Bare session identifier from transcription routes and Director session data | No | **Active unsafe:** progress from one tenant changes another tenant's presentation timestamp. |
| 5 | `TranscribingSessions._activelyTranscribing` | `Transcription/TranscribingSessions.cs` | singleton | Bare session identifier from transcription routes and Director session data | No | **Active unsafe:** one tenant can set or clear another tenant's transcribing marker. |
| 6 | `DirectorRegistry._directors` | `Discovery/DirectorRegistry.cs` | singleton | `DirectorKey`, formed from authenticated tenant plus Director identifier from `DirectorHub` hello | Yes | **Clean:** the primary Director registry is tenant-partitioned. |
| 7 | `DirectorRegistry._everReachable` | `Discovery/DirectorRegistry.cs` | singleton | Bare Director identifier | No | **Latent:** removal code exists, but the audited snapshot has no completing producer and reader pair. |
| 8 | `DirectorRegistry._stateReporting` | `Discovery/DirectorRegistry.cs` | singleton | Bare Director identifier from `DirectorHub` hello, heartbeat, and doorbell paths | No | **Latent:** writes exist, but the production reader is absent in the audited snapshot. |
| 9 | `FleetRoleObserver._lastSent` | `Fleet/FleetRoleObserver.cs` | singleton | Bare pushed session identifier supplied by a Director | No | **Latent:** hosted construction receives only the local empty or no-operation snapshot. |
| 10 | `FleetDisplayStateObserver._lastSent` | `Fleet/FleetDisplayStateObserver.cs` | singleton | Bare pushed session identifier supplied by a Director | No | **Latent:** hosted construction receives only the local empty or no-operation snapshot. |
| 11 | `SessionStateEventEmitter._lastState` | `Governance/SessionStateEventEmitter.cs` | singleton | Bare session identifier from heartbeat and doorbell activity | No | **Active unsafe:** another tenant can suppress or remove a state transition and thereby suppress tenant-scoped durable history. |
| 12 | `AutoDismissSweeper._closing` | `Running/AutoDismissSweeper.cs` | singleton | Explicit tenant plus session marker built from each tenant's own sweep | Yes | **Clean:** `MarkKey` includes the tenant. |
| 13 | `CronEngine._inFlight` | `Running/CronEngine.cs` | singleton | Bare short job identifier from run-now and scheduled-job stores | No | **Active unsafe:** colliding six-hex-digit job identifiers share an overlap lock and can deny execution across tenants. |
| 14 | `WorkListRunnerManager._activeByMachine` | `Running/WorkListRunnerManager.cs` | singleton | Bare machine name from list-run requests or the target Director | No | **Active unsafe:** tenants sharing a machine name share start, read, drain, and deletion state. |
| 15 | `GatewayStreamRegistry._streams` | `Streaming/GatewayStreamRegistry.cs` | singleton | Bare server-minted stream identifier later supplied by the Director-side stream consumer | No owner is checked | **Active unsafe:** a tenant can claim, inject frames into, or tear down another tenant's colliding stream. |
| 16 | `PushedSessionStore._byTenant` Director partition | `Streaming/PushedSessionStore.cs` | singleton | Authenticated tenant, then Director identifier from the Director hello | Yes | **Clean:** the outer partition is the authenticated tenant. |
| 17 | `PushedSessionStore.DirectorEntry.Sessions` | `Streaming/PushedSessionStore.cs` | nested retained state | Authenticated tenant, Director identifier, then pushed session identifier | Yes | **Clean:** session access remains below the tenant and Director partitions. |
| 18 | `GatewayDeviceRegistrationService._lastPublishedEndpointUrls` | `Account/GatewayDeviceRegistrationService.cs` | singleton | One unkeyed list supplied by server endpoint configuration | Not tenant data | **Clean:** it retains only the last published Gateway endpoint configuration. |
| 19 | `GatewayDictationEndpoint._completes` | `Api/GatewayDictationEndpoint.cs` | static | Bare caller-supplied dictation upload identifier | No | **Active unsafe:** completion state can be read or overwritten across tenants. |
| 20 | `GatewayDictationEndpoint._uploadSids` | `Api/GatewayDictationEndpoint.cs` | static | Bare caller-supplied upload identifier mapped to a bare session identifier | No | **Active unsafe:** one tenant can bind or observe another tenant's upload-to-session association. |
| 21 | `NetDiagResultStore._items` | `Api/NetDiagResultStore.cs` | singleton | Unkeyed result list written and read by diagnostic routes | No | **Active unsafe:** diagnostic results from all tenants are mixed and disclosed. |
| 22 | `NetDiagRollupStore._buckets` | `Api/NetDiagRollupStore.cs` | singleton | Coordinated Universal Time hour from diagnostic result monitoring | No | **Active unsafe:** tenants can poison and read a shared hourly aggregate. |
| 23 | `DirectorEventLog._rings` | `Events/DirectorEventLog.cs` | singleton | Bare Director identifier from doorbell and event routes | No | **Active unsafe:** event rings collide across tenants. |
| 24 | `FleetSessionNumberAllocator._bySession` plus `_inUse` | `Discovery/FleetSessionNumberAllocator.cs` | singleton | Bare session identifier, Director identifier, and observed number from roster and session-number routes | No | **Active unsafe:** allocation and reservation state is shared across tenants. |
| 25 | `FleetRosterCache._byDirector` plus `Entry.Snapshot` | `Discovery/FleetRosterCache.cs` | singleton | Authenticated tenant plus Director identifier | Yes | **Clean:** the roster snapshot is below a tenant-aware Director key. |
| 26 | `TurnEndWatcher._lastActivity` | `Briefing/TurnEndWatcher.cs` | singleton | Bare session identifier and activity from heartbeat and doorbell paths | No | **Active unsafe:** one tenant can advance another tenant's turn-end activity marker. |
| 27 | `CarModeTurnCache._byKey` | `CarMode/CarModeTurnCache.cs` | singleton | Authenticated credential, separator, and caller idempotency key | Yes | **Clean:** the effective key includes the authenticated credential. |
| 28 | `CarModeConversationStore._byDevice` plus `Conversation.Messages` | `CarMode/CarModeConversationStore.cs` | singleton | Device key derived from the authenticated credential | Yes | **Clean:** conversation history is credential-partitioned. |
| 29 | `CarModePendingStore._byDevice` | `CarMode/CarModePendingStore.cs` | singleton | Device key derived from the authenticated credential | Yes | **Clean:** pending actions are credential-partitioned. |
| 30 | `CarModeSubjectStore._byDevice` | `CarMode/CarModeSubjectStore.cs` | singleton | Device key derived from the authenticated credential | Yes | **Clean:** subject state is credential-partitioned. |
| 31 | `LoopbackCarModeFleet._rosterCache` | `CarMode/LoopbackCarModeFleet.cs` | singleton | One unkeyed loopback roster loaded with the machine token | No | **Latent:** the hosted loopback path cannot currently populate this cache with tenant roster data. |
| 32 | `NetDiagMonitor._devices` plus `DeviceState.Samples` | `Api/NetDiagMonitor.cs` | singleton when constructed | Tailscale device or address identity | No tenant field | **Clean:** this monitor is not constructed by the hosted composition at the audited snapshot. |
| 33 | `NetDiagAlertService._emailedEpisode` | `Api/NetDiagAlertService.cs` | singleton when constructed | Tailscale device episode | No tenant field | **Clean:** this alert service is not constructed by the hosted composition at the audited snapshot. |
| 34 | `NetDiagDeviceStore._devices` plus `PersistedDevice.Samples` | `Api/NetDiagDeviceStore.cs` | singleton when constructed | Tailscale device identity | No tenant field | **Clean:** this store is not constructed by the hosted composition at the audited snapshot. |
| 35 | `TelemetryRetryQueue._events` | `Api/TelemetryRetryQueue.cs` | singleton | Unkeyed queue; each retained event carries its own bearer context | Per-event credential | **Clean:** callers cannot address another queued item by a shared key. |
| 36 | `TunnelCatchAllDispatch.GetReads` | `Api/TunnelCatchAllDispatch.cs` | static immutable use | Server-owned route and verb literals | Not tenant data | **Clean:** dispatch configuration only. |
| 37 | `TunnelCatchAllDispatch.BodyWrites` | `Api/TunnelCatchAllDispatch.cs` | static immutable use | Server-owned route and verb literals | Not tenant data | **Clean:** dispatch configuration only. |
| 38 | `CarModeConfirm.Affirmatives` | `CarMode/CarModeConfirm.cs` | static immutable use | Server-owned phrase literals | Not tenant data | **Clean:** classifier configuration only. |
| 39 | `CarModeConfirm.Negatives` | `CarMode/CarModeConfirm.cs` | static immutable use | Server-owned phrase literals | Not tenant data | **Clean:** classifier configuration only. |
| 40 | `CarModeTelemetryStore._records` | `CarMode/CarModeTelemetryStore.cs` | singleton | One unkeyed list written and read by telemetry routes | No | **Active unsafe:** telemetry records from all tenants are mixed and disclosed. |
| 41 | `CockpitReactApp.BrowserPageRoots` | `Cockpit/CockpitReactApp.cs` | static immutable use | Server-owned page-root literals | Not tenant data | **Clean:** browser application routing configuration only. |
| 42 | `LauncherRegistry._launchers` | `Discovery/LauncherRegistry.cs` | singleton | Bare caller-supplied machine name from launcher routes | No | **Active unsafe:** registration, lookup, and removal collide across tenants. |
| 43 | `SessionOwnerCache._ownerBySession` | `Discovery/SessionOwnerCache.cs` | singleton | Bare session identifier from roster and proxy writers | No | **Latent:** the audited production composition has no consumer of `OwnerOf`. |
| 44 | `GovernanceAuditLog.ActorRequired` | `Governance/GovernanceAuditLog.cs` | static immutable use | Server-owned event-name literals | Not tenant data | **Clean:** validation configuration only. |
| 45 | `DeviceRegistry._byDeviceId` | `Pairing/DeviceRegistry.cs` | singleton | One-way hash of authenticated tenant plus caller device identifier | Yes | **Clean:** hosted enrollment namespaces the caller identifier at ingestion. |
| 46 | `WebPushNeedsYouNotifier.DotState.Announced` | `Push/WebPushNeedsYouNotifier.cs` | nested retained state | Bare roster session identifiers | No | **Latent:** the notifier is not constructed by the hosted composition at the audited snapshot. |
| 47 | `WebPushNeedsYouNotifier.NoExpired` | `Push/WebPushNeedsYouNotifier.cs` | static immutable | Server-owned empty sentinel collection | Not tenant data | **Clean:** no caller-owned state. |
| 48 | `SourceAdapters.BySource` | `Running/SourceAdapters.cs` | static immutable use | Server-owned source-name literals | Not tenant data | **Clean:** adapter configuration only. |
| 49 | `GatewayInputStatsAggregator._highWater` | `Stats/GatewayInputStatsAggregator.cs` | singleton | Bare session identifier plus modality and surface from Director statistics | No | **Active unsafe:** high-water counters collide across tenants. |
| 50 | `GatewayInputStatsAggregator._agentDrivenHighWater` | `Stats/GatewayInputStatsAggregator.cs` | singleton | Bare session identifier from Director statistics | No | **Active unsafe:** agent-driven high-water counters collide across tenants. |
| 51 | `GatewayInputStatsAggregator._wingmanSessions` | `Stats/GatewayInputStatsAggregator.cs` | singleton | Bare session identifier from Director statistics | No | **Active unsafe:** a tenant can suppress another tenant's first-seen wingman accounting. |
| 52 | `GatewayInputStatsAggregator._tokenHighWater` | `Stats/GatewayInputStatsAggregator.cs` | singleton | Bare session identifier from token statistics | No | **Active unsafe:** token high-water accounting collides across tenants. |
| 53 | `GatewayInputStatsAggregator._agentsSeeded` | `Stats/GatewayInputStatsAggregator.cs` | singleton | Bare session identifier used as the seed marker | No | **Active unsafe:** one tenant can suppress another tenant's initial agent seed. |
| 54 | `GatewayInputStatsAggregator._repoIds` | `Stats/GatewayInputStatsAggregator.cs` | singleton | Repository display value supplied through Director statistics | No | **Active unsafe:** repository identities are globally coalesced. |
| 55 | `GatewayInputStatsAggregator._agentIds` | `Stats/GatewayInputStatsAggregator.cs` | singleton | Agent display value supplied through Director statistics | No | **Active unsafe:** agent identities are globally coalesced. |
| 56 | `GatewayInputStatsAggregator._modelIds` | `Stats/GatewayInputStatsAggregator.cs` | singleton | Model display value supplied through Director statistics | No | **Active unsafe:** model identities are globally coalesced. |
| 57 | `GatewayInputStatsAggregator._checkoutIds` | `Stats/GatewayInputStatsAggregator.cs` | singleton | Checkout display value supplied through Director statistics | No | **Active unsafe:** checkout identities are globally coalesced. |
| 58 | `GatewayInputStatsAggregator._repoDisplay` | `Stats/GatewayInputStatsAggregator.cs` | singleton | Server database identity mapped back to caller-influenced repository display | No | **Active unsafe:** reverse identity display state is shared across tenants. |
| 59 | `GatewayInputStatsAggregator._agentDisplay` | `Stats/GatewayInputStatsAggregator.cs` | singleton | Server database identity mapped back to caller-influenced agent display | No | **Active unsafe:** reverse identity display state is shared across tenants. |
| 60 | `GatewayInputStatsAggregator._modelDisplay` | `Stats/GatewayInputStatsAggregator.cs` | singleton | Server database identity mapped back to caller-influenced model display | No | **Active unsafe:** reverse identity display state is shared across tenants. |
| 61 | `GatewayInputStatsAggregator._checkoutDisplay` | `Stats/GatewayInputStatsAggregator.cs` | singleton | Server database identity mapped back to caller-influenced checkout display | No | **Active unsafe:** reverse identity display state is shared across tenants. |
| 62 | `GatewayInputStatsAggregator._repoSessions` | `Stats/GatewayInputStatsAggregator.cs` | singleton | Repository database identity plus bare session identifier | No | **Active unsafe:** repository session membership is globally coalesced. |
| 63 | `GatewayInputStatsAggregator._agentSessions` | `Stats/GatewayInputStatsAggregator.cs` | singleton | Agent database identity plus bare session identifier | No | **Active unsafe:** agent session membership is globally coalesced. |
| 64 | `GatewaySessionConcurrencyStats._hours` | `Stats/GatewaySessionConcurrencyStats.cs` | singleton | Coordinated Universal Time hour from roster observations | No | **Active unsafe:** hourly concurrency aggregates mix all tenants. |
| 65 | `GatewaySessionConcurrencyStats._curSessions` | `Stats/GatewaySessionConcurrencyStats.cs` | singleton | Bare session identifier from roster observations | No | **Active unsafe:** current session concurrency coalesces tenants. |
| 66 | `GatewaySessionConcurrencyStats._curMachines` | `Stats/GatewaySessionConcurrencyStats.cs` | singleton | Bare machine name from roster observations | No | **Active unsafe:** current machine concurrency coalesces tenants. |
| 67 | `GatewaySessionConcurrencyStats._curRepos` | `Stats/GatewaySessionConcurrencyStats.cs` | singleton | Bare repository name from roster observations | No | **Active unsafe:** current repository concurrency coalesces tenants. |
| 68 | `StatsPageEndpoint.NotCaptured` | `Stats/StatsPageEndpoint.cs` | static immutable use | Server-owned explanatory text | Not tenant data | **Clean:** presentation configuration only. |
| 69 | `LauncherConnectionRegistry._byMachine` | `Streaming/LauncherConnectionRegistry.cs` | singleton | Bare machine name from `LauncherHub` hello | No | **Active unsafe:** connection ownership collides across tenants. |
| 70 | `AuthMiddleware.PublicPaths` | `Util/AuthMiddleware.cs` | static immutable use | Server-owned public route literals | Not tenant data | **Clean:** authentication configuration only. |
| 71 | `GatewayTurnJobStore._tenants` plus `TenantJobs.Jobs` | `Voice/GatewayTurnJobStore.cs` | singleton | Validated canonical tenant plus server-minted turn identifier | Yes | **Clean:** turn jobs are below the tenant partition. |
| 72 | `GatewayTurnJobStore._tenants` plus `TenantJobs.ByUpload` | `Voice/GatewayTurnJobStore.cs` | singleton | Validated canonical tenant plus caller upload identifier | Yes | **Clean:** upload lookup is below the tenant partition. |
| 73 | `VoiceUploadStore._recordGates` | `Voice/VoiceUploadStore.cs` | static | Canonical record path derived from a bare caller upload identifier | No | **Active unsafe:** upload identifiers select shared record locks in an unpartitioned root. |
| 74 | `WaitingScreenClassifier.ModeStatusAnchors` | `Wingman/WaitingScreenClassifier.cs` | static immutable use | Server-owned classifier literals | Not tenant data | **Clean:** classifier configuration only. |
| 75 | `WaitingScreenClassifier.BorderOrSpace` | `Wingman/WaitingScreenClassifier.cs` | static immutable use | Server-owned character literals | Not tenant data | **Clean:** classifier configuration only. |
| 76 | `WaitingScreenClassifier.BoxEdge` | `Wingman/WaitingScreenClassifier.cs` | static immutable use | Server-owned character literals | Not tenant data | **Clean:** classifier configuration only. |
| 77 | `WaitingScreenClassifier.VerticalBorder` | `Wingman/WaitingScreenClassifier.cs` | static immutable use | Server-owned character literals | Not tenant data | **Clean:** classifier configuration only. |
| 78 | `WingmanMenu.BorderPadding` | `Wingman/WingmanMenu.cs` | static immutable use | Server-owned character literals | Not tenant data | **Clean:** menu parsing configuration only. |
| 79 | `WingmanMenu.NumberWords` | `Wingman/WingmanMenu.cs` | static immutable use | Server-owned number-word literals | Not tenant data | **Clean:** menu parsing configuration only. |
| 80 | `WingmanVoiceService._tenants` plus `TenantVoiceState.VoiceSessions` | `Wingman/WingmanVoiceService.cs` | singleton | Validated tenant plus session identifier | Yes | **Clean:** voice-session markers are below the tenant partition. |
| 81 | `WingmanVoiceService._tenants` plus `TenantVoiceState.Ready` | `Wingman/WingmanVoiceService.cs` | singleton | Validated tenant plus session identifier | Yes | **Clean:** ready audio is below the tenant partition. |
| 82 | `WingmanVoiceService._tenants` plus `TenantVoiceState.Generating` | `Wingman/WingmanVoiceService.cs` | singleton | Validated tenant plus session identifier | Yes | **Clean:** generation markers are below the tenant partition. |
| 83 | `WingmanVoiceService._tenants` plus `TenantVoiceState.Unavailable` | `Wingman/WingmanVoiceService.cs` | singleton | Validated tenant plus session identifier | Yes | **Clean:** unavailability state is below the tenant partition. |
| 84 | `WingmanVoiceService._tenants` plus `TenantVoiceState.NothingToNarrate` | `Wingman/WingmanVoiceService.cs` | singleton | Validated tenant plus session identifier | Yes | **Clean:** narration state is below the tenant partition. |
| 85 | `WingmanVoiceService._tenants` plus `TenantVoiceState.PreferBackupUntil` | `Wingman/WingmanVoiceService.cs` | singleton | Validated tenant plus session identifier | Yes | **Clean:** fallback deadlines are below the tenant partition. |
| 86 | `WingmanVoiceService._tenants` plus `TenantVoiceState.InFlight` | `Wingman/WingmanVoiceService.cs` | singleton | Validated tenant plus session identifier | Yes | **Clean:** in-flight voice work is below the tenant partition. |
| 87 | `BuiltInWorkflows.Definitions` | `Workflows/BuiltInWorkflows.cs` | static immutable use | Server-owned workflow definitions | Not tenant data | **Clean:** workflow configuration only. |
| 88 | `WorkflowRunStore.LegalTransitions` | `Workflows/WorkflowRunStore.cs` | static immutable use | Server-owned status-transition literals | Not tenant data | **Clean:** workflow validation configuration only. |
| 89 | `WorkflowRunStore.AcceptanceStatuses` | `Workflows/WorkflowRunStore.cs` | static immutable use | Server-owned status literals | Not tenant data | **Clean:** workflow validation configuration only. |
| 90 | `WorkflowRunStore.CriterionStatuses` | `Workflows/WorkflowRunStore.cs` | static immutable use | Server-owned status literals | Not tenant data | **Clean:** workflow validation configuration only. |
| 91 | `WorkflowValidation.AllowedFileExtensions` | `Workflows/WorkflowValidation.cs` | static immutable use | Server-owned file-extension literals | Not tenant data | **Clean:** workflow validation configuration only. |
| 92 | `GatewayTurnBriefStore._latest` | `Briefing/GatewayTurnBriefStore.cs` | singleton | Bare session identifier from roster and turn-brief routes | No | **Active unsafe:** latest-brief cache entries collide across tenants. |
| 93 | `GatewayTurnBriefStore._packages` | `Briefing/GatewayTurnBriefStore.cs` | singleton | Bare session identifier plus turn count | No | **Active unsafe:** turn packages collide across tenants. |
| 94 | `CarModeHelp` cheat-sheet modes plus per-mode examples | `CarMode/CarModeHelp.cs` | static immutable use | Server-owned help configuration | Not tenant data | **Clean:** the mode and its examples are returned as one inseparable help unit. |

## The eight named collapses

Each collapse below reduces the raw declaration count by exactly one. The sixth and seventh retain
multiple logical rows because the outer tenant collection is shared by multiple independently
addressable inner collections.

1. `NetDiagDeviceStore._devices` and `PersistedDevice.Samples` become row 34.
2. `NetDiagMonitor._devices` and `DeviceState.Samples` become row 32.
3. `CarModeConversationStore._byDevice` and `Conversation.Messages` become row 28.
4. `FleetRosterCache._byDirector` and `Entry.Snapshot` become row 25.
5. `FleetSessionNumberAllocator._bySession` and `_inUse` become row 24.
6. `GatewayTurnJobStore._tenants` is folded into each inner index, producing rows 71 and 72 rather
   than a third independently addressable row.
7. `WingmanVoiceService._tenants` is folded into each of its seven inner state collections,
   producing rows 80 through 86 rather than an eighth independently addressable row.
8. `CarModeHelp` modes and each mode's examples become row 94.

## Boundary exclusions

The sweep examined and excluded these shapes because they are not retained, independently
addressable Gateway collection state:

- method-local accumulators;
- request, response, and data-transfer objects;
- containers used only while deserializing persisted files;
- database context sets;
- synchronization primitives;
- byte-array payload values;
- framework-owned internal collections.

No collection-like keyed timers were found.

## Count correction

An intermediate tranche counted `WebPushNeedsYouNotifier.DotState.Initial` as row 47. It is a scalar
sentinel whose value contains the already-counted `Announced` set, not an independently addressable
collection. Removing it corrected that tranche's running total downward from 71 to 70. The separate
`NoExpired` immutable empty collection remains row 47 in the final renumbered table.

## What this census does and does not prove

This closes finite-shape enumeration only, at the stated snapshot. It does not establish convergence.
The 40 active rows still require fixes and proof. Open-ended shapes still require two clean dry rounds
plus a list-completeness audit, and the instrumentation clause remains separate.
