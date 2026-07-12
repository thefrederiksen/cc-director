# Gateway Cleanup - Phase 0 Execution Plan and Worker Breakdown

Status: written 2026-07-12 by the Manager session ("Gateway Cleanup - Manager", session 432d5006,
machine SOREN_NORTH). This is the EXECUTION plan for Phase 0 (the tunnel protocol). The design it builds
on is fixed in two documents and must not be re-opened here: the mission brief
(gateway-cleanup-mission-2026-07-11.md) and the Phase 0 protocol
(gateway-cleanup-phase0-tunnel-protocol.md, including the four binding Architect refinements).

The Architect for this mission is session 51f1898e ("Gateway Cleanup - Architect"). Every design question
goes there first, never straight to the owner.

## What Phase 0 delivers (no deletions)

Phase 0 is purely additive. It extends the tunnel so that Phase 1 can delete the Director's remote REST
surface with the new path already in place. At the end of Phase 0:

1. The Director dispatches every read and write verb over the tunnel through one dispatch entry point that
   delegates to per-area executor classes. The REST routes still exist and call the SAME cores, so nothing
   is broken.
2. The up-stream primitive is implemented end to end: the `StreamUp` hub method on the Gateway, the
   `GatewayStreamRegistry` (streamId to sink) with pull-then-forward backpressure and the two lifecycle-race
   handlers, and the Director-side producers (terminal and finite file/screenshot byte reads) plus the
   `open-terminal-stream` / `read-file` / `screenshot-file` / `close-stream` / `terminal-input` verbs.
3. The hub's `MaximumReceiveMessageSize` and `StreamBufferCapacity` are set from `DirectorStreamLimits`.
4. Unit and acceptance tests pass: one representative read verb and one write verb (status parity with the
   REST route), the up-stream framing end to end (order + cancel), the backpressure acceptance test, and the
   terminal producer golden-bytes test.

The up-stream is implemented and tested but NOT yet wired into the browser-facing Gateway endpoints - that
swap is Phase 2. Phase 0 proves the machinery in isolation.

## The seams (confirmed by reading the code, 2026-07-12)

- Down-channel: `GatewayHost.SendCommandAsync` (GatewayHost.cs:1618) resolves the Director connection via
  `PushedSessions.GetActiveConnectionId(directorId)` and calls
  `hub.Clients.Client(conn).InvokeAsync<DirectorCommandResult>("Command", command, ct)`. The Director
  receives it in `GatewayStreamClient` (GatewayStreamClient.cs:139) via
  `_connection.On<DirectorCommand, DirectorCommandResult>("Command", ...)` which calls
  `SessionCommandExecutor.DispatchAsync` (SessionCommandExecutor.cs:54).
- The dispatch switch today has exactly ten write verbs (SessionCommandExecutor.cs:61-74).
- Up-channel today: the Director calls the hub methods `PushSnapshot` / `PushDelta` / `RemoveSession`
  (DirectorHub.cs:67-102). The new `StreamUp` hub method joins these.
- Hub registration: `builder.Services.AddSignalR();` (GatewayHost.cs:945). The two limits attach here via
  `.AddHubOptions<DirectorHub>(...)`.
- Terminal producer source: `TerminalStreamEndpoint.StreamSessionAsync` (TerminalStreamEndpoint.cs:99) is
  the exact cursor loop to lift into an `IAsyncEnumerable<DirectorStreamFrame>` (snapshot -> Size + Binary,
  then `Buffer.GetWrittenSince(cursor)` -> Binary, resize -> Size, exit -> Closed).
- The foundation contract file (DirectorUpStreamMessages.cs) is present, compiles, and encodes all four
  refinements: `DirectorStreamFrame`, `DirectorStreamFrameType`, `DirectorStreamLimits` (48 KB cap, buffer
  capacity 4, 4 KB envelope allowance), `OpenStreamRequest`, `OpenReadResponse`, `CloseStreamRequest`.

## The spine the Manager builds first (before any worker fan-out)

These four pieces are the load-bearing structure every worker plugs into. The Manager builds them so the
contracts are settled before parallel work starts. They are sequenced so each compiles on top of the last.

- Spine 1 - Gateway up-stream receive side. `GatewayStreamRegistry` (streamId to an `IStreamSink`), the
  `StreamUp(string streamId, IAsyncEnumerable<DirectorStreamFrame> frames)` hub method with pull-then-forward
  backpressure, the two lifecycle-race handlers (StreamUp-after-sink-gone cancels immediately;
  StreamUp-never-arrives sink timeout and teardown), and the hub options
  (`MaximumReceiveMessageSize = MaxBinaryFrameBytes + FrameEnvelopeAllowanceBytes`,
  `StreamBufferCapacity = StreamBufferCapacity`). Acceptance tests ride with this piece.
- Spine 2 - Director up-stream produce side. The Director-side stream registry (streamId to a
  `CancellationTokenSource`), the stream-verb handling that starts a producer and calls
  `_connection.SendAsync("StreamUp", streamId, ProduceFramesAsync(...))` and IMMEDIATELY returns Ok, and the
  idempotent `close-stream`. The terminal producer (lifted cursor loop) and the file producer (chunk at the
  cap, then a Closed/eof frame, with a cheap stat feeding `OpenReadResponse.TotalBytes`). Golden-bytes and
  in-process framing tests ride with this piece.
- Spine 3 - Executor-dispatch skeleton. Extend `SessionCommandExecutor.DispatchAsync` so the single dispatch
  entry delegates to per-area executor classes (`SessionReadExecutor`, `SessionWriteExecutor`, `GitExecutor`,
  and so on), each initially a thin class owning its area's verbs. The dispatch mapping stays the single
  source of truth. This is the structure that lets a worker own one area's executor file without colliding.
- Spine 4 - Representative-verb proof. Extract ONE read verb (proposed: `turns`) and ONE write verb
  (proposed: `resize`) fully through the new skeleton, with the REST route calling the same core, and the
  parity unit tests (Ok / BadRequest / NotFound / Conflict against the REST route). This proves the pattern
  a worker copies.

## Worker fan-out (after the spine lands)

Each worker owns ONE executor area, extracts that area's read/write handlers out of their MapGet/MapPost
lambdas into callable cores, points the existing REST route at the core, and adds the verb to its area
executor. Grouped from the brief's verb catalogue (Appendix A). Each worker runs in its own git worktree off
origin/main and produces its own pull request; it stages only its own files by name.

- Worker R1 - Session reads (executor `SessionReadExecutor`): snapshot, buffer, buffer/html, turns (done in
  the spine as the exemplar), summary, handover, handover-context, brief, recap, turn-summaries, queue,
  usage, context, history, wingman view, wingman/explain, github-urls.
- Worker R2 - Catalog and director-level reads (executor `CatalogReadExecutor`): git status, facts,
  coaching-categories, claude-sessions, repos list, interrupted list, fs/list, directory list.
- Worker W1 - Session state writes (executor `SessionWriteExecutor`): resize (done in the spine as the
  exemplar), clear-context, history-picker, mobile-mode, voice-mode, wingman-enabled, wingman/ask,
  wingman/act, execute-action, recap-generate, turn-summaries POST, rule-violations, recovery-prompt,
  state-vote, relink, request-deletion, cancel-deletion, handover-generate, chat.
- Worker W2 - Queue and git writes (executor `QueueGitExecutor`): the queue mutation group (POST / PATCH /
  DELETE / move-up / move-down / clear / send), git stage / unstage / discard / commit, create-from-github.
- Worker S1 - Byte and stream verbs on top of the spine producers: screenshots list (unary read),
  screenshot-file (finite up-stream), session file bytes (finite up-stream, the Local Files viewer),
  upload-image (down, unary or chunked). The terminal producer and read-file producer themselves are spine
  work; S1 fills in the remaining byte verbs against the same producer machinery.

Verbs no real client uses are NOT given a tunnel verb (brief decision): the local desktop UI, the
settings/agents configuration surface, tools/scheduler/dispatch/workspaces if unused remotely. Their routes
are deleted in Phase 1 and the handler stays in-process.

## Sequencing and merge discipline

- The spine (Spine 1 through 4) merges first, as one pull request or a short chain, human-verified to build
  and pass its tests. It changes no behaviour (additive), so the proof is: solution builds, new tests green,
  REST routes still call the shared cores.
- Only after the spine is on origin/main do the workers fan out, at most three open pull requests at a time
  (the soft cap), each a branch that lives under a day.
- No commit happens until the owner explicitly asks; each ask is per-commit and does not carry forward.
- The Phase 0 to Phase 1 boundary is a checkpoint: the Manager pings the Architect before starting the
  deletions in Phase 1.

## Open design question sent to the Architect (2026-07-12)

Two points, sent for a second set of eyes before the spine is built:

1. Where does stream-verb dispatch live? The open/close stream verbs need the live SignalR connection (to
   call `StreamUp`) and a per-stream `CancellationTokenSource` registry, neither of which
   `SessionCommandExecutor` holds. Proposed: stream verbs are handled inside `GatewayStreamClient` (which
   owns the connection), delegating frame production to producers that take only the `SessionManager`; the
   pure unary reads and writes stay in `SessionCommandExecutor` and its per-area executors. This keeps the
   connection concern out of the executor.
2. How to keep the central dispatch mapping from being a merge chokepoint when five workers extend it at
   once. Proposed: the dispatch entry maps a verb to its area executor by a registration each area owns, so a
   worker adds verbs by touching only its own executor file, not one shared switch statement.
