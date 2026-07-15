# Gateway dependency audit - WORK IN PROGRESS

Status: DRAFT, being written. Do not read as findings yet.

## Confirmed so far (verified against origin/main, tree 0 behind)

- `GatewayConfig.IsEnabled` (src/CcDirector.Core/Configuration/GatewayConfig.cs:121) is
  `!string.IsNullOrWhiteSpace(Url)`. It is a STRING CHECK. It makes no network call and says
  nothing about reachability.
- `GET /fleet/sessions` (src/CcDirector.ControlApi/ControlEndpoints.cs:321-344): when
  `IsEnabled` is true the relay is attempted and a failure returns 502. The local-sessions
  fallback at line 339 is UNREACHABLE whenever a Gateway URL is configured.
- Chain proven: cc-devthrottle session list -> _get_sessions (session_ops.py:87) ->
  director.get_json("fleet/sessions") -> 502 -> DirectorError -> exit 1.
- Cascade: _resolve_target (session_ops.py:96) calls _get_sessions, so message send / ask /
  rename / hold / interrupt / buffer / role / done all fail when addressing a target by name,
  prefix or number - EVEN A LOCAL SESSION ON THE SAME MACHINE.
