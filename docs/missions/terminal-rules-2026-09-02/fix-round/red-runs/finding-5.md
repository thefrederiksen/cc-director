# Finding 5 - the red run, before the fix

A temporary class `RedRunFinding5Tests` asserted the one thing the shipped code could not do: say
anything at all when a screen was dropped. It held exactly the log assertion that ships in
`ScreenPushLossBoundaryTests`, and was deleted after this run.

Command:

```
dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj \
  --filter "FullyQualifiedName~RedRunFinding5Tests" --nologo -v n
```

Result against the unfixed client:

```
Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1

RedRunFinding5Tests.A_screen_dropped_because_there_is_no_tunnel_says_so_in_the_log [FAIL]
  Assert.Contains() Failure: Filter not matched in collection
  Collection: []
```

`Collection: []` is the finding in one line. A whole turn's screen was thrown away and the log was
EMPTY - not a wrong line, not a vague line, no line. A Director dropping every screen it captured was
indistinguishable from one that had captured none.

## What was chosen, and what was not

Ruling 12 allows either durability or an honest boundary. This round takes the honest boundary: a
durable outbox is a new mechanism and a fix round is new writing that would owe its own proofs. The
hole is real and stays:

> A turn whose screen could not be sent has no row in the Gateway's history and never will. The next
> turn sends the NEXT turn's screen. The Director's own local turn-review file still holds it, and
> nothing replays that file into the store. If the machine then goes offline, the history read has no
> fallback for that turn at all.

The false claim is deleted from `GatewayScreenSink` - it said a miss cost "a round trip, never a
record" - and replaced with the paragraph above. The report says the same thing.

What was fixed is the silence. Every drop is logged with its session, its capture time and its reason,
and counted by `GatewayStreamClient.ScreenPushesDropped`, with a DELIVERED counter beside it because
"nothing was dropped" is satisfied by a Director that never pushed anything.

## The green run, after the fix

```
dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj \
  --filter "FullyQualifiedName~ScreenPushLossBoundary" --nologo -v q
Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2
```

The second test covers a push with no session id, which was the one remaining way a screen could
vanish with nothing saying so.
