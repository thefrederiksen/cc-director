# Issue #1215 diagnosis - where a single failed poll drops a Director's sessions

## Summary

The blink is a presentation defect in the roster aggregation, exactly as the issue
predicted. The `DirectorRegistry` mechanisms (heartbeat timeout, reachability circuit,
unreachable cooldown, stale sweeper) are sound and are NOT the cause. The defect is in
the aggregated `GET /sessions` handler: when a single poll to one Director fails, that
Director's sessions are removed from the aggregated response for that refresh, then
reappear on the next successful poll.

## Exact code path

File: `src/CcDirector.Gateway/Api/GatewayEndpoints.cs`, the `GET /sessions` handler
(the roster fan-out that aggregates every Director's sessions).

1. Fan-out per Director (before this change, lines 424-473). For each Director the
   handler either serves from the pushed cache, short-circuits with an error for a
   flagged / cooling-down / no-endpoint Director, or pulls over HTTP via
   `client.ListSessionsWithStatusAsync(ep, includeExitedActual)`. A failed pull returns
   `(Director: d, Sessions: null, Error: <reason>)` and calls
   `registry.RecordUnreachable(d.DirectorId, error)`.

2. Aggregation loop (before this change, the original lines 480-492):

   ```csharp
   foreach (var (d, sessions, error) in results)
   {
       if (error is not null)
       {
           machineErrors.Add(new MachineErrorDto
           {
               DirectorId = d.DirectorId,
               MachineName = d.MachineName,
               Error = error,
           });
           continue;   // <-- THE DEFECT: the Director's sessions are dropped for this refresh
       }
       if (sessions is null) continue;
       ...
   }
   ```

   The `continue` on any non-null `error` means a SINGLE failed poll cycle removes that
   Director's whole session group from `all` (the aggregated list) and from the envelope's
   `sessions`. There is no last-known-good retention: the very next successful poll puts the
   sessions back. Between those two refreshes the Cockpit sees the sessions vanish and then
   reappear - the "blink".

3. Result assembly and return (before this change, the original lines 606-610):

   ```csharp
   if (envelope == true)
       return Results.Json(new { sessions = all, machineErrors });
   return Results.Json(all);
   ```

   So both the plain array response and the `?envelope=true` response lose the Director's
   sessions on the failed cycle. The envelope only gained a `machineErrors` entry (a binary
   "reachable / unreachable"); it had no notion of "briefly wobbly, still show the last-known
   sessions".

## Why the registry is not at fault

`DirectorRegistry` keeps the Director registered across a transient miss - its
`HttpHeartbeatTimeout` is 60 s, its `UnreachableEvictAfter` is 3 minutes, and its
reachability circuit only opens after `MaxConsecutiveFailures` (3) probe failures. So on
a one-cycle network hiccup the Director is still in `ListDirectors()`; it is only its
per-refresh SESSION list that the aggregation drops. The fix therefore belongs in the
aggregation/presentation layer (this handler), not in the registry, and must not change
any registry constant.

## Fix direction (implemented in this pull request)

Introduce a Gateway-side last-known-good roster cache
(`src/CcDirector.Gateway/Discovery/FleetRosterCache.cs`) with a defined grace window of
`GraceWindowPollCycles = 3` failed poll cycles. On a failed poll the aggregation now
consults the cache instead of dropping: within the grace window it serves the stored
snapshot marked Wobbly (with a last-seen timestamp and age); once the grace window is
exhausted it declares the Director Offline and drops the sessions exactly as before (so a
genuinely down machine still reads as down promptly, well inside the registry's existing
eviction bounds). The envelope gains a `directors` array carrying each Director's
reachability state (online / wobbly / offline) and last-seen age. This is a defined
presentation state, not a silent retry that hides an outage.
