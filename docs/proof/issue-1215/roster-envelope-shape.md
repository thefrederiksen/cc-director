# Issue #1215 - roster envelope shape, before and after

`GET /sessions?envelope=true` is what the Cockpit fleet surfaces read
(`packages/client-core/src/fleet/fleetClient.ts` -> `getSessionsEnvelope`).

A live capture from the running Gateway on `http://127.0.0.1:7878/sessions?envelope=true`
returned HTTP 401 (the auth gate is ON by default, issue #917 - a credential is required
even on loopback), so the shapes below are documented from the contract types and the
unit-tested behaviour of `FleetRosterCache`, per the issue's "captured JSON or documented
shape" acceptance option.

## BEFORE (drop-on-first-failure)

On a single failed poll to a Director, its sessions were removed from `sessions` for that
refresh and it appeared only as a `machineErrors` entry. There was no last-known-good
retention and no "wobbly" concept.

```json
{
  "sessions": [
    { "sessionId": "s-100", "directorId": "dir-A", "name": "build", "activityState": "Working" }
  ],
  "machineErrors": [
    { "directorId": "dir-B", "machineName": "desktop-2", "error": "timeout" }
  ]
}
```

Note: `dir-B`'s sessions are simply GONE from `sessions` on the failed cycle, then
reappear on the next successful one - the blink.

## AFTER (grace window + stale envelope)

The envelope keeps `sessions` and `machineErrors` unchanged for back-compat, and adds a
`directors` array: one reachability record per Director the fan-out considered, with the
three-state marker and a last-seen age. A Wobbly Director's sessions STAY in `sessions`
(served from the last-known-good snapshot); they are not dropped.

New field types (camelCase on the wire):

- `directors[].directorId` : string
- `directors[].machineName` : string
- `directors[].state` : string, one of `"online"`, `"wobbly"`, `"offline"`
- `directors[].lastSeenUtc` : string | null (ISO 8601 UTC)
- `directors[].lastSeenAgeSeconds` : number | null (0 when online, null when never seen)
- `directors[].error` : string | null (the failure reason for wobbly/offline; null when online)

### Example: dir-A online, dir-B WOBBLY (transient miss absorbed, sessions retained)

```json
{
  "sessions": [
    { "sessionId": "s-100", "directorId": "dir-A", "name": "build", "activityState": "Working" },
    { "sessionId": "s-200", "directorId": "dir-B", "name": "tests", "activityState": "Idle" }
  ],
  "machineErrors": [],
  "directors": [
    {
      "directorId": "dir-A",
      "machineName": "desktop-1",
      "state": "online",
      "lastSeenUtc": "2026-07-10T12:00:10Z",
      "lastSeenAgeSeconds": 0,
      "error": null
    },
    {
      "directorId": "dir-B",
      "machineName": "desktop-2",
      "state": "wobbly",
      "lastSeenUtc": "2026-07-10T12:00:05Z",
      "lastSeenAgeSeconds": 5,
      "error": "timeout"
    }
  ]
}
```

`dir-B`'s `s-200` is still present in `sessions` (served stale). The Cockpit joins
`session.directorId` -> `directors[].state` and dims `s-200` in place, showing
"last seen 5 seconds ago". Nothing disappears, nothing reflows.

### Example: dir-B OFFLINE (grace window exhausted)

After `FleetRosterCache.GraceWindowPollCycles` (3) consecutive failed cycles, the next
failure drops dir-B's sessions and marks it offline. This matches the historical drop
behaviour, so a genuinely down machine still reads as down:

```json
{
  "sessions": [
    { "sessionId": "s-100", "directorId": "dir-A", "name": "build", "activityState": "Working" }
  ],
  "machineErrors": [
    { "directorId": "dir-B", "machineName": "desktop-2", "error": "timeout" }
  ],
  "directors": [
    { "directorId": "dir-A", "machineName": "desktop-1", "state": "online",  "lastSeenUtc": "2026-07-10T12:00:20Z", "lastSeenAgeSeconds": 0,  "error": null },
    { "directorId": "dir-B", "machineName": "desktop-2", "state": "offline", "lastSeenUtc": "2026-07-10T12:00:05Z", "lastSeenAgeSeconds": 15, "error": "timeout" }
  ]
}
```

## Grace-window constant

`FleetRosterCache.GraceWindowPollCycles = 3` (a named constant with an explanatory
comment in `src/CcDirector.Gateway/Discovery/FleetRosterCache.cs`). Counted in poll
cycles, matching how `DirectorRegistry`'s reachability circuit counts consecutive
failures. Three cycles is far shorter than the registry's outer bounds (60 s heartbeat
timeout, 3 min unreachable-evict), so Offline is still reached promptly and within those
bounds - the fix never extends them.

## State-transition proof (unit tests)

`src/CcDirector.Gateway.Tests/FleetRosterCacheTests.cs`, all passing:

- `ReachableThenWobblyThenReachable_AbsorbsTransientMiss` (reachable -> wobbly -> reachable)
- `ReachableThenWobblyThenOffline_TransitionsOnceAfterGraceWindow` (reachable -> wobbly -> offline)
- `OfflineThenReachable_ReappearsOnline` (offline -> reachable)
- `RecordUnreachable_NeverReachable_IsOfflineWithNoSnapshot`
- `WobblyServe_RecomputesIdleClockFromLastActivity`
- `Forget_ClearsSnapshot_NextFailureIsOffline`
- `RecordReachable_StoresSnapshot_ReadsOnline`
