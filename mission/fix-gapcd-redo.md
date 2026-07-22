# MTR-10 Gap C+D redo

Re-implementation of MTR-10 Gap C+D after the first attempt (abf581ff) was reverted
(#1995) for regressing the display-state push.

## The isolation goals (unchanged from the original)

- **Gap C**: `TurnEndWatcher` (and the coupled `NeedsYouClock`) keyed by
  `(tenant, sessionId)`, never the bare session id. The owning tenant is resolved
  BEFORE the transition decision and carried on the `TurnEndSignal`, and the push-store
  reconcile runs one pass per tenant. Two accounts that happen to share a session GUID
  can no longer suppress or fabricate each other's Working -> Waiting boundary (and so
  each other's voice auto-refresh or "waiting since" clock).
- **Gap D**: the display-state PUSH seam's voice enrichment
  (`GatewayHost.EnrichVoiceThenFoldForPush`) reads the AMBIENT per-tenant of the display
  pass, not `TenantId.Local`. The tenant-partitioned voice service is live on hosted
  (#1973); a `Local` read there is an EMPTY partition, so the push-only desktop rail
  folded `VoiceAudioReady=false` for every session and held every voice-mode session
  permanently "Preparing voice" (yellow) while the roster served red.

## Why the first attempt broke PushSnapshot

The reverted change made the Gap D push closure read the ambient tenant:

```csharp
voiceGeneratingFor: sid => _tenantPass.Current is { } t && _voiceService?.IsGenerating(t, sid) == true,
voiceAudioReadyFor: sid => _tenantPass.Current is { } t && _voiceService?.HasVoice(t, sid) == true,
```

The ambient tenant during a Director push is whatever tenant that Director is BOUND to.
`DirectorHub.PushSnapshot` scopes the whole handler to the bound tenant
(`EnterBoundTenantScope`) and then calls `_fleetDisplayState?.ObserveSnapshot(set)`
SYNCHRONOUSLY, which folds through this closure.

`WingmanVoiceService` REFUSES to name a voice-state partition for a tenant that is not
`TenantId.Local` and not a minted account tenant - `IsGenerating`/`HasVoice` ->
`StateFor` -> `CanonicalTenantKey` THROW `ArgumentException` for it. A Director bound to
such a tenant therefore threw straight out of `PushSnapshot` as a SignalR
`HubException`, taking the WHOLE fleet's display push down.

`SessionServingLoopIsolationTests` binds its two Directors to `TenantId("tenant-alice")`
and `TenantId("tenant-bob")` - deliberately unminted ids - so its `PushSnapshotAsync`
calls hit exactly this throw. The original review only ran an "affected slice" (79/79)
that did not include those tests, so the regression reached production and was caught by
the CI backstop and rolled back (#1995).

## How this fixes it

The push read now degrades to "no voice" for an ambient tenant that cannot name a voice
partition, instead of throwing:

```csharp
voiceGeneratingFor: sid => _tenantPass.Current is { } t
    && Wingman.WingmanVoiceService.CanNameVoicePartition(t)
    && _voiceService?.IsGenerating(t, sid) == true,
voiceAudioReadyFor: sid => _tenantPass.Current is { } t
    && Wingman.WingmanVoiceService.CanNameVoicePartition(t)
    && _voiceService?.HasVoice(t, sid) == true,
```

`WingmanVoiceService.CanNameVoicePartition(TenantId)` is the SAME decision
`CanonicalTenantKey` already makes (Local, or a minted account tenant), surfaced without
throwing. This aligns the hot read path with the design-documented answer that a
partition-less tenant (System, unminted, unresolved) "has no voice state at all" - a
plain false, never an exception. The persisting/generating paths still throw for such a
tenant, because writing into a partition that cannot be named IS a bug.

Gap D's per-tenant read is unchanged where it matters: minted account tenants
(production) and `Local` (self-host) are nameable, so the ambient read still finds their
real voice state. The `DisplayPushTenantEnrichmentTests` canary (minted GUID tenants)
still proves owning-tenant=red / other-tenant=yellow.

Gap C did not participate in the throw: the turn-end watcher is fed by the doorbell
(`onSessionState`) and the reconcile timer, not by `PushSnapshot`, so its per-tenant
voice calls never ran synchronously inside the push. Gap C is re-applied exactly as the
original, with its revert-proof wiring canaries intact.

## Proof

- `dotnet build` Gateway + Gateway.Tests: 0 warnings, 0 errors.
- HARD GATE - `SessionServingLoopIsolationTests`: 4/4 PASS.
- `TurnEndWatcherTenantIsolationTests` (solo): 1/1 PASS.
- `VoiceServingLoopIsolationTests` (solo): 3/3 PASS.
- `DisplayPushTenantEnrichmentTests` (solo): 1/1 PASS.
- Turn-end / needs-you-clock / display-push units: 32/32 PASS.
- Full Gateway test suite: PASS (see PR body for counts).

## Files

- `src/CcDirector.Gateway/Wingman/WingmanVoiceService.cs` - add
  `CanNameVoicePartition`.
- `src/CcDirector.Gateway/GatewayHost.cs` - guard the Gap D push read; Gap C wiring.
- `src/CcDirector.Gateway/Briefing/TurnEndWatcher.cs` - `(tenant, sessionId)` keying,
  tenant on the signal, per-tenant reconcile.
- `src/CcDirector.Gateway/Briefing/NeedsYouClock.cs` - `(tenant, sessionId)` keying.
- `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` - thread the owning tenant into the
  needs-you stamp.
- Tests: `DisplayPushTenantEnrichmentTests`, `TurnEndWatcherTenantIsolationTests` (new);
  `NeedsYouClockTests`, `TurnEndWatcherVoiceRefreshTests`, `GatewayTurnBriefTests`,
  `VoiceServingLoopIsolationTests`, `DisplayPushVoiceEnrichmentTests` (updated).
