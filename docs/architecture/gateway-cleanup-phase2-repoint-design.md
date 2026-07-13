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
