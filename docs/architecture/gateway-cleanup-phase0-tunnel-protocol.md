# Gateway Cleanup - Phase 0: The Tunnel Protocol

Status: draft by the Manager session ("Gateway Cleanup - Manager", session 871b9100, machine SOREN_NORTH),
2026-07-11. This document defines the tunnel protocol that every later phase builds on. It is the
architectural spine: the full verb set the Director dispatches, and the one up-stream primitive that carries
continuous terminal output and finite byte reads. No code is deleted in Phase 0; this only extends the
tunnel so Phase 1 can delete the Director's remote REST surface with the new path already in place.

The name is "the tunnel" everywhere, per the mission decisions. The tunnel is the existing two-way SignalR
channel: the Director dials the Gateway's DirectorHub at `/director-stream`, binds its id with `Hello`, and
the Gateway drives it with SignalR client results.

## What already exists (the starting point)

- Down-channel (Gateway to Director): `GatewayHost.SendCommandAsync` calls
  `hub.Clients.Client(connectionId).InvokeAsync<DirectorCommandResult>("Command", command, ct)`. The Director
  handles it in `GatewayStreamClient` via `_connection.On<DirectorCommand, DirectorCommandResult>("Command", ...)`,
  which dispatches to `SessionCommandExecutor.DispatchAsync`.
- The command envelope: `DirectorCommand { CommandId, Verb, SessionId, PayloadJson }` and
  `DirectorCommandResult { CommandId, Status, BodyJson, Error }` with
  `DirectorCommandStatus { Ok, BadRequest, NotFound, Conflict, Error, Locked }`. This shape is FROZEN; we only
  extend the verb set, never change the envelope (mission decision).
- Ten verbs ride the tunnel today, all writes: `prompt`, `interrupt`, `escape`, `hold`, `kill`, `patch`,
  `create`, `wingman-goal`, `set-role`, `attach-mission`.
- Up-channel (Director to Gateway): the Director calls the hub methods `PushSnapshot`, `PushDelta`,
  `RemoveSession` to stream session state up. There is NO byte/stream up-channel yet - that is the new
  primitive this phase adds.
- The Gateway maps `DirectorCommandStatus` back to HTTP for the browser via `DirectorCommandRouter`
  (`ReadBody<T>`, `DescribeFailure`). This mapping is reused unchanged for every new verb.

## Primitive 1 - Unary command (request then response)

Every read and every write is a unary command: `SendCommandAsync` down, `DirectorCommandResult` back. The
reply body (`BodyJson`) is the serialized response DTO for a read, or the write's small acknowledgement DTO.
Nothing about the envelope changes; the work is:

1. Extract each surviving in-process handler out of its REST endpoint lambda into a callable core (the exact
   pattern the ten existing verbs already followed into `SessionCommandExecutor`). The REST route - until it
   is deleted in Phase 1 - calls that core; the tunnel verb calls the same core. They cannot drift.
2. Add a `case` to the verb dispatch. Reads that today live inline in a `MapGet` lambda (turns, history, git
   status, and the rest) get the same treatment writes already got: lambda body moves to a core method, the
   route calls it, the dispatch calls it.

Because the read handlers are spread across several endpoint files (not just `ControlEndpoints.cs`), the
Phase 0 dispatch grows beyond `SessionCommandExecutor`. Design choice: keep ONE dispatch entry point
(`SessionCommandExecutor.DispatchAsync`) and let it delegate to per-area executor classes
(`SessionReadExecutor`, `SessionWriteExecutor`, `GitExecutor`, and so on) so a Worker can own one area's
executor without colliding on one giant file. The dispatch switch stays the single source of the verb-to-handler
mapping.

### Verb naming

Verbs are lower-case-kebab, matching the existing ones (`wingman-goal`, `set-role`). A read verb is named for
its resource (`turns`, `history`, `git-status`, `handover`, `context`, `usage`, `queue`, `file-list`,
`directory-list`, and so on). A write verb is named for its action (`resize`, `clear-context`,
`request-deletion`, `cancel-deletion`, `mobile-mode`, `voice-mode`, `wingman-enabled`, `git-stage`,
`git-unstage`, `git-discard`, `git-commit`, and so on). The exhaustive verb list is fixed once the route
inventory (mission brief appendices) is folded in; this document fixes the SHAPE, the appendix fixes the SET.

### Session-addressed vs director-addressed

`DirectorCommand.SessionId` carries the target session for a per-session verb, or "" for a director-level verb
(`create` already does this; `create-from-github`, `repos-list`, `facts`, `coaching-categories`,
`interrupted-list` are director-level). No envelope change - just an empty `SessionId`.

## Primitive 2 - The up-stream (Director to Gateway byte/frame stream)

This is the one genuinely new piece of protocol. It carries BOTH the live terminal output (an open-ended
frame stream) and a finite byte read (a file or screenshot download), keyed by a stream id. It uses SignalR's
native client-to-server streaming, which fits our topology exactly: the Director is the SignalR client, and a
client streams UP to the server by passing an `IAsyncEnumerable<T>` (or `ChannelReader<T>`) to a hub method.

### The frame

```
public sealed class DirectorStreamFrame
{
    public string StreamId { get; set; } = "";      // correlates to the browser request the Gateway is serving
    public DirectorStreamFrameType Kind { get; set; } // Size | Binary | Closed
    public byte[]? Data { get; set; }                 // Binary payload (terminal bytes or a file chunk)
    public int Cols { get; set; }                     // Size frames only
    public int Rows { get; set; }                     // Size frames only
    public string? Reason { get; set; }               // Closed frames only ("session exited", "not found", "eof")
}

public enum DirectorStreamFrameType { Size = 0, Binary = 1, Closed = 2 }
```

One frame type set serves both uses. The terminal maps its existing wire protocol onto it directly:
`{"type":"size"}` becomes a `Size` frame, a raw PTY chunk becomes a `Binary` frame, `{"type":"closed"}`
becomes a `Closed` frame. A finite file read emits `Binary` frames until the file is exhausted, then one
`Closed` frame with `Reason = "eof"`. The Gateway's browser-facing leg is UNCHANGED - it still speaks the same
`/sessions/{sid}/stream` WebSocket protocol to the browser; it just now sources frames from the up-stream
instead of a dialed WebSocket, and translates each `DirectorStreamFrame` back into the browser frame it always
sent.

### Opening, running, and closing a stream

1. The browser opens its WebSocket (terminal) or issues its GET (file/screenshot) to the Gateway, unchanged.
2. The Gateway allocates a `streamId` (a fresh Guid), registers a sink for it in a `GatewayStreamRegistry`
   (streamId -> the browser-facing writer / channel), and sends a unary command down the tunnel:
   - terminal: `verb = open-terminal-stream`, payload `{ sessionId, streamId }`
   - file read: `verb = read-file`, payload `{ sessionId, streamId, path }` (or `screenshot-file` with an id)
   The command returns promptly: `Ok` if the Director accepted and started producing, or a typed failure
   (`NotFound` for a missing session, `BadRequest` for a bad path) which the Gateway maps to the browser
   exactly as the old dialed path did.
3. On accepting the open command, the Director starts a background producer and IMMEDIATELY returns `Ok` from
   the command handler (it does not await the whole stream inside the unary call). The producer calls
   `_connection.SendAsync("StreamUp", streamId, ProduceFramesAsync(...))`, passing an
   `IAsyncEnumerable<DirectorStreamFrame>`:
   - terminal producer: the exact cursor loop that `TerminalStreamEndpoint.StreamSessionAsync` runs today
     (snapshot first, then `Buffer.GetWrittenSince(cursor)` frames, size frames on resize, a Closed frame on
     exit) - but `yield return`ing frames instead of `ws.SendAsync`. The logic is lifted, not rewritten.
   - file producer: read the file in chunks, `yield return` a `Binary` frame per chunk, then a `Closed`/eof
     frame. Same for a screenshot file.
4. The Gateway hub method `Task StreamUp(string streamId, IAsyncEnumerable<DirectorStreamFrame> frames)`
   consumes the stream and writes each frame to the registered sink for that `streamId` (the browser
   WebSocket, or the buffered HTTP response). When the enumerable completes (a Closed/eof frame or natural
   end), the Gateway finishes the browser response.
5. Closing:
   - Browser disconnect (terminal) or Gateway-side cancel: the Gateway sends `verb = close-stream`, payload
     `{ streamId }`, which cancels the Director's producer CancellationToken for that stream, ending the
     `IAsyncEnumerable`.
   - Session exit / eof: the Director's producer emits a `Closed` frame and completes; the Gateway tears down
     the sink and the browser response.

### Terminal input stays unary (Primitive 3)

The browser's keystrokes are NOT part of the up-stream. Each keystroke frame the browser sends to the
Gateway becomes a unary `verb = terminal-input`, payload `{ sessionId, bytes }` (base64 in `PayloadJson`)
down the tunnel; the Director calls `Session.SendInput(bytes)` - the same call the old `ForwardClientInputAsync`
made. Small and frequent, but each is a normal command with an `Ok` reply. Uploads (an image, a dictated clip)
ride unary commands with the bytes in the payload, chunked across several commands if one message would be too
large, reusing the resilient upload the Gateway already exposes.

## Auth

The tunnel connection is authenticated once when the Director dials in and binds its id (`Hello`). Per-verb
bearer tokens on Gateway-to-Director calls disappear. No new auth is added for the up-stream: it rides the same
authenticated connection.

## Phase 0 test plan (no deletions, so both paths still exist)

- Unit-test the command dispatch for a representative read verb and a representative write verb: a valid call
  returns `Ok` with the expected `BodyJson`; a bad id returns `BadRequest`; a missing session returns
  `NotFound`; an exited session returns `Conflict` where the REST route did. Assert the tunnel result equals
  what the REST route would have returned (same core method).
- Unit-test the up-stream framing end to end in-process: a fake Director producer yields Size + Binary +
  Closed frames under a stream id; the Gateway registry routes them to a fake sink in order; a `close-stream`
  cancels the producer. Assert frame order and that cancel ends the enumerable.
- Assert the terminal producer, given a session buffer with known bytes, yields the same snapshot-then-tail
  sequence the old `StreamSessionAsync` wrote (golden bytes).

## What Phase 0 delivers to Phase 1

- The full verb dispatch in place on the Director, reachable over the tunnel, with the REST routes still
  calling the same cores (so nothing is broken yet).
- The up-stream primitive (`DirectorStreamFrame`, the `StreamUp` hub method, the `GatewayStreamRegistry`, the
  `open-terminal-stream` / `read-file` / `close-stream` / `terminal-input` verbs) implemented and unit-tested,
  but not yet wired into the browser-facing Gateway endpoints (Phase 2 does that swap).

Phase 1 then deletes the Director's REST surface down to the floor, and Phase 2 re-points the Gateway's
browser-facing legs from the dialed WebSocket/HTTP onto this up-stream and these verbs.

## Architect ruling (Phase 0 up-stream) - SETTLED 2026-07-11

The mechanism is SETTLED as the Manager designed it: SignalR native client-to-server streaming, ONE
`DirectorStreamFrame` primitive serving both the open-ended terminal and finite byte reads, terminal input
unary. It is NOT fire-and-forget frame pushes. The following four points are binding and must be written
into the design and the acceptance tests before implementation - they are the difference between "works in
a demo" and "works when a build is spewing output over a slow phone link".

1. Backpressure is the REASON for native streaming, and it is a first-class invariant, not an optimization.
   This is the one property fire-and-forget cannot give and native streaming gives for free, so we must not
   throw it away. The Gateway's `StreamUp` consumer MUST pull-then-forward: await the browser/sink write for
   a frame BEFORE pulling the next frame from the `IAsyncEnumerable`. Set SignalR `StreamBufferCapacity`
   small (single digits). Then a slow browser stops the Gateway pulling, which fills the SignalR channel,
   which makes the Director's producer `await` at its `yield return` - end-to-end backpressure from the
   browser all the way back to the PTY, with bounded memory everywhere. Acceptance test: a producer that
   yields faster than a deliberately-stalled sink drains must block the producer (bounded in-flight frames),
   never buffer unboundedly.

2. Frames are BOUNDED and small. Every `Binary` frame is capped (32 to 64 KB), and
   `MaximumReceiveMessageSize` is set deliberately on the hub to match, not left at the default. Two reasons:
   a file chunk must not exceed the message limit, AND the tunnel is ONE shared SignalR connection carrying
   every stream plus every unary command - a single large frame blocks that connection until it is sent, so
   a big file read would stall terminal output and keystroke commands. Small frames interleave; keep them
   small so no one stream monopolizes the shared connection. The file producer chunks at this bound; the
   terminal producer's tail frames are already small.

3. `close-stream` is LOAD-BEARING, not belt-and-suspenders - keep it and make it robust. Because the
   Director is the stream PRODUCER (client-to-server), the Gateway abandoning its read does NOT
   automatically stop the producer the way a server-to-client stream would; the explicit `close-stream`
   verb cancelling the producer's `CancellationToken` IS the stop mechanism. It must be idempotent: a
   `close-stream` for a stream that already completed (eof) or never started is a safe no-op. Also handle
   the two lifecycle races explicitly: a `StreamUp` that arrives after its sink is already gone (browser
   disconnected first) - the Gateway cancels it immediately; and a `StreamUp` that never arrives after an
   `Ok` open (Director died mid-open) - the sink has a timeout and is torn down. `streamId` is a fresh Guid
   per open and is never reused, so no frame can alias a later stream.

4. Finite reads should carry their total size when known. For a `read-file` / `screenshot-file`, put the
   total byte length in the open command's `Ok` `BodyJson` when the Director knows it up front, so the
   Gateway can set `Content-Length` on the browser response instead of falling back to chunked transfer.
   Optional for correctness, but it makes the file/screenshot viewer and downloads behave properly; do it
   where the size is cheap to stat.

Everything else in this document stands as written. The verb SET is fixed by folding in the brief
appendices (as the doc says); this ruling fixes the up-stream SHAPE. Reads and writes as unary commands,
one dispatch entry delegating to per-area executors, auth collapsing to the once-authenticated connection -
all approved. Proceed to implement Phase 0 on this basis.

## Architect ruling (Phase 0 dispatch and stream-verb placement) - SETTLED 2026-07-12

Two Manager questions from the Phase 0 execution plan (gateway-cleanup-phase0-execution-plan.md), both
settled here by the Architect (session 51f1898e). This is design authority, not carried to the owner. Both
proposals are approved; each gets one correction/clarification below. Write these into the spine before
worker fan-out.

### Ruling A - stream-verb dispatch lives at the connection layer; unary reads and writes stay in the executors

Approved as the Manager proposed, with one correction.

- The CONNECTION-BOUND stream verbs - `open-terminal-stream`, `read-file`, `screenshot-file`,
  `close-stream` - are handled in the layer that owns the live SignalR connection and the per-stream
  CancellationTokenSource registry: `GatewayStreamClient`, inside its existing
  `On<DirectorCommand, DirectorCommandResult>("Command", ...)` handler. That handler is the true single
  entry: it branches the small connection-bound stream family to a stream-verb handler (which owns the
  connection plus the Director-side stream-CancellationTokenSource registry, calls
  `_connection.SendAsync("StreamUp", streamId, ProduceFramesAsync(...))`, and returns Ok immediately), and
  forwards EVERYTHING ELSE to `SessionCommandExecutor.DispatchAsync`. The "one dispatch entry" property in
  this document is about the unary verb-to-core map; the stream family is correctly a separate branch at the
  connection layer, because only it needs the connection.
- Producers (terminal, file, screenshot) take only the `SessionManager` plus a CancellationToken. They stay
  connection-agnostic and unit-testable; the connection concern never leaks into them. Correct as proposed.
- CORRECTION: `terminal-input` is NOT a stream verb. It is a plain unary write (payload `{ sessionId, bytes }`
  calling `Session.SendInput(bytes)`); it needs neither the connection nor the stream registry, so it belongs
  in the write executor (`SessionWriteExecutor`), NOT the stream handler. Keep the stream handler to exactly
  the four connection-bound verbs above.
- The Director-side stream-CancellationTokenSource registry is owned by that same connection layer, and MUST
  tear down every live stream if the tunnel connection drops - a dropped connection can no longer deliver
  frames, so leave nothing producing into a dead socket. `close-stream` stays idempotent (refinement 3).

### Ruling B - the verb map is the union of per-area verb lists, composed once, so it is never a merge chokepoint

Approved as the Manager proposed, with a concrete non-magic shape so it stays explicit and fails loud.

- Define a small area contract: each area executor declares its own verbs and executes them, for example
  `interface ISessionCommandArea { IReadOnlyCollection<string> Verbs { get; } Task<DirectorCommandResult> ExecuteAsync(DirectorCommand cmd, CancellationToken ct); }`.
  `SessionReadExecutor`, `CatalogReadExecutor`, `SessionWriteExecutor`, `QueueGitExecutor` (and the byte-verb
  area) each implement it.
- In the SPINE (Spine 3), pre-create and pre-register ALL area executors ONCE, and build a verb-to-area
  dictionary at startup from their declared `Verbs`. `SessionCommandExecutor.DispatchAsync` looks the verb up
  and calls that area. After the spine, the registration site never changes again: a worker adds a verb by
  editing ONLY its own executor (its `Verbs` list plus its own handling), never the shared dispatch. That is
  what removes the chokepoint - so build all five area classes empty in the spine, and let workers fill them.
- Keep it EXPLICIT, no magic: a plain dictionary built from the visible per-area verb lists - NOT reflection
  scanning, NOT attribute discovery. The verb set stays readable in each executor file.
- Fail loud, no fallback (mission rule): at composition, if two areas declare the SAME verb, THROW
  immediately naming both areas and the verb - an accidental duplicate is a build-time failure, never a
  silent shadow. At dispatch, an unknown verb returns an explicit BadRequest or NotFound that names the verb;
  it is never swallowed. The union of the area verb-lists, guarded by that duplicate check, IS the single
  source of truth.

### Spine 4 exemplars (turns / resize) - approved

`turns` is a good representative read (it exercises session-not-found and exited-session); `resize` is a
clean representative write. Good choices to set the pattern a worker copies. Everything else in the execution
plan stands - proceed to build the spine on this basis.

## Architect ruling (unary shape coverage for the deferred verb group) - SETTLED 2026-07-12

The Manager surfaced a consolidated fork: several deferred verbs appear not to fit the unary tunnel shape
(DirectorCommandStatus = Ok/BadRequest/NotFound/Conflict/Error/Locked plus a JSON DTO body). Ruling: the
envelope stays FROZEN and NO new DirectorCommandStatus values are added - none are needed. Every apparent gap
resolves without changing the envelope. This ruling sets wave 3. Walk the categories:

- 201 Created / 202 Accepted and other success codes: NOT a gap. The Gateway already maps a verb's Ok to
  that verb's natural success code - the existing `create` verb returns 201 from Ok today. `create-from-github`
  and any accepted-async verb are the same: return Ok, the Gateway stamps the per-verb success code. No status
  added.
- 499 / request-cancellation (brief): NOT a status the server returns - 499 means the client disconnected
  before the response. Over the tunnel that is the command's CancellationToken (browser disconnect -> the
  Gateway cancels the command / sends close). Model it with the token, not a status value.
- 500 / 502 / 503: 500 is the existing `Error` status (a handler that faults). 502 / 503 are TRANSPORT-level
  "the Director is not reachable" - over the tunnel that is "the Director is not stream-connected", which the
  DirectorCommandRouter already answers (DescribeFailure) BEFORE any verb runs. Not a verb-handler concern; no
  status added.
- Non-JSON body (handover-context text/plain): the payload string rides in DirectorCommandResult.BodyJson (it
  is a string carrier); the Gateway serves it with the verb's known content type. No envelope change. (But see
  the local-vs-tunnel criterion below - handover-context may not be a tunnel verb at all.)
- async-submit (state-vote to the GitHub tracker): a NORMAL unary verb - return Ok (an acknowledgement)
  immediately and do the GitHub submit in the background on the Director, exactly as the REST endpoint does
  today. No distinct "action verb" shape. Option (b) is DECLINED: nothing here needs a new shape - streaming is
  already the up-stream, and async-submit is unary-acknowledge.
- Needs extra services or the Director version (handover, chat, wingman-ask, wingman-act, recap-generate,
  turn-summaries-generate, rule-violations, recovery-prompt, handover-generate): this is ordinary dependency
  injection, NOT an envelope problem. Extend the shared executor context / constructor with the services these
  cores need (the LLM / chat / feedback services) and the Director version string; then each lifts as a normal
  JSON-DTO unary verb. This is the "small context addition" half of option (a); the "extend
  DirectorCommandStatus" half is DECLINED as unnecessary.

Correction to the local-vs-tunnel criterion: a verb stays Director-LOCAL (route deleted in Phase 1, handler
kept in-process) if and only if NO remote consumer needs it - NOT because it is "HTTP-shaped". As shown above,
499 and text/plain are both reproducible over the tunnel, so being HTTP-bound is not itself a reason to keep a
verb local. So `brief` and `handover-context` are decided purely by "does any client-core path, Gateway call,
or migrated command-line verb invoke them": if no (as Appendix D indicates), local; if yes, they lift by the
mechanisms above. Confirm the consumer before deciding.

Long-running LLM verbs caution (chat, wingman-ask, wingman-act, and the generate group): a synchronous unary
command ties up the one shared tunnel connection for the whole call and risks the SignalR client-result
timeout. Prefer TRIGGER-AND-ACKNOWLEDGE for any verb that can exceed a few seconds - return Ok immediately and
deliver the result over an existing push / stream channel - so the shared connection is not held on a slow
model call (consistent with the up-stream ruling: no single message monopolizes the shared connection). For a
bounded, fast verb a synchronous unary is fine. Decide per verb; escalate any that is genuinely
synchronous-and-slow.

Net for wave 3: no envelope change, no new status values, no new verb shape. Extend the executor context with
the services plus the Director version; async-submit is unary-acknowledge; text/plain rides BodyJson with a
content type; cancellation is the token; local-vs-tunnel is decided by remote-consumer presence only.

Addendum 2026-07-12 (the shared-spine dependency addition): wave 2's R2 confirmed the root cause empirically -
`facts` needs the injected Director version and `repos-list` needs the live RepositoryRegistry, neither of
which is threaded through SessionCommandContext / SessionCommandServices today. The host-level dependencies to
thread through in ONE deliberate change are: the Director version string, the RepositoryRegistry, and the
specific LLM / chat / feedback services the wingman / recap / summary / chat group needs. Thread ONLY what is
actually needed, each as a named constructor-injected dependency (NOT statics, NOT a service locator), so the
context does not bloat into a god-object. Sequencing: because this touches the shared spine every executor
depends on, the MANAGER owns this addition as its own pull request (exactly like the original spine), merges
it, and only THEN fans out the wave-3 verb workers on top - which preserves the "each worker touches only its
own executor file" invariant and avoids context-file merge conflicts. Once it lands, `facts`, `repos-list`,
`handover`, and the service group all lift as ordinary JSON-DTO unary verbs on top of it.

## Architect ruling (wave 3 verb classification) - SETTLED 2026-07-12

The Manager is blocked pending a definitive per-verb decision for the ~14 deferred verbs, because Phase 1
deletes every non-floor route, so each must first be resolved as either "gets a tunnel verb" or "explicitly
removable / Director-local". This ruling resolves it.

### The decision rule (the ONLY criterion)

A deferred verb GETS A TUNNEL VERB if and only if a real remote consumer calls it. "Remote consumer" means any
of: (i) a Gateway call site in DirectorEndpointClient (Appendix B) - because Phase 2 re-points those onto the
tunnel; (ii) a web client path through packages/client-core (Appendix D); (iii) a migrated command-line or
scheduled caller. If NONE of those call it, the verb is DIRECTOR-LOCAL: its route is deleted at Phase 1, and
its in-process handler is kept only if the desktop app still calls it in-process (otherwise the handler is
removed entirely - that retain-vs-remove call is a Phase 1 detail, not a wave-3 blocker).

Correction to the earlier framing: a verb is NOT kept local because it is "HTTP-bound" or "async". As already
ruled, 499 is just the command CancellationToken, text/plain rides BodyJson with a content type, and an
async-submit is a unary Ok-acknowledge with background work. So `brief`, `handover-context`, and `state-vote`
are decided by the consumer rule like everything else - being HTTP-shaped or async is not a reason to keep them
local.

### Certain TUNNEL VERBS (remote consumer confirmed in the appendices) - build these in wave 3 now

These six have a confirmed Gateway (and sometimes client) consumer, so they ride the tunnel. They lift on top
of the shared-spine dependency addition above:

- `handover` (GET) - Gateway GetHandoverAsync (Appendix B) + client client.ts:709 (Appendix D). Needs the
  Director version from the extended context.
- `handover-generate` (POST /handover) - Gateway PostHandoverAsync (Appendix B).
- `recap-generate` (POST /recap) - Gateway PostRecapAsync (Appendix B). Needs the recap service.
- `wingman-ask` (POST /wingman/ask) - Gateway AskWingmanAsync (Appendix B) + WingmanAskForwardingTests. Needs
  the wingman/LLM service; long-running, so apply the trigger-and-acknowledge caution.
- `facts` (GET) - Gateway GetFactsAsync (Appendix B); Appendix A explicitly marks it "Gateway pulls this -
  tunnel verb". Needs the Director version.
- `repos-list` (GET /repos) - Gateway ListReposAsync (Appendix B) + client per-director repos (Appendix D).
  Needs the RepositoryRegistry from the extended context.

### VERIFY-THEN-RATIFY set (no obvious remote consumer in B/D) - one grep pass, then I ratify

For these eight, run ONE verification pass: grep each route's path across the Gateway (DirectorEndpointClient
and any other Gateway call site), packages/client-core, and the command-line tools. Bring me a small table
(verb -> consumer found, with file:line, or "none"). I ratify the local-vs-tunnel split from that evidence -
this is the deterministic rule applied to real call sites, not a guess.

- `handover-context` (GET, text/plain), `brief` (GET, 499/endpoint-local cache), `chat` (POST /chat),
  `wingman-act` (POST /wingman/act), `turn-summaries-generate` (POST /turn-summaries), `rule-violations`
  (POST), `recovery-prompt` (POST), `state-vote` (POST, async GitHub submit).

Expectation to sanity-check the grep against (not a pre-judgment): `wingman-act` likely has a Wingman-UI
consumer (would be a tunnel verb, service-backed, trigger-and-acknowledge); `brief` and `handover-context`
appear consumer-less in Appendix D (would be Director-local); the rest are genuinely unknown until grepped.

### You are NOT blocked - sequencing

Start wave 3 immediately; nothing waits on the verification pass:

1. Build the shared-spine dependency addition (Director version + RepositoryRegistry + the LLM/chat/feedback
   services through SessionCommandContext/Services) as your own pull request - it is the prerequisite for the
   six certain tunnel verbs anyway and depends on no verification.
2. In parallel, run the verify-then-ratify grep and send me the consumer table.
3. Then extract the six certain tunnel verbs on top of the merged spine addition, and the ratified subset of
   the eight, as wave-3 worker pull requests.

Only after every one of the ~14 is either a tunnel verb or an explicitly-ratified Director-local/removable does
the Phase 0 -> Phase 1 checkpoint open. That checkpoint (the first deletions) comes to me before anything is
deleted.
