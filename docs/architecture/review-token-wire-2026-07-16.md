# Review: Token Wire

## Verdict

No blocking findings. I would land this change.

## Findings

None.

## Review Notes

The token read is gated correctly. `SessionRecordsWatcher` wires only sessions whose driver can report at least one records-backed fact, then independently gates model and token refreshes inside `RefreshFromRecords`. The token path calls `ReadUsage` only when `DriverCapabilities.TokenUsage` is present. The production drivers whose `ReadUsage` implementations throw do not declare that capability, so the turn-end path does not reach those throws as normal control flow.

The watcher rename looks mechanically complete in the touched runtime paths. The host field, startup wiring, disposal path, session comments, DTO comments, and tests now reference `SessionRecordsWatcher`. The model refresh behavior is unchanged: it still passes no launch arguments to `ReadCurrentModel`, still ignores null or blank reads through `SetCurrentModel`, and still refreshes on the same turn-end transition plus the initial wire-up pass.

The roster wire carries lean token totals only. `TokenTotalsDto` contains scalar totals, a context gauge, and an optional timestamp. `ControlEndpoints` maps `s.TokenTotals` directly onto `SessionDto.TokenTotals`; it does not attach the full per-turn usage object or the per-turn list to the roster snapshot.

The null discipline is correct. `SetTokenTotals` returns on null and leaves the last known totals in place, matching the model setter's missed-read behavior. A later non-null read replaces the whole totals snapshot.

## Verification

Ran:

```text
dotnet test src/CcDirector.Core.Tests/CcDirector.Core.Tests.csproj --filter "FullyQualifiedName~SessionRecordsWatcherTests" --no-restore
dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --filter "FullyQualifiedName~PushedSessionStore|FullyQualifiedName~ControlEndpoints" --no-restore
```

Results: passed, 9 tests in the focused core watcher suite; passed, 21 tests in the focused gateway suite.

Repository scan found no remaining `SessionCurrentModelWatcher` references in source files.
