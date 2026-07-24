# Mission state - repositories-full

Branch: mission/repositories-full · Worktree: D:\ReposFred\devthrottle-repos-mission
Spec: devthrottle_internal#510 · Brief: docs/MISSION-repositories-full-2026-07-23.md

## Done (committed on the branch)
- (nothing yet)

## In progress
- Step 1 (A-model): worktree list + sizes + dirty-since + provisional on the model.

## Next
- Step 2 (A-unify), then per the brief's landing order.

## Notes for a fresh seat
- Owner approval required before ANY merge to origin/main. Commit/push to the branch freely.

## C research (received, condensed - trust but verify line numbers)
- Session push: GatewayStreamClient (ControlApi) dials Gateway SignalR hub /director-stream,
  MessagePack. Sends: Hello, then InvokeAsync("PushSnapshot", seq, SessionDto[]) / "PushDelta" /
  "RemoveSession"; one Interlocked _sequence shared by all sends. Snapshot func injected by
  ControlApiHost.BuildStreamClient (~line 736); event wiring in WireDoorbellPush (~825); periodic
  re-push timer already exists (reuse for repos).
- Gateway receive: DirectorHub (Gateway/Streaming/DirectorHub.cs) methods Hello/PushSnapshot/
  PushDelta/RemoveSession -> PushedSessionStore keyed Tenant -> directorId; tenant resolved from the
  authenticated device key, NEVER the payload. Clone as PushedRepositoryStore + new hub methods
  PushRepoSnapshot/RemoveRepo (never change existing signatures; old Gateway returns HubException
  on unknown method - harmless, sends are try/caught).
- GET /sessions shape to clone: GatewayEndpoints.Map (~753): ResolveReadTenant (403 unbound on
  hosted), registry.ListDirectors(tenant) + ?machine= filter, pushedSessions.TryGetFresh per
  Director, FleetRosterCache grace. Add GET /repositories + /worktrees the same way.
- Local relay: ControlEndpoints /fleet/sessions (~376): gw.ListFleetSessionsAsync else standalone
  fallback. Add /fleet/repositories + /fleet/worktrees; fallback reads RepositoryMonitor.Snapshot()
  - pass the monitor into ControlApiHost ctor (App.axaml.cs ~521) and into ControlEndpoints.Map.
- DTO casing: HTTP = camelCase web defaults; Python director.field() reads both. Stream = MessagePack
  (member-based). RepositoryStatus lives in Core; mirror a contract DTO in Gateway.Contracts for the
  push (SessionDto precedent).
- Python CLI: tools/cc-devthrottle/src/cli.py typer app; session_app pattern (~42/76/486);
  session_ops.list_sessions uses director.get_json("fleet/sessions"), rich Table box.ASCII, bare
  print for --json. Add repo_ops/worktree_ops + repo_app/worktree_app identically.
