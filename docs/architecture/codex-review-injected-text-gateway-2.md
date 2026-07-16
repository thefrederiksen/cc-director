Verdict: do not block.

1. Auth refresh: fixed. `InjectedTextStore.RefreshAsync` now sends an explicit `HttpRequestMessage`, attaches `Authorization: Bearer` from the Gateway config/token override, and keeps the shared `HttpClient` headers untouched. The new `RefreshAsync_AttachesTheFleetToken` test covers the regression.

2. Freshness after Cockpit edits: fixed enough for this increment. `ControlApiHost` still refreshes on Gateway connection, and now also starts a 60-second `PeriodicTimer` poll while a Gateway is configured, cancelled from `StopAsync`. That bounds stale cache time for already-connected Directors rather than requiring reconnect/restart.

3. Required PUT fields: fixed. `PUT /gateway/injected-text` now parses a `JsonObject`, requires both `use_yours` and `yours`, validates their JSON types, and only then builds `InjectedTextSettings`. Partial bodies no longer silently flip `use_yours` or erase saved custom text.

I did not find a remaining blocker in the increment-2 changes.

Validation run: `dotnet test --filter "InjectedText|FleetPreamble|PiPreamble"` passed: 89 `CcDirector.Core.Tests` and 2 `CcDirector.Gateway.Tests` matched and passed.
