# Mission Brief: Gateway Cleanup

Status: active mission. Written 2026-07-11 by the Architect session ("Gateway Cleanup - Architect",
session 7ae5fb4e, machine SOREN_NORTH). This document is the Architect's handover to the Manager
session. The Manager owns execution from here; the Architect does not gate the Manager.

Roles and escalation (per docs/new_architecture/mission-as-first-class-unit-of-work.md, amended
2026-07-11): the Architect ("Gateway Cleanup - Architect", session 7ae5fb4e) STAYS AVAILABLE for the
whole life of this Mission and is the standing go-to for design questions. The Manager takes every
DESIGN question, design fork, or brief-ambiguity to the ARCHITECT first (a second set of eyes), NOT
to Soren. The Architect settles what the design answers and escalates to Soren only the genuine
product decisions it cannot settle - and the Architect carries those to Soren. Execution questions
(sequencing, worker assignment, merge timing) stay with the Manager. Reach the Architect with
`cc-devthrottle message ask 7ae5fb4e "<design question>"`.

This is a distinct mission from "Gateway Connection" (docs/architecture/gateway-connection-mission-2026-07-11.md).
That one rebuilds the desktop app's Gateway sign-in and connect user interface. This one removes the
Director's remote control surface and moves every Gateway-to-Director conversation onto the two-way
stream. They touch neighbouring code; coordinate, do not collide.

## The one-sentence mission

The Gateway must never again dial a Director over the network. Every conversation between the Gateway
and a Director rides the two-way stream we already have (from here on, "the tunnel"). The Director's
Control API shrinks to a tiny local-only floor. Anything that used to reach a Director directly is
either moved onto the tunnel (Gateway traffic) or re-pointed at the Gateway (local tools), and if it
still tries to reach a Director directly it fails loudly.

## Why we are doing this (the reasoning, so nobody re-opens it)

Today the Gateway reaches a Director by dialing that Director's advertised control endpoint - which for
any cross-machine Director is its Tailscale address, not the local network. That single fact is the root
of a whole class of failures: a two-second probe timeout, a circuit breaker that then skips the Director
for thirty seconds, a stale advertised endpoint after a restart, Tailscale Serve not provisioned. The
symptom the owner hit was the phone's voice screen blanking to "reconnecting to this session's computer"
even though every machine was on the same local network - because nothing in that path used the local
network; it went Gateway to Tailscale to Director.

The tunnel removes the entire problem by construction. The Director already dials OUT to the Gateway and
holds that connection open (GatewayStreamClient to DirectorHub). The Gateway already sends commands DOWN
that same socket and awaits the reply (SignalR client results). So "reach the session's computer" stops
being a network operation the Gateway performs - the computer is already connected. No advertised
endpoint, no Tailscale hop for control traffic, no probe, no circuit breaker.

Three durable reasons this is the right end state, not just a bug fix:

1. One path or permanent split-brain. The moment a local shortcut is allowed "just for local agents," it
   never heals: it is the easy path, so tools keep using it and the tunnel path for those verbs rots and
   is never exercised. Two transports means two auth models, two failure modes, and double the test
   surface. This is the same logic as the project's "no fallback programming" rule.
2. The Gateway is the meter, and a meter with a bypass measures nothing. Central usage tracking (the
   DevThrottle Stats mission) only works if the traffic crosses the Gateway. Local direct-to-Director
   calls are invisible to it.
3. It finishes the account-is-source-of-truth epic (1069). One auth boundary, enforced once, at the
   Gateway. An open remote Director REST surface is an auth hole.

The owner chose the big-bang approach deliberately: strip one Director down to the minimal surface, run
it on test slot 5, and fix everything forward. The owner has access to every machine to verify. We are
not doing an incremental instrument-then-delete migration.

## The core design: two surfaces, cleanly separated

The mapping revealed that "the Director's REST API" is really two different things wearing one coat. The
cleanup is precisely the act of separating them.

### Surface A - the tunnel (all Gateway-to-Director traffic)

Every operation the Gateway performs against a Director moves onto the tunnel. The tunnel is the existing
SignalR channel: the Director dials the Gateway's DirectorHub at `/director-stream`, binds its id with
`Hello`, and the Gateway drives it with
`hub.Clients.Client(connectionId).InvokeAsync<DirectorCommandResult>("Command", command, ct)`
(GatewayHost.SendCommandAsync). Nine write verbs already ride it. This mission moves ALL the rest.

The tunnel needs three primitives:

1. Unary command (request then response). The existing `DirectorCommand` / `DirectorCommandResult` pair.
   Carries every read (the reply body is the DTO) and every write. The Director executes each verb
   through the SAME in-process handler its REST route calls today, so the two paths cannot drift.
   `DirectorCommandStatus` already models Ok / BadRequest / NotFound / Conflict / Error / Locked, which
   the Gateway maps back to the matching HTTP status for the browser. Extend the verb set; do not change
   the shape.

2. An up-stream (Director to Gateway) for continuous or unbounded bytes: the live terminal output and
   file/screenshot downloads. The Director is the SignalR client, so this is client-to-server streaming:
   the Gateway sends a unary "open" command carrying a stream id; the Director then streams frames up
   under that id until the Gateway sends "close" or the browser disconnects. ONE primitive serves both
   the terminal (an open-ended frame stream) and a file/screenshot read (a finite byte stream), keyed by
   the stream id. The browser-facing contract is unchanged: the browser still opens the same WebSocket to
   the Gateway at `/sessions/{sid}/stream`; only the Gateway-to-Director leg changes from a dialed
   WebSocket to consuming the Director's up-stream.

3. Input stays unary. Terminal keystrokes are already sent today as small writes (prompt with
   appendEnter false); each becomes a unary command down the tunnel. Uploads (an image, a dictated clip)
   ride unary commands with the bytes in the payload, chunked if a single message would be too large,
   reusing the resilient upload the Gateway already exposes.

Auth collapses. The tunnel connection is authenticated once when the Director dials in and binds its id.
Per-verb bearer tokens on Gateway-to-Director calls disappear.

### Surface B - the Director's minimal local floor (loopback only)

A deliberately tiny REST surface remains on the Director, bound to loopback, for same-machine callers
that must work even when the cloud is unreachable and that would be absurd to route through the Gateway.
This is the "very simple commands" set the owner described. It is NOT a general control surface.

Kept on the floor:

- `GET /healthz` - liveness for the launcher and local operator.
- `POST /shutdown` - graceful self-shutdown (the launcher's DirectorSupervisor uses this; so does the
  test-isolation teardown).
- `POST /reconnect` - NEW. Force the Director to tear down and re-dial the tunnel. The break-glass for a
  wedged stream. This is the "reconnect command" the owner asked for.
- Agent lifecycle IPC, same-machine only:
  - `POST /sessions/{sid}/claude-hook` - the installed agent hook posts lifecycle events here on every
    turn/start/stop.
  - `GET /sessions/{sid}/fleet-preamble` - the installed agent hook reads its preamble here at start.

  Rationale for keeping these two local rather than routing them through the Gateway: they are a session
  talking to its OWN Director on the same machine; routing them through the cloud would be a pointless
  round-trip (agent to Gateway to tunnel back to the same local Director) AND would break session startup
  during a Gateway outage. The Gateway still "sees everything" because the Director already relays session
  state UP the tunnel (PushSnapshot / PushDelta); the hook does not need to reach the Gateway for the
  Gateway to learn about it. This is the one design call in this document worth the owner's veto (see Open
  Decision 1).

Everything else on the Director's HTTP surface is deleted (the registration; the in-process handler stays
and is reached via the tunnel).

## What gets removed and what it becomes (the big-bang inventory)

The three appendices at the end list every route, call site, and dead-code location with file references.
This section is the summary the Manager sequences from.

### On the Director (src/CcDirector.ControlApi)

The Control API today registers roughly 130 routes (composition root `ControlApiHost.cs`, bulk in
`ControlEndpoints.cs`). After this mission it registers only the floor above. For every other route: keep
the in-process handler, delete the route registration, and expose the handler as a tunnel verb (Surface
A) if and only if the Gateway or a migrated client needs it. Routes that were only ever the Director's own
desktop or internal machinery (the local UI at `/`, `/login`, `/logout`, `/view`, the static xterm assets)
are simply deleted - the desktop app is the Director in-process and needs no HTTP to draw its own terminal.

The handshake pair `GET /verify/{nonce}` and `GET /verify-ws/{nonce}` are deleted: they exist only to
prove the Gateway can dial the Director, which it no longer does.

### On the Gateway (src/CcDirector.Gateway)

Delete, because the tunnel replaces them:

- `DirectorEndpointClient` - the entire remote HTTP client to a Director. Every read, write, and the
  upload-image byte transfer moves to a tunnel verb. Every call site listed in Appendix B re-points at
  `DirectorCommandRouter.TrySendAsync` (now the only path, no fallback).
- `SessionWsForwarder` / the Director-dialing half of `SessionWsProxyEndpoints` - the hand-rolled
  WebSocket and HTTP forwarder to the Director. The terminal stream and the screenshot/file byte proxies
  move to the tunnel up-stream. The browser-facing routes stay; their Director leg changes.
- The catch-all `/sessions/{sid}/{**rest}` passthrough. A generic HTTP passthrough cannot exist over the
  tunnel - every verb must be explicit. Enumerate exactly the per-session routes real clients (cockpit,
  mobile, the migrated command-line tools) use and make each an explicit tunnel verb; drop the rest.
- The nine existing tunnel-first verbs lose their HTTP fallback (Appendix B section 4). Stream-first
  becomes stream-only.

Delete, because they exist only to support dialing the Director and become dead once nothing dials it:

- The reachability circuit breaker in `DirectorRegistry` (ShouldProbe, RecordUnreachable, RecordReachable,
  WasEverReachable, the Reachability state, the cooldown/evict constants) and its consumers in the fleet
  aggregator and TurnEndWatcher.
- The advertised-endpoint re-verification loop, `AdvertisedEndpointMonitor` (whole file).
- The verify / verify-ws handshake: `VerifyCallbackAsync`, `VerifyStreamCallbackAsync`, and the
  `POST /directors/{id}/verify` endpoint that drives them.
- `TailnetEndpoint` / `ControlEndpoint` selection: they are the addresses all the above dialed. The
  Director no longer advertises a reachable control endpoint at all. Keep only what liveness needs (is
  this Director stream-connected), which the PushedSessionStore connection binding already answers.
- The Director-port Tailscale Serve provisioning in `TailscaleServeProvisioner` (the per-Director-port
  HTTPS mappings). Keep the Gateway's own front-door 443 mapping - that is how phones and browsers reach
  the Gateway, and it stays.

The roster aggregation stops pulling from Directors over HTTP and serves entirely from the pushed store
(what Directors stream up the tunnel). A Director that is not stream-connected is simply not drivable, and
the Gateway says so with a clear error - there is no HTTP fallback to reach it.

### Everything else that reaches a Director directly (Appendix C)

- The `cc-devthrottle` command-line tool (and `cc-status`, `cc-history`, which share its transport). Today
  every fleet verb (`session list`, `rename`, `done`, `spawn`, `message send`/`ask`/`broadcast`,
  `mission create`/`list`) goes to the local Director over loopback via `CC_DIRECTOR_API`, and the
  Director relays the fleet ones to the Gateway. Re-point these at the Gateway directly. The tool already
  has a Gateway path (its `schedule` verbs read `gateway.url` from local config); extend that pattern -
  resolve the Gateway URL and this machine's device credential from local config and call the Gateway's
  own session and fleet-messaging surface. Fleet messaging becomes Gateway-native (the Hub already gates
  broadcast, issue 1229); the Director's `/fleet/*` routes are deleted.
- The launcher's `DirectorSupervisor` shutdown call stays - `POST /shutdown` is on the floor.
- The agent lifecycle hook scripts (emitted by ClaudeHookInstaller / CodexHookInstaller) stay - their two
  endpoints are on the floor (see Surface B and Open Decision 1).
- Skills and scripts that hit a Director directly (`cc-settings-api` reads the instance file and calls
  `/settings*`; `capture-feature-screenshots.ps1`, `agent-session-isolation.ps1`, `test-voice-turn.ps1`).
  Re-point the fleet/session ones at the Gateway; the local-config ones (cc-settings-api) either stay on a
  narrow local settings floor or move in-process to the desktop app - decide per skill during Phase 4.
- The 23 integration test files that spin up a real `ControlApiHost` and drive it over loopback HTTP.
  These must be rewritten to drive the in-process handlers directly or exercise the tunnel loop. This is a
  first-class phase, not cleanup: the build is not green until they pass against the new surface.

## The tunnel verb catalogue (what the Manager must define)

Every verb maps one-to-one to an existing in-process Director handler; the work is binding it to the
tunnel instead of a route, not writing new behavior. Group them so a small set of workers can each own a
group.

- Session reads (unary, reply body is the DTO): session snapshot, buffer, turns, history, summary, recap,
  git status, handover, handover-context, brief, wingman view, context, usage, queue, interrupted list,
  repos list, facts, claude-sessions, coaching categories, filesystem list, directory list.
- Session writes (unary): prompt, interrupt, escape, hold, patch, kill, request-deletion, cancel-deletion,
  role, mission, mobile-mode, voice-mode, wingman-enabled, wingman ask/act/goal, execute-action, resize,
  clear-context, history-picker, recap-generate, handover-generate, recovery-prompt, turn-summaries,
  state-vote, rule-violations, the queue mutations, the git stage/unstage/discard/commit group,
  create-session and create-from-github.
- Byte and stream (up-stream primitive): terminal output stream, screenshot-list plus screenshot-file
  bytes, session file bytes (the Local Files viewer), upload-image (down, unary or chunked), dictation
  delivery (down, the existing resilient upload feeding a prompt).
- Gateway-native, not a Director verb: fleet messaging (send/ask/broadcast/spawn/sessions) and scheduling
  already belong to the Gateway; the command-line tool calls the Gateway for these and the Director's
  `/fleet/*` routes are removed.

Verbs no real client uses (internal Director-only features, the local desktop UI, the settings/agents
configuration surface, tools/scheduler/dispatch/workspaces if unused remotely) are NOT given a tunnel
verb; their routes are deleted and the handler stays in-process for the desktop app's own use.

## Decisions already made - do not re-litigate

1. The Gateway never dials a Director. Every Gateway-to-Director operation rides the tunnel. There is no
   HTTP fallback; a Director that is not stream-connected is not drivable and the Gateway says so plainly.
2. Big bang, not incremental. Strip one Director to the floor, run it on test slot 5, fix forward. No
   instrument-then-delete, no dual-run period per verb.
3. The name is "the tunnel" in all code comments, logs, documents, and conversation.
4. The Director keeps a tiny loopback-only floor: healthz, shutdown, reconnect, and the two agent
   lifecycle IPC endpoints (claude-hook, fleet-preamble). Everything else is deleted.
5. The in-process handlers are NOT rewritten. Each verb binds an existing handler to the tunnel; the REST
   route that used to call it is deleted. The stream path and any surviving local path call the same code.
6. Auth collapses on the tunnel: the connection is authenticated once at dial; per-verb tokens on
   Gateway-to-Director calls are removed.
7. The browser-facing and phone-facing contracts do not change. The terminal WebSocket and every
   `/sessions/...` route the cockpit and mobile call stay exactly where they are on the Gateway; only the
   Gateway's Director-facing leg changes.
8. The command-line tools route through the Gateway, reading the Gateway URL and device credential from
   local config exactly as `cc-devthrottle schedule` already does. Fleet messaging is Gateway-native.
9. The dead dialing machinery is removed in full: reachability circuit breaker, advertised-endpoint
   monitor, verify/verify-ws handshake, Director control-endpoint advertisement, and the per-Director-port
   Tailscale Serve mappings. The Gateway front-door 443 mapping stays.
10. No fallback programming; fail loudly with a named cause. Plain English, no abbreviations, ASCII only,
    in all code and output.
11. Windows first: build and human-verify every phase on SOREN_NORTH against the real Gateway. The Mac
    gets a single verification pass at the end (one codebase, no porting step).

## The work, in phases

Each phase is implemented, merged to origin/main per the trunk rule, and human-verified before the next
begins - EXCEPT that this is a big-bang cut, so Phases 1 through 3 will leave the tree unable to drive a
Director the old way on purpose; the proof for those phases is the new path working on slot 5, not the old
path still working.

- Phase 0 - Tunnel protocol. Define the full verb set (extend the `DirectorCommand` verb dispatch on the
  Director side to cover every group in the catalogue) and add the up-stream primitive (terminal output
  and finite byte reads) to the hub and the Director stream client. Unit-test the command dispatch and the
  stream framing. No deletions yet.
- Phase 1 - Director floor. On the Director: route every kept operation through the tunnel dispatcher;
  delete the REST surface down to the floor (healthz, shutdown, reconnect, claude-hook, fleet-preamble).
  Build to test slot 5 with scripts/local-build-avalonia.ps1 and launch via the cc-director-launch
  scheduled task (never from this session's process tree). Proof: a session on slot 5 is driven end to end
  - roster, terminal stream, prompt, a read (turns), a file view - entirely through the tunnel, with the
  Director exposing nothing but the floor.
- Phase 2 - Gateway to tunnel. Re-point every Gateway endpoint at the tunnel: delete DirectorEndpointClient
  and its call sites' HTTP paths, move the terminal and byte proxies to the up-stream, remove the catch-all
  passthrough and the nine fallbacks. Proof: cockpit and mobile drive slot 5 with no Director HTTP dial
  anywhere (verified by log and by grep for the removed client).
- Phase 3 - Delete the dialing machinery. Remove the reachability circuit breaker, AdvertisedEndpointMonitor,
  verify/verify-ws, the Director control-endpoint advertisement, and the Director-port Tailscale Serve
  mappings. Proof: the Gateway builds and runs with none of it, and a Director with no advertised endpoint
  is fully drivable.
- Phase 4 - Migrate the local callers. Re-point cc-devthrottle / cc-status / cc-history at the Gateway;
  make fleet messaging Gateway-native; update the cc-settings-api skill and the scripts; update the
  fleet-comms skill text and the session-start preamble (which today says "you reach the fleet through
  your own Director" - it becomes "through the Gateway"). Keep the hook floor. Proof: an agent runs the
  full cc-devthrottle verb set against the Gateway from a machine, and the hooks still fire locally.
- Phase 5 - Tests green. Rewrite the 23 loopback ControlApiHost integration tests to drive the in-process
  handlers or the tunnel loop. Proof: the whole solution builds and the test suite passes.
- Phase 6 - Make the tunnel mandatory and finish. Remove the stream-mode configuration flag and any
  remaining "stream off" branch so the tunnel is the only path; roll the new Director to the owner's real
  machines; the Mac verification pass. Proof: the fleet runs on the new Director everywhere, and the
  original failure (the phone voice screen reaching a Director that is on the same local network) cannot
  recur because no control traffic uses an advertised endpoint.

## Definition of done for the mission

1. The Gateway contains no code that dials a Director over HTTP or WebSocket. DirectorEndpointClient,
   SessionWsForwarder's Director leg, the catch-all passthrough, and the dialing-support machinery
   (reachability breaker, advertised-endpoint monitor, verify handshake, Director control-endpoint
   advertisement, Director-port Tailscale Serve) are gone.
2. The Director's Control API exposes only the floor: healthz, shutdown, reconnect, claude-hook,
   fleet-preamble. Every other former route is deleted; its handler is reached only through the tunnel or
   in-process.
3. No non-Gateway caller reaches a Director directly except the agent lifecycle hooks and the launcher
   shutdown. The command-line tools, skills, and scripts go through the Gateway.
4. The solution builds and all tests pass against the new surface.
5. The whole fleet runs on the new Director on the owner's machines, verified by the owner, and the Mac
   verification pass is done.
6. A final verification report (HTML, in docs/reviews/) shows a session driven end to end through the
   tunnel and a Director exposing only the floor, with the roster, terminal, a prompt, and a file read all
   working with no advertised endpoint present.

## Open decision for the owner (one) - RESOLVED

Open Decision 1 - RESOLVED 2026-07-11 by the owner: the two agent lifecycle endpoints (claude-hook,
fleet-preamble) STAY on the Director's local loopback floor. They are not merely convenient to keep local,
they are Director-local by nature: both operate on the live in-process session object on the Director that
spawned the agent (fleet-preamble is built from that session's record; claude-hook mutates that session's
transcript pointer), and they must re-fire on /clear and /compact, which only the local CLI observes.
Routing them through the Gateway would force the Gateway to loop right back to the same Director over the
tunnel to touch local state, for zero tracking gain (the Gateway already learns session state from the
up-tunnel push), and would make sessions fail to start or get their preamble during a Gateway outage.
Everything that is an actual fleet operation (the command-line verbs) still moves to the Gateway.

Note on which agents this affects: only Claude Code and Codex install a SessionStart hook today
(ClaudeHookInstaller / CodexHookInstaller). Claude uses both endpoints (preamble injection plus the
transcript-pointer POST, because Claude rotates its session id and transcript on clear/compact); Codex
uses fleet-preamble only. No other agent CLI uses these endpoints. The floor must keep both.

## Appendices (the exhaustive inventory)

The Architect mapped the full surface with three read-only passes. The exhaustive tables (every Director
route with file and line; every DirectorEndpointClient method with its Gateway call sites; every
non-Gateway direct caller with file and line) are recorded in the Architect session transcript and should
be reproduced into this document's appendices by the Manager before Phase 1, or regenerated from the code,
so the big-bang deletion list is complete and traceable. Key anchors:

- Director routes: composition root src/CcDirector.ControlApi/ControlApiHost.cs; bulk in ControlEndpoints.cs;
  the Control API is hosted in-process by the desktop app at src/CcDirector.Avalonia/App.axaml.cs.
- Gateway dialing: src/CcDirector.Gateway/Discovery/DirectorEndpointClient.cs (all methods);
  src/CcDirector.Gateway/Api/SessionWsProxyEndpoints.cs and the SessionWsForwarder within it;
  the catch-all at SessionWsProxyEndpoints.cs (the `/sessions/{sid}/{**rest}` map);
  the tunnel path at src/CcDirector.Gateway/Api/DirectorCommandRouter.cs and GatewayHost.SendCommandAsync.
- Dead-once-cut machinery: src/CcDirector.Gateway/Discovery/DirectorRegistry.cs (reachability),
  src/CcDirector.Gateway/Discovery/AdvertisedEndpointMonitor.cs (whole file),
  src/CcDirector.Gateway/Tailscale/TailscaleServeProvisioner.cs (Director-port mappings only),
  the verify methods in DirectorEndpointClient.cs and the POST /directors/{id}/verify endpoint.
- Non-Gateway callers: tools/cc-devthrottle, tools/cc_shared/director.py, tools/cc-status, tools/cc-history;
  the hook installers src/CcDirector.Core/Claude/ClaudeHookInstaller.cs and Codex/CodexHookInstaller.cs;
  the launcher src/CcDirector.Launcher/DirectorSupervisor.cs; .claude/skills/cc-settings-api; the scripts
  under scripts/; the 23 integration tests under src/CcDirector.Gateway.Tests.

The exhaustive tables below were regenerated from the code by the Manager (three read-only passes),
2026-07-11, before any deletion. Line numbers are absolute per file and are a snapshot; re-grep before
editing a specific site.

### Appendix A - Director routes (118 total, all under src/CcDirector.ControlApi/)

Composition root wires every `*.Map(...)` at ControlApiHost.cs:403-436. There is NO MapFallback and NO
static-file middleware - every route is an explicit `Map*`, so this list is exhaustive. There is NO
`reconnect` HTTP route today; `POST /reconnect` on the floor is NEW work in Phase 1.

FLOOR (keep): GET /healthz (ControlEndpoints.cs:86); POST /shutdown (:3292); POST /sessions/{sid}/claude-hook
(:2521, Session.UpdateClaudeSessionPointer + RelinkClaudeSession); GET /sessions/{sid}/fleet-preamble (:255,
FleetPreamble.Build); plus NEW POST /reconnect (force tunnel re-dial).

Session reads -> tunnel verbs: GET /sessions (:229), /sessions/{sid} (:239), /wingman (:1050),
/wingman/explain (:824), /github-urls (:981), /buffer (:1249), /buffer/html (:1301), /turns (:1329),
/summary (:1395), /handover (:1458), /brief (:1496), /handover-context (:1606), /recap (:1652),
/turn-summaries (:2072), /queue (:2304), /git (:1952), /usage (SessionUsageEndpoint.cs:19),
/context (SessionContextEndpoint.cs:22), /history (SessionHistoryEndpoint.cs:25).

Session writes -> tunnel verbs: POST /sessions (:3112, exec create), POST /sessions/github (:3127),
DELETE /sessions/{sid} (:3167, exec kill), PATCH /sessions/{sid} (:1218, exec patch), prompt (:2266),
interrupt (:2421), escape (:2443), history-picker (:2463), clear-context (:2488), resize (:2553),
role (:1134), mission (:1197), hold (:909), mobile-mode (:850), voice-mode (:879), wingman-enabled (:946),
wingman/ask (:731), wingman/act (:771), wingman/goal (:1098), execute-action (:3318), recap (:1689),
turn-summaries POST (:2088), rule-violations (:1929), recovery-prompt (:2008), state-vote (:2106),
relink (:1995), git/stage (:1984) git/unstage (:1986) git/discard (:1988) git/commit (:1990),
queue POST (:2314) PATCH/{itemId} (:2343) DELETE/{itemId} (:2329) move-up (:2357) move-down (:2369)
DELETE queue (:2381) {itemId}/send (:2395), request-deletion POST (:3204) DELETE (:3226),
POST /fanout-local (:2681), POST /handover (:2145), POST /chat (:1905).

Byte/stream -> up-stream primitive + unary: GET /sessions/{sid}/stream WS (TerminalStreamEndpoint.cs:56),
GET /file (:1010), GET /sessions/{sid}/file (:1034, Local Files), GET /screenshots (:2625),
GET /screenshots/file (:2652), DELETE /screenshots/file (:2670), POST /sessions/{sid}/upload-image (:2574),
POST /sessions/{sid}/voice-turn (VoiceTurnEndpoint.cs:50), GET /dictate WS (DictationEndpoint.cs:90),
POST /voice/command (:1806), GET /voice/status (:1827), POST /voice/utterance (:1837),
PUT /voice/utterance/{id}/chunk/{index} (:1848), POST /voice/utterance/{id}/complete (:1868),
POST /tts (:2022), GET /tts/status (:2056).

Local desktop/internal UI (DELETE outright): GET / (:156), /login (:167) POST (:177), /logout (:207),
/sessions/{sid}/view (:213), GET /verify/{nonce} (:105), GET /verify-ws/{nonce} (:121) [these two are the
Gateway handshake - delete in Phase 3, they break registration; coordinate], xterm.js/css/canvas
(TerminalStreamEndpoint.cs:49/51/53), dictate.html/worklet/overlay (DictationEndpoint.cs:70/76/82).

Config surface (per-verb decision in Phase 4 - tunnel verb, desktop in-process only, or narrow local floor):
/settings GET/PUT (SettingsEndpoint.cs:37/39) + detect/test (:56/70/84/97); /settings/agents 12 routes
(AgentsEndpoint.cs:110-335); /tools 5 routes (ToolsEndpoint.cs:35-114); /workspaces GET (:25) /{slug} (:31);
GET /history (WorkspacesEndpoint.cs:39); /scheduler (:24) /{name}/run (:38); POST /dispatch
(DispatchEndpoint.cs:26); GET /facts (FactsEndpoint.cs:29, Gateway pulls this - tunnel verb).

Fleet messaging (/fleet/*) -> DELETE, becomes Gateway-native: GET /fleet/sessions (:301), POST /fleet/send
(:329), /fleet/broadcast (:405), /fleet/ask (:585), /fleet/spawn (:689).

Missions/repos/catalogs/fs/crash-recovery/admin: POST /missions (:1159) GET (:1173) /{mid} (:1181);
/repos GET (:2764) POST (:2795) PATCH (:2822) DELETE (:2782) /repos/overview (:2846); /coaching/categories
(:2924); /claude-sessions (:2948); /claude-transcripts (ClaudeTranscriptsEndpoint.cs:21); /handovers GET
(:3009) POST (:3031) DELETE (:3056) /handovers/content (:3076); /fs/list (:3097); /interrupted GET (:3246)
DELETE /{id}/{pid} (:3253) DELETE /{id}/{pid}/sessions/{sid} (:3263); POST /admin/backfill-numbers (:3282).

Handlers that MUST survive (reached via tunnel or in-process): the SessionCommandExecutor verbs already
shared by REST + stream (create, kill, prompt, patch, hold, interrupt, escape, set-role, attach-mission,
wingman-goal). Read handlers live inline in their MapGet lambdas today and get extracted into executor cores
per the Phase 0 protocol design. `ControlEndpoints.Map(session, directorId)` is the DTO mapper, NOT a route.

### Appendix B - Gateway to Director dialing (src/CcDirector.Gateway/)

DirectorEndpointClient (Discovery/DirectorEndpointClient.cs) - the entire remote HTTP client. Methods and
their heaviest call sites: VerifyCallbackAsync(65)/VerifyStreamCallbackAsync(110) [verify handshake,
GatewayEndpoints.cs:377/389]; GetHealthAsync(165)/GetHealthDetailedAsync(183)
[AdvertisedEndpointMonitor.cs:38]; ListSessionsAsync(206)/ListSessionsWithStatusAsync(227) [aggregator
GatewayEndpoints.cs:219/492/1418, TurnEndWatcher.cs:140, ExesEndpoints.cs:54]; GetSessionAsync(248) [9 sites
incl. SessionWsProxyEndpoints.cs:298 locate-owner, GatewayHost.cs:731]; GetWingmanAsync(263)/
AskWingmanAsync(283)/SetWingmanGoalAsync(310)/SetRoleAsync(334)/SetHoldAsync(358)/KillSessionAsync(385)/
RequestSessionDeletionAsync(408)/CancelSessionDeletionAsync(435)/PatchSessionAsync(457)/GetBufferAsync(481)/
GetTurnsAsync(505)/PostPromptAsync(602)/PostInterruptAsync(629)/PostEscapeAsync(643)/UploadImageAsync(662)/
ListReposAsync(698)/DeleteRepoAsync(711)/GetFactsAsync(728)/ListCoachingCategoriesAsync(741)/
ListClaudeSessionsAsync(754)/ListHandoversAsync(767)/GetHandoverContentAsync(780)/ListDirectoryAsync(793)/
CreateGitHubSessionAsync(809)/CreateSessionAsync(829)/GetSummaryAsync(849)/GetGitAsync(865)/
GetHandoverAsync(883)/PostHandoverAsync(896)/GetRecapAsync(916)/PostRecapAsync(929)/GetInterruptedAsync(962)/
DismissInterruptedAsync(976)/RemoveInterruptedSessionAsync(995)/PostShutdownAsync(1011). Full call-site
table is in the Manager's working notes; every site re-points at DirectorCommandRouter.TrySendAsync.

WS/byte forwarding (Api/SessionWsProxyEndpoints.cs): browser-facing routes STAY (their registration), the
Director-dialing leg MOVES to the tunnel up-stream: GET /sessions/{sid}/stream (map 55), /dictate (61),
/screenshots/file (77), /screenshots (88), the catch-all /sessions/{sid}/{**rest} (101) [DELETE the
catch-all; enumerate explicit verbs], /directors/{id}/settings (131), /directors/{id}/backfill-numbers (164).
The dialer itself: SessionWsForwarder (388) - ForwardAsync(407), ForwardWebSocketAsync(416, connect 428),
PumpAsync(457), ForwardHttpAsync(487, send 505), pooled HttpClient(395); ProxyAsync(197),
LocateOwningDirectorAsync(288). Address pickers: ForwardDestination(321), DeriveDirectorBaseUrl
(GatewayEndpoints.cs:2061).

Dead-once-cut machinery: (a) reachability breaker in Discovery/DirectorRegistry.cs - consts 47/50/53,
Reachability 60-66, ShouldProbe(227), RecordReachable(238), WasEverReachable(251), RecordUnreachable(337),
sweeper evict 461-473; consumers TurnEndWatcher.cs:136/143/146 and GatewayEndpoints.cs:404/483/485-487/494/
496/735. (b) AdvertisedEndpointMonitor.cs whole file (138 lines); refs GatewayHost.cs:288/825/826/1533-1534;
plus DirectorRegistry.RecordEndpointProbeResult(301, store 290) and DirectorDto.AdvertisedEndpoint* die with
it. (c) verify/verify-ws trio + POST /directors/{id}/verify (GatewayEndpoints.cs:363) and registry
MarkTwoWayVerified(259)/MarkStreamVerified(275). (d) TailscaleServeProvisioner.cs - KEEP front door 443
(FrontDoorHttpsPort const 53, QueueServeOn 122, WatchFrontDoorCore 285, _frontDoorTimer 142); DELETE
per-Director port mappings (DirectorPortMin/Max 65-66, HandleAdded 323, HandleRemoved 337, ShouldMap 351,
_portsById 80). NOTE: FleetRosterCache.cs:64/91 RecordReachable/Unreachable is a DIFFERENT class - do not touch.

Nine existing tunnel verbs whose HTTP fallback becomes stream-only (GatewayEndpoints.cs, stream/http lines):
kill(927/930), wingman-goal(1006/1009), set-role(1024/1027), hold(1044/1047), patch(1090/1098),
prompt(1137/1146), interrupt(1197/1200), escape(1213/1216), create(1289/1297). Router:
DirectorCommandRouter (ReadBody 49, DescribeFailure 53); wired when _streamMode on (GatewayHost.cs:1091/1370).

### Appendix C - Non-Gateway direct callers

Shared transport tools/cc_shared/director.py resolves CC_DIRECTOR_API only (director_base_url() :31-37); this
is the module re-pointed at the Gateway. cc-devthrottle verbs (session_ops.py / mission_ops.py): session list
(:90 GET fleet/sessions), rename (:213 PATCH sessions/{sid}), done (:241 POST request-deletion), spawn local
(:441 POST sessions) / --machine (:443 POST fleet/spawn), message send (:305 POST fleet/send) / all (:295 POST
fleet/broadcast) / ask (:331 POST fleet/ask), mission create (mission_ops.py:38 POST missions) / list (:59).
cc-devthrottle settings = local config.json DIRECT, no HTTP, no conversion. cc-devthrottle schedule
(schedule_ops.py:46-55) is THE MODEL to replicate: reads gateway.url + gateway.token from local config, calls
Gateway /cron/* directly. cc-status (cli.py:43 GET fleet/sessions); cc-history (cli.py:64 fleet/sessions,
:85 GET sessions/{sid}/history).

Hooks (STAY, floor): ClaudeHookInstaller.cs emits POST /sessions/{sid}/claude-hook (:44) + GET
/sessions/{sid}/fleet-preamble (:53); CodexHookInstaller.cs emits GET fleet-preamble only (:34).
Launcher (STAYS): DirectorSupervisor.cs:100 POST /shutdown (port from instances/*.json).

Skills/scripts: cc-settings-api (configure_settings.py) discovers Director from instances/*.json and calls
GET/PUT /settings + /settings/detect/* + /settings/test/gateway (Phase 4 decision: narrow local floor or
in-process). scripts/capture-feature-screenshots.ps1 (GET/POST /sessions, DELETE /sessions/{sid});
agent-session-isolation.ps1 (POST /shutdown, GET /healthz - floor, fine); test-voice-turn.ps1 (GET /sessions
only, rest is Gateway); test-degradation.ps1 (prompt/buffer/resize/git/DELETE). Gateway-only scripts
(redeploy-gateway.ps1 etc.) are out of scope.

23 integration test files under src/CcDirector.Gateway.Tests (~271 tests) each `new ControlApiHost` over
loopback - the Phase 5 rewrite target: ControlApiHostTests(23), DirectorSurfaceEndpointTests(9),
RestApiSelfServiceEndpointsTests(16), CockpitParityEndpointsTests(13), SettingsEndpointTests(4),
AgentsEndpointTests(18), ScreenshotEndpointsTests(9), FleetMessagingTests(12), MessageStewardEndpointTests(3),
FanoutAndEventsTests(8), DirectorEventsAndFactsTests(6), HandoverInfoEndpointTests(6), DispatchEndpointTests(8),
ExecuteActionEndpointTests(10), ToolRunEndpointTests(8), ChatEndpointTests(5), VoiceEndpointTests(5),
VoiceTurnEndpointTests(9), GatewayVoiceTurnAsyncTests(33), DictationEndpointTests(6), StreamCommandTests(30),
WingmanAskForwardingTests(3), GatewayHostTests(17). Shared harness: TestEnvironment.cs, DirectorRootCollection.cs.
SessionsAggregationTests.cs uses FAKE Kestrel Directors - excluded from the 23.

### Appendix D - Which Director routes the web clients actually call (catch-all replacement set)

Regenerated 2026-07-11 by the Manager. Every per-session and per-director call from apps/cockpit and
apps/mobile funnels through the shared library packages/client-core (the only exception is the cockpit
service worker fetching GET /sessions at apps/cockpit/public/sw.js:100). So re-pointing is ONE library, not
two apps. This appendix fixes the explicit tunnel-verb set that must replace the `/sessions/{sid}/{**rest}`
catch-all; the Phase 2 worker reconciles each row against Appendix A (a few client paths - /transcribing,
/wingman/voice*, /wingman/menu* - must be classified Director-backed vs Gateway-native before wiring).

Catch-all-dependent TODAY (no explicit schema route - these MUST get an explicit verb or lose their Director
backend; all in packages/client-core/src/api/client.ts unless noted): GET history(:309), POST
clear-context(:373), POST history-picker(:387), the whole queue group GET(:424)/POST(:484)/DELETE(:557)/
DELETE {itemId}(:497)/PATCH {itemId}(:521)/{itemId}/send(:509)/move-up(:534)/move-down(:546),
GET git(:465), GET file?path=(:613), GET handover(:709), POST transcribing(:878), POST voice-mode(:1565),
POST wingman/voice/stop(:1585). Catch-all dependence is confirmed in source comments at client.ts:299 and :603.

Already explicit (route exists, just loses HTTP fallback): POST prompt(:331, also the terminal keystroke
pump terminal/interactive.ts:15 + keys.ts:1), escape(:345), interrupt(:358), GET screenshots(:638),
screenshots/file(:597), DELETE screenshots/file(:658), POST upload-image(:676), POST hold(:852),
DELETE /sessions/{sid}(:894), PATCH /sessions/{sid} rename(fleetClient.ts:164).

Per-director: GET /directors/{id}/repos(:784), POST /directors/{id}/sessions(:824), GET/PUT
/directors/{id}/settings(fleetClient.ts:308/324), DELETE /directors/{id}(exesClient.ts:114). Collections:
GET /sessions roster(:280, fleetClient.ts:123), GET /directors(:752), GET /interrupted + its DELETE/restore
(fleetClient.ts:214/238/260/281).

WebSockets actually opened by clients: ONLY GET /sessions/{sid}/stream (terminal/interactive.ts:352 +
terminal/stream.ts:259). /sessions/{sid}/dictate is defined in schema but NOT opened - dictation uploads
audio over HTTP to Gateway-native /dictation/*. So the up-stream primitive's terminal producer is the single
WebSocket leg to move; the file byte producer serves GET /sessions/{sid}/file and the screenshots reads.

Gateway-native (NOT Director-backed, out of this cleanup): /wingman/tts, /wingman/queue, /wingman/utterance/*,
/dictation/*, /ingest/*, /cron/jobs*, /account/*, /push/*, /exes/*, /gateway/*, /transcription/*, /turnbriefs*,
/explain*, /signin, /healthz. schema.ts also declares many per-session routes no client here calls (buffer,
summary, recap, wingman, wingman/ask, wingman/goal) - still tunnel verbs for other consumers per Appendix A,
but not required by the client cleanup.
