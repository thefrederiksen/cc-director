# Gateway Cleanup - Phase 2: the Gateway browser-leg re-point onto the tunnel (design + slot-5 proof plan)

Author: Manager session d1286c9f, 2026-07-12, machine SOREN_NORTH. Additive, Option-A-autonomous (build + merge
without a gate). This is the "new road carries traffic before the old road is removed" work that must land
BEFORE the Phase 1 Director-floor deletion, so a floor-only Director can be proven through the Gateway.

Invariant: EVERYTHING is additive and gated on the existing streamMode flag. streamMode OFF (production today)
= byte-identical to now (the Gateway keeps dialing the Director over HTTP). streamMode ON = the Gateway serves
the browser-facing legs through the tunnel. Both paths coexist until Phase 6 makes the tunnel mandatory. Nothing
on main changes behavior for production until the rollout gate.

## What is already true (Phase 0, on main)

- The tunnel carries 9 write verbs on the Gateway (kill, prompt, interrupt, escape, hold, patch, set-role,
  wingman-goal, create) via DirectorCommandRouter.TrySendAsync(sendCommand, ...); sendCommand is non-null only
  under streamMode. Roster is served from the Director push store under streamMode (PushedSessions).
- The up-stream primitive is complete on both ends but wired to NO browser leg:
  - Director: DirectorUpStreamHandler handles open-terminal-stream / read-file / screenshot-file / close-stream;
    OpenStreamRequest { StreamId, Path, ScreenshotId }; open-terminal-stream returns Ok (no body); read-file /
    screenshot-file return Ok with OpenReadResponse { long? TotalBytes, string? ContentType } or NotFound;
    close-stream is idempotent. terminal-input is a unary write (SessionWriteExecutor) that calls SendInput.
  - Gateway: DirectorHub.StreamUp(streamId, frames) -> GatewayStreamRegistry.ConsumeAsync pumps frames into a
    registered IStreamSink with pull-then-forward backpressure; Register(streamId, sink) mints the open-timeout
    and returns a teardown token; Close(streamId) is the browser-disconnect teardown. IStreamSink is
    WriteFrameAsync(frame) + CompleteAsync(reason). Phase 0 shipped only a test sink.

So Phase 2 = provide the two real sinks and branch the browser legs onto the tunnel under streamMode.

## Piece 1 - the up-stream browser legs (terminal, file, screenshot)

Two IStreamSink implementations (new, in src/CcDirector.Gateway/Streaming/):

- WebSocketStreamSink: wraps an accepted browser WebSocket. WriteFrameAsync translates one DirectorStreamFrame
  back into the EXACT browser terminal wire protocol (TerminalStreamEndpoint's protocol, unchanged for the
  browser): Size -> text {"type":"size","cols":C,"rows":R}; Binary -> a binary WS message of frame.Data; Closed
  -> text {"type":"closed","reason":R} then close the socket. CompleteAsync closes the socket if still open.
  Because the registry is pull-then-forward, the sink just awaits the one WS send and returns (no buffering).
- HttpResponseStreamSink: wraps the browser HttpResponse for a file / screenshot GET. On the open command's Ok
  it sets Content-Type and (when OpenReadResponse.TotalBytes is known) Content-Length, from the Ok BodyJson,
  BEFORE the first Binary frame. WriteFrameAsync writes Binary frame.Data to the response body and flushes;
  Size frames do not occur for a file read; Closed/eof completes the response. CompleteAsync finishes the body.

Browser-leg wiring (SessionWsProxyEndpoints.cs), each leg gains a streamMode branch that precedes the existing
ProxyAsync HTTP dial (the HTTP dial stays as the streamMode-off fallback, byte-identical):

- GET /sessions/{sid}/stream (terminal): if streamMode and the session's owning Director is known (owners
  cache), accept the WebSocket, mint streamId = fresh Guid, register a WebSocketStreamSink, send
  open-terminal-stream { StreamId } with SessionId=sid to the owning Director via sendCommand. On NotFound ->
  close with "session not found"; on Ok -> run a keystroke receive loop that forwards each browser message DOWN
  as a terminal-input unary verb { sessionId, bytes(base64) }, and await the registry teardown token. On browser
  disconnect -> registry.Close(streamId) + send close-stream { StreamId }. Else (streamMode off / owner unknown)
  -> the existing ProxyAsync HTTP dial.
- GET /sessions/{sid}/file (file view): if streamMode + owner known, mint streamId, register an
  HttpResponseStreamSink over ctx.Response, send read-file { StreamId, Path=<the ?path query> }; map NotFound ->
  404, BadRequest -> 400; on Ok set headers from OpenReadResponse and let frames stream into the response; await
  teardown. Else the HTTP dial. (NOTE: /sessions/{sid}/file is the Local Files viewer leg; confirm its current
  Gateway route - it may be inside the catch-all today - and give it the same up-stream branch.)
- /sessions/{sid}/screenshots/file and .../screenshots (bytes): same HttpResponseStreamSink pattern via
  screenshot-file { StreamId, ScreenshotId=<the ?name query> }. The LIST (/screenshots) is a unary read verb
  (screenshots-list), handled in Piece 2, not the up-stream.

sendCommand + the GatewayStreamRegistry + the owners cache are threaded into SessionWsProxyEndpoints.Map as new
optional parameters (null when streamMode off => the branch is never taken => byte-identical).

## Piece 2 - unary read/write dispatch replacing the catch-all Director-dial

The catch-all app.Map("/sessions/{sid}/{**rest}") today forwards ANY method+path to the Director over HTTP.
Under streamMode, dispatch it as a tunnel verb instead, with the SAME fall-back-to-HTTP-when-off rule.

- A Gateway-side path+method -> verb table, built directly from the checkpoint Appendix A1 route->verb mapping
  (it already enumerates every route and its verb). Examples: GET .../turns -> turns; GET .../buffer -> buffer;
  GET .../buffer/html -> buffer-html; GET .../summary -> summary; GET .../usage -> usage; GET .../context ->
  context; GET .../history -> history; GET .../git -> git-status (read-only, already shadowed); GET .../queue ->
  queue-read; GET .../github-urls -> github-urls; GET .../wingman -> wingman-view; GET .../handover -> handover;
  POST .../resize -> resize; POST .../clear-context -> clear-context; POST .../mobile-mode -> mobile-mode; POST
  .../queue -> queue-add; DELETE .../queue/{itemId} -> queue-remove; POST .../request-deletion ->
  request-deletion; DELETE .../request-deletion -> cancel-deletion; POST .../execute-action -> execute-action;
  ... (the full set is Appendix A1's DELETE-> lists). Director-level verbs (repos-list, facts,
  coaching-categories, claude-sessions, interrupted-list, create-from-github) keep SessionId="".
- Marshal: the HTTP request body (for writes) and/or query string become the verb's PayloadJson via the same
  request DTO the REST route used; the DirectorCommand carries SessionId=sid (or "" for director-level).
  DirectorCommandResult -> HTTP via the DirectorCommandRouter mapping already in use (status -> code, BodyJson ->
  the response, content type per verb; text/plain verbs carry BodyJson as a string with their content type).
- An unknown rest-path under streamMode is NOT silently dialed: it fails loud (the table is the source of truth;
  a missing entry is a build/dispatch error naming the path), so nothing slips back onto the HTTP leg silently.
  (The git-WRITE refusal at the top of the catch-all stays exactly as is - the Gateway is a read-only window on
  source control.)

This piece is mechanical but broad (~30+ verbs). It may split into its own PR after Piece 1, or a small sub-chain
by area (session reads, session writes, queue/git, director-level, byte lists), each with parity tests.

## Both-paths-coexist and the kill switch

- Every branch is `if (streamMode && ownerKnown) { tunnel } else { existing HTTP dial }`. streamMode off keeps
  today's behavior to the byte. This is the same kill-switch discipline the 9 write verbs already follow.
- No browser/phone contract changes: the browser still speaks the same terminal WS protocol, the same file GET,
  the same session REST verbs; only the Gateway's Director-facing leg moves from an HTTP dial to a tunnel verb /
  up-stream.

## PR chain (all additive, merged autonomously under Option A)

- PR A: the two IStreamSink implementations + the terminal/file/screenshot streamMode branches (Piece 1) + unit
  tests (sink frame translation golden bytes; a fake registry/owner e2e) + an integration test proving a
  streamMode Gateway serves a terminal + a file from a fake tunnel Director.
- PR B (may sub-split): the catch-all read/write verb dispatch table + marshal + reply mapping (Piece 2) +
  per-verb parity tests (tunnel result == what the HTTP dial returned for a representative read and write).
- Each PR: up-to-date-with-main AND audit-read-aware before merge (the TerminalPromptInjectionChokepointTests
  lesson - an intervening commit editing a file a source-audit test READS can red main).

## Slot-5 proof plan (tunnel-carries-all) - run after PR A + PR B merge, before Phase 1

Prove on a FULL-surface Director (pre-deletion) on slot 5 + a streamMode Gateway that the tunnel carries the
whole browser-facing surface, so Phase 1's floor-only re-proof is then just "the same, with the HTTP routes
gone":

1. Build a full-surface Director to slot 5: scripts/local-build-avalonia.ps1 -Slot 5. Launch ONLY via the
   cc-director-launch scheduled task (CLAUDE.md rule 0b; svchost parent, clean stdio for any spawned agent).
2. Run a Gateway with streamMode ON (config StreamMode=true) pointed so the slot-5 Director dials its tunnel.
   Confirm the slot-5 Director's tunnel connection is bound (DirectorHub Hello in the Gateway log) and the
   roster serves from the push store.
3. Through the Gateway (streamMode on), with a live agent session on the slot-5 Director, prove each rides the
   tunnel (confirm via the [DirectorCommandRouter] / [GatewayStreamRegistry] / [DirectorHub] log lines, not just
   a 200):
   - roster: GET /sessions shows the slot-5 session from the push store (no HTTP pull).
   - terminal: open /sessions/{sid}/stream; frames arrive via open-terminal-stream + StreamUp; a keystroke rides
     terminal-input.
   - prompt: POST a prompt; rides the prompt verb.
   - turns: GET /sessions/{sid}/turns; rides the turns verb (NOT an HTTP dial).
   - file: open /sessions/{sid}/file for a real file; rides read-file up-stream with correct Content-Length.
4. Negative control: flip streamMode OFF on the same Gateway and confirm the identical operations still work
   over the HTTP dial (proves the coexistence / kill switch).
5. Teardown: POST /shutdown to the slot-5 Director (graceful). Never force-kill.

Only after this passes do I cut the Phase 1 Director-floor deletion branch and RE-prove floor-only (every
deleted route 404s; the 6 floor routes + POST /reconnect answer; roster + terminal + prompt + turns + file still
work because they now ride the tunnel, not the deleted HTTP routes). That floor-only proof is the one I bring to
the Architect before the destructive merge (Soren's gate).

## Architect note on the slot-5 proof plan - 2026-07-12 (session 51f1898e)

The proof plan is strong - especially verifying each operation by the [DirectorCommandRouter] /
[GatewayStreamRegistry] / [DirectorHub] LOG LINES (not just a 200, which could be the HTTP dial) and the
streamMode-off negative control. APPROVED, with FOUR ADDITIONS so the proof exercises the up-stream INVARIANTS
and the original under-load failure this mission exists to fix ("works when a build spews output over a slow
phone link", not just a demo):

1. UNDER LOAD, not trivial: prove a LARGE file read (many frames, chunked at MaxBinaryFrameBytes, correct
   Content-Length) and a HIGH-VOLUME terminal burst (a command that spews output). A tiny read proves wiring,
   not the backpressure invariant (ruling 1); confirm bounded in-flight frames (the producer blocks on a stalled
   sink) - the whole reason native streaming was chosen.
2. CONCURRENCY / no-monopoly (ruling 2): run a terminal stream AND a large file read SIMULTANEOUSLY over the one
   shared tunnel connection and confirm BOTH make progress (neither starves) - proves a big frame does not stall
   the shared connection.
3. TEARDOWN (ruling 3): disconnect the browser mid-terminal-stream and confirm close-stream fires AND the
   Director producer STOPS (log evidence of the cancellation) - no leaked producer streaming into a dead sink;
   also exercise the file eof / natural-close path.
4. ERROR PARITY: the tunnel must reproduce the HTTP error mapping, not just happy paths - prove GET turns on a
   missing session -> NotFound / 404 over the tunnel, and read-file of a missing path -> 404, matching what the
   HTTP dial returned.

These four turn the proof from "it works" into "it works under the exact conditions that broke the old path".
Fold them into the slot-5 run, and where cheap into PR A's integration tests. Everything else in the plan stands
- proceed.

## PR C - the explicit GatewayEndpoints session routes - SETTLED 2026-07-13 (session 51f1898e)

PRs A/B1/B2 moved the browser stream legs + the catch-all read/write dispatch onto the tunnel. But the
EXPLICITLY-registered session routes in GatewayEndpoints.cs shadow the catch-all, so they were still dialing the
Director over HTTP. PR C gives each the same stream-first branch (DirectorCommandRouter.TrySendAsync, HTTP
fallback on a null return, byte-identical when streamMode is off). The Director side already handles every verb
(the read/write executors), so PR C is Gateway-side only.

- Landed in PR C (plain unary): buffer, summary, git-status, handover, recap (READ), wingman-view,
  request-deletion, cancel-deletion - plus the two slow-LLM verbs below. Covered by TunnelExplicitRouteProofTests
  (the unreachable-Director trick proves each rode the tunnel with the correct verb + payload).

- Slow LLM (wingman-ask, recap-generate) - RULING: plain SYNCHRONOUS unary threading the request
  CancellationToken, NO trigger-and-ack. SignalR client-results have no per-invocation timeout, keep-alive pings
  sustain a multi-minute await, and the ct mirrors RequestAborted exactly like today's HTTP forwarder, so the
  synchronous browser contract is byte-identical (the browser contract must NOT change). VERIFY in the
  whole-surface real-exe proof: a genuinely-slow call (>30s, past ClientTimeoutInterval) over the tunnel
  COMPLETES with no connection drop - closes the only residual risk.

- upload-image - RULING: OPTION 1 (CHUNK), as its own small additive PR. It is ruling 2 (no single message
  monopolizes the shared tunnel) plus the Phase 0 protocol's "uploads chunk across unary commands". The Director
  client (GatewayStreamClient) sets NO MaximumReceiveMessageSize (SignalR default 32 KB) - SET IT to the SAME
  bounded value as the hub (MaxBinaryFrameBytes + FrameEnvelopeAllowanceBytes ~= 52 KB), symmetric up/down. Chunk
  the image at MaxBinaryFrameBytes, reassembled on the Director by an uploadId (begin / chunk / complete), then
  run the existing SaveUploadedImage handler. Bounded both directions, no monopoly. Option 2 (raise the down-limit
  to a few MB) is REJECTED - it monopolizes the connection and raises the receive ceiling for ALL down-traffic.

  IMPLEMENTATION NOTE (Manager, 2026-07-13): shipped the CHUNKED path (option 1) with begin / chunk / complete
  reassembled on the Director by uploadId, with a 25 MB fail-loud ceiling, a 2-minute abandoned-upload sweep,
  and out-of-order / size-mismatch / bad-base64 all failing loud. It reuses the SAME save core as the single-shot
  path (SaveUploadedImage now delegates to a shared SaveImageBytes). One deviation from the exact mechanism above:
  the chunks ride as BASE64 in the command payload (the existing DirectorCommand.PayloadJson) sized at
  DirectorStreamLimits.UploadChunkRawBytes = 20 KB raw (~27 KB on the wire), NOT 48 KB binary with a raised
  ~52 KB Director-client receive limit. Reason: the SignalR client (the Director's HubConnection) does not expose
  a clean public MaximumReceiveMessageSize setter, so rather than depend on a fragile/internal API, the chunk is
  kept comfortably under the SignalR default (~32 KB) - which sidesteps the client-limit question entirely and
  still satisfies the load-bearing invariant (ruling 2: bounded, no single message monopolizes the tunnel). The
  cost is the base64 tax on an infrequent, small payload (images), which is negligible. Proven end to end by
  TunnelExplicitRouteProofTests: a 50 KB image uploaded over the tunnel in 3 chunks reassembles byte-for-byte and
  is written to disk. If the Architect prefers the literal 48 KB-binary/52 KB-limit mechanism, it is a small
  follow-up (add a byte[] payload field to DirectorCommand + set the client limit) - flagged for his call.

## PR D + the DirectorEndpointClient completeness sweep - 2026-07-13 (Manager a6a3406c)

Architect ruling this session (session 51f1898e): the completeness gate for the destructive cut is that EVERY
Gateway call site that HTTP-dials the Director is re-pointed onto the tunnel in Phase 2 (additive, streamMode)
BEFORE the cut, and that a grep confirms `DirectorEndpointClient` has ZERO callers left at the cut (only its own
definition remains). That zero-callers state is the operational definition of "the tunnel carries everything" and
makes the Phase 3 `DirectorEndpointClient` deletion a clean no-caller removal. Both PR-D escalations were
approved: wingman-voice + voice-turn are Phase-2 re-points (NOT deferred to Phase 3), and the three
director-level mutations fold into PR D.

### PR D (MERGED autonomously under Option A): the `/directors/{id}/*` surface onto the tunnel

Re-pointed in `GatewayEndpoints.cs`, each mirroring the create-verb pattern (`DirectorCommandRouter.TrySendAsync`
first; a null return falls back to the byte-identical HTTP dial; a non-Ok stream result collapses to the same 502
the HTTP null produced). All director-level, so `SessionId` is "". The Director-side verbs already existed
(CatalogReadExecutor from Phase 0 waves 2-4a; SessionWriteExecutor for the mutations from Wave 4a), and the verb
response DTOs match the `DirectorEndpointClient` return types exactly, so this is a pure Gateway re-point:

- Reads: `repos-list`, `facts`, `coaching-categories`, `claude-sessions`, `fs-list` (path in payload),
  `handovers-list`, `handovers-content` (path in payload), and `interrupted-list` (BOTH call sites: the
  `GET /interrupted` fan-out across every Director, and the restore-flow journal re-read).
- Mutations: `repo-delete` (path in payload), `interrupted-dismiss` and `interrupted-remove` (journal key /
  session key in payload) - each on the reporting Director (`via`).

Proof: `TunnelDirectorReadProofTests.cs` - a REAL streamMode `GatewayHost` + REAL DirectorHub + REAL MessagePack
SignalR client, the Director registered UNREACHABLE so a 200 with the expected body can ONLY have ridden the
tunnel; each test also asserts the exact verb and payload marshaling. 11/11 green; full Gateway suite green.

### The full remaining DirectorEndpointClient caller sweep (the PR-E+ map)

Grep of every `DirectorEndpointClient`-typed caller (the Account/Push `_client`s - RegisterAsync / HeartbeatAsync
/ ListDevicesAsync / RequestPushMessageDeliveryAsync - are a DIFFERENT client type and are OUT of scope).
Classified:

- GROUP A - director-level `/directors/*` (DONE, PR D above). No callers remain on these routes except the HTTP
  fallback branch (removed when the tunnel is made mandatory at the cut).
- GROUP B - Director-backed session-verb dials still bypassing the tunnel (PR E, Phase-2 re-point). Each already
  has a tunnel verb (turns / prompt / buffer / snapshot / create); they just call `DirectorEndpointClient`
  directly instead of `TrySendAsync`:
  - `GatewayWingmanVoiceEndpoint.cs` - GetTurns / PostPrompt / GetBuffer / GetSession (wingman-voice).
  - `GatewayVoiceTurnEndpoint.cs` - GetSession (+ owner-locate) (voice-turn).
  - `WingmanVoiceService.cs` - GetTurns.
  - `GatewayDictationEndpoint.cs` - PostPrompt + GetSession (dictation delivers transcribed text as a prompt).
  - `WingmanTrainingStore.cs` - GetBuffer.
  - `MachineSessionSpawner.cs` / `DirectorImplSessionDriver.cs` - CreateSession + GetBuffer (cron/worklist spawn
    and the seed-prompt impl driver).
- GROUP C - roster / health PULLS for which there is NO pull verb (the roster is a PUSH store). RULING SETTLED
  (Architect, 2026-07-13): the roster is served from the PUSH store, so re-point the pulls to READ
  `PushedSessions` - NO pull verb - and DROP `GetHealthDetailedAsync` in favour of tunnel-connectedness (the
  PushedSessionStore connection binding already answers "is this Director stream-connected") - NO health verb.
  One check owed: verify `PushedSessions` carries EVERY field each pull consumer reads (parity) so nothing goes
  missing.
  - `ExesEndpoints.cs` - ListSessionsWithStatusAsync (per-Director session+status list) -> read PushedSessions.
    DONE (PR E-C).
  - `GatewayEndpoints.cs` /healthz session count (was ListSessionsAsync) and the DELETE /directors/{id} live
    session gate (was ListSessionsAsync) -> read PushedSessions. DONE (PR E-C).
  - `Briefing/TurnEndWatcher.cs` - ListSessionsWithStatusAsync (turn-end catch-up sweep) -> read PushedSessions.
    Deferred into the voice PR (PR E-B voice cluster) because the sweep hands the owning Director's endpoint to
    WingmanVoiceService.GenerateAsync, so the endpoint->directorId change must land together with the voice
    service's own tunnel re-point.
  - `GetHealthDetailedAsync` -> DROP: its ONLY caller is `AdvertisedEndpointMonitor` (GROUP D, deleted Phase 3),
    so tunnel-connectedness becomes the health signal with NO Phase-2 re-point (the caller dies with the class).
    The GatewayEndpoints roster fan-out (`:549` ListSessionsWithStatus) was ALREADY re-pointed in Phase 1a - it
    reads PushedSessions first and only falls through to the HTTP pull as the coexistence path.
- GROUP D - dialing machinery DELETED in Phase 3 (NOT re-pointed): `AdvertisedEndpointMonitor` (whole class);
  `SessionWsProxyEndpoints` SessionWsForwarder + the `/sessions/{sid}/{**rest}` catch-all + LocateOwningDirector
  (the browser stream/file/screenshot legs already tunnel-branched in PR A); the verify / verify-ws callbacks;
  ControlEndpoint advertisement reads (DeriveDirectorBaseUrl).

On the zero-callers timing (CONFIRMED by the Architect, 2026-07-13): Groups A/B/C keep an HTTP fallback
else-branch during streamMode coexistence, so the literal "zero `DirectorEndpointClient` callers" grep passes
only when those fallback branches are removed (tunnel made mandatory) together with the Group-D deletions - i.e.
AT the cut, not before it. The PRE-PROOF gate the Architect clears the cut on is: EVERY `DirectorEndpointClient`
caller HAS a tunnel branch, AND the whole-surface real-exe proof (streamMode ON) exercises all of them. The
fallback else-branches still referencing `DirectorEndpointClient` are FINE pre-cut (they are the coexistence
path). The literal zero-callers grep is the at-cut check run right before deleting `DirectorEndpointClient`
(remove the streamMode-off fallbacks + delete Group D + grep-confirm zero callers + delete the client).

## PR E-B status (Group B) - 2026-07-13 (Manager d262b023)

The Group B session-verb dials are re-pointed through a single new choke point, `SessionVerbClient`
(`src/CcDirector.Gateway/Api/SessionVerbClient.cs`): it binds a resolved owning `DirectorDto` (its `DirectorId`
for the tunnel, its `ControlEndpoint` for the fallback) to the shared client + the sendCommand hook, and every
method is tunnel-first via `DirectorCommandRouter.TrySendAsync` (turns / buffer / prompt / create verbs) with the
HTTP dial as the byte-identical fallback (sendCommand null => stream mode off, or the Director has no active
stream). Owner resolution reuses `GatewayEndpoints.LocateSessionAsync` (made internal) so the push-store-first
location is shared, not re-implemented.

DONE and merged in PR E-B:
- `GatewayWingmanVoiceEndpoint.cs` - ResolveEndpointAsync's director-loop replaced by push-store resolve; the
  helpers (DetectMenuAt / PressAndSummarize / WaitForReply / CountTextWidgets) go through SessionVerbClient; Map
  threads sendCommand + pushedSessions + owners.
- `WingmanVoiceService.cs` + `WingmanTrainingStore.cs` - GenerateAsync / GenerateOnce / CaptureTraining /
  CaptureAsync take a SessionVerbClient (the service no longer holds a DirectorEndpointClient); the idle voice
  sweep (GatewayHost) and the turn-end path build the route from the owning Director.
- `Briefing/TurnEndWatcher.cs` (the Group-C-classified catch-up sweep, landed here with the voice cluster) -
  `TurnEndSignal.DirectorEndpoint` became `DirectorId` (the push-fed Observe already had the id and only
  converted it to a control URL); `SweepAsync` reads `PushedSessions.SnapshotFresh` under stream mode and
  HTTP-pulls only non-pushing (legacy) Directors.
- `GatewayDictationEndpoint.cs` - resolve push-store-first + inject through SessionVerbClient. The dictation
  DELIVERY marker (was the HTTP-only `X-Dictation-Delivery` header) now rides a new `PromptRequest.DeliveryUploadId`
  field; the shared `SessionCommandExecutor.PromptAsync` maps a non-empty DeliveryUploadId to
  `SendSource.Delivery`, so the tunnel prompt verb carries the Delivery signal with no header and the frozen
  `DirectorCommand` envelope is unchanged (the REST path still honors the header for back-compat). This was the
  marker/id-only case the Architect cleared for the DTO field (no clip bytes in the prompt).
- Proofs: `SessionVerbClientTests` (tunnel-vs-fallback routing + DeliveryUploadId marshaling + failed-result
  mapping) and `TunnelWingmanVoiceProofTests` (unreachable-Director + PushSnapshot: the wingman menu reads the
  session buffer over the tunnel by construction, proving the Map wiring threads sendCommand + the push store).

DONE and merged in PR E-B2 (#1465, Manager a2de9030): `MachineSessionSpawner.cs` / `DirectorImplSessionDriver.cs`
(cron / worklist spawn + seed-prompt). `SessionVerbClient` gained a director-level `CreateSessionAsync` (the
`create` verb, empty session id, `NewSessionRequest` payload -> `SessionDto`) plus a `ForDirector` factory for
callers that hold only a director id + control endpoint; the spawner + driver route create tunnel-first through
the resolved director id (HTTP fallback on the control endpoint) and the driver reads the session buffer through
the same `SessionVerbClient`. Director id + the stream hook threaded through `ICronWorkListDrainLauncher.LaunchAsync`,
the driver factory, `DirectorCronWorkListRunner`, `WorkListRunnerEndpoints.Map`, and the `GatewayHost` wiring.
Proof: `SessionVerbClientTests` (director-level create) + `TunnelSpawnerDriverProofTests` (unreachable control
endpoint => success can only be the tunnel).

## PR E-B3 (Group B, completeness-gate find) - 2026-07-13 (Manager a2de9030)

The Phase-2 completeness-gate sweep (run after E-B2) found ONE `DirectorEndpointClient` caller not in the A-D
classification and with NO tunnel branch and NO stream-mode guard: the **snooze expiry watchdog**
(`SnoozeExpirySweep`, Snooze Length mission), wired in `GatewayHost` with `readOnHold` = `client.GetSessionAsync`
and `forwardUnhold` = `client.SetHoldAsync` dialed unconditionally on the Director's control endpoint. `hold` and
the session snapshot are NOT in the 6-item Director floor, so post-cut those dials 404 and snooze expiry silently
stops (a snoozed session stuck on hold forever). Architect RULING (2026-07-13): re-point NOW as PR E-B3, additive
- per the completeness invariant every `DirectorEndpointClient` caller must have a tunnel branch before the cut.

DONE and merged in PR E-B3 (#1468): new choke point `SnoozeSweepDirectorClient`
(`src/CcDirector.Gateway/Api/SnoozeSweepDirectorClient.cs`), tunnel-first under stream mode with the existing
HTTP dial as the byte-identical fallback:
- the RAW `OnHold` READ rides the `snapshot` read verb (chosen over reading `PushedSessions`: the sweep's
  nudge->next-sweep-clears cycle needs the Director's post-nudge raw state promptly and byte-identically to the
  old `GetSession`; the round-trip is negligible for the few snoozed entries - Architect gave the call);
- the expiry NUDGE rides the `hold` write verb (`OnHold=false`).
- `SnoozeExpirySweep` is now director-id addressed: its resolve-to-an-endpoint gate became `isDirectorReachable`
  (reachable over the tunnel - stream-connected - OR over HTTP - advertised endpoint), so it survives the cut (a
  stream-only Director with no HTTP endpoint is still reachable); the read/nudge seams take the director id. The
  three-way decision logic is unchanged.
- Proof: `TunnelSnoozeSweepProofTests` (snapshot/hold verbs + payload + reachability gate, by construction);
  `SnoozeExpirySweepTests` updated to the id-addressed seams.

With E-B2 + E-B3 merged, every remaining `DirectorEndpointClient` caller is either a tunnel-first HTTP-fallback
branch (Groups A/B/C - fine pre-cut) or Group-D machinery deleted at the cut. The literal zero-callers grep lands
AT the cut (remove the fallbacks + delete Group D + delete the client), as ruled above.

## Architect ruling (retire the client-dead async voice-turn path) - SETTLED 2026-07-13 (session 51f1898e)

`GatewayVoiceTurnEndpoint.cs` (the async `POST /sessions/{sid}/voice-turn/submit` + upload/poll surface, issue
#376) drove the DIRECTOR's SSE endpoint `POST /sessions/{sid}/voice-turn` (`CcDirector.ControlApi/VoiceTurnEndpoint.cs`,
issue #351) over a RAW HttpClient reading staged `data:` events - a Gateway->Director HTTP dial NOT reachable by
the tunnel primitives without new SSE-up-stream work. It is CLIENT-DEAD: the only caller beyond the generated
`client-core/schema.ts` is the RETIRED native MAUI phone client (`phone/CcDirectorClient`); cockpit and mobile
both use `/wingman/voice-turn` (`GatewayWingmanVoiceEndpoint`), which runs the whole turn Gateway-side. RULING:
OPTION A - RETIRE (Option B tunnel-SSE-up-stream and Option C decompose both REJECTED as heavy work / needless
complexity for a dead feature). In PR E-B the Gateway endpoint + its wiring (GatewayEndpoints:123) + its two
dedicated tests (`GatewayVoiceTurnAsyncTests`, `VoiceTurnNoMicE2EHarnessTests`) + the manual
`scripts/test-voice-turn.ps1` are DELETED; the retired native client + its own tests stay as dead artifacts (no
compile dep on the Gateway - they harmlessly 404). The Director SSE `VoiceTurnEndpoint.cs` is on the Phase 1
DROP list, deleted AT the cut. The Gateway-side `GatewayTurnJobStore` / `VoiceTurnArchive` become dead once the
endpoint is gone; leaving them is harmless (a phase-1-cut cleanup, not this PR).

## CUT RESTORATION (SB-3b, do NOT re-delete): the /sessions/{sid}/{**rest} catch-all ROUTE

Seat-7 OVER-DELETED at the cut: it removed the whole `/sessions/{sid}/{**rest}` catch-all ROUTE (and its tunnel
dispatch wiring) when it should have deleted ONLY the HTTP-passthrough reverse-proxy LEG inside it. That orphaned
`TunnelCatchAllDispatch` (zero callers) and left the entire remaining browser-facing session verb set with NO
Gateway route: the reads turns/buffer-html/usage/context/history/github-urls/queue-read and the writes
resize/clear-context/history-picker/mobile-mode/voice-mode/wingman-enabled/relink/execute-action plus the voice
queue add/update/remove/move-up/move-down/send/clear. client-core actively calls these; the Cockpit/mobile
terminal + voice queue were broken. Surfaced by TunnelMechanismProofTests.Turns_read (404) during SB-3b.

RESTORED (Architect-approved Option A, SB-3b): `SessionWsProxyEndpoints.Map` re-maps the catch-all route
`/sessions/{sid}/{**rest}` -> resolve owner via `PushedSessionStore.TryLocate` (push store; a located owner is
always tunnel-connected) -> `TunnelCatchAllDispatch.TryDispatchAsync` (dispatch the verb over THE TUNNEL). The
dispatcher's verb TABLE is the EXPLICIT verb set (unknown verb -> 404), NOT a generic HTTP passthrough. An owner
not tunnel-connected -> 503. There is NO HTTP fallback (the HTTP reverse-proxy stays deleted). The catch-all is
the least-specific `/sessions/{sid}/...` route, so every literal route (stream/file/screenshots/buffer/prompt/
patch/...) wins over it; it is mapped before the Cockpit SPA site-root fallback so it claims these verb paths.
The SB-4 floor-only real-exe proof RE-VERIFIES this whole restored verb set works over the tunnel post-cut.
