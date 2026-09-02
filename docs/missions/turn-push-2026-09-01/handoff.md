# Turn push - running handoff note

Read `brief.md` first. This note is the compact state a fresh seat needs; it is rewritten at every
boundary, never appended to. Seats on this mission are named `Turn Push - <Role>`.

- **Branch:** `mission/turn-push`, worktree `D:\ReposFred\devthrottle-turn-push`.
- **Issue:** thefrederiksen/devthrottle#2638.
- **Phase 0 (the store): MERGED**, pull request 2640. `session_turns` + `session_turn_heads`, migrations on
  both providers, `SessionTurnStore`.
- **Phase 1 (the Director pushes, the Gateway stores): MERGED**, pull request 2645. `DirectorHub.PushTurns`,
  watermarks on `Hello`, `TurnPushBuilder` + `TurnPusher` on the Director, deterministic triggers.
- **Phase 2 (Chat reads the store): MERGED**, pull request 2646. `GET /sessions/{sid}/history` is a literal
  Gateway route served from the store inside the caller's tenant scope; the `history` entry is gone from
  `TunnelCatchAllDispatch`; `SessionConversationFold` decides the whole answer including the sentence an
  empty screen shows; `TurnPushCapabilityRegistry` tells "nothing sent yet" from "that computer cannot send
  it"; the client renders the Gateway's `emptyText` verbatim.
- **Phase 3a (the wingman narrates from the store): MERGED**, pull request 2648.
- **Phase 3b (waiting for a spoken answer watches the store): MERGED**, pull request 2649.
- **Phase 3c (the retry schedule, then the button): COMMITTED, NOT MERGED** - commit `d2738932e`.
  `VoiceRetryPolicy`: five automatic attempts, three minutes apart, counted against the TURN (a digest of
  the reply) rather than reset by an observed transition; a spent schedule is re-read after ten minutes so a
  turn that changed unobserved is never stranded. `VoiceDisplayFold` words the gave-up verdict from the
  count and turns the Generate button on once the schedule is spent.
- **Phase 4 (one resolver, and no transcript read anywhere): COMMITTED, NOT MERGED** - commit `061b90b0d`.
  `SessionVerbClient.GetTurnsAsync` and the dispatcher's `turns` entry are deleted; the Director's eight
  path-by-formula call sites go through `SessionHistoryReader.ResolveTranscriptPath`.
  `OneTranscriptResolverArchitectureTests` enforces both on the compiled intermediate language and was
  watched failing (reverting one call site reddened it, naming `ControlEndpoints::ComputeTurnCount`). It
  runs in the DEFAULT gate.

## What is in flight right now

- **Main is RED in the parked Gateway suite** and pull request **2651** (`fix/turn-push-parked-suite`,
  worktree `D:\ReposFred\devthrottle-redfix`) is the fix: the three voice tests that watched for a `turns`
  tunnel read now seed the store and watch for `screen-grid`. A full parked `Gateway.Tests` run against
  that branch is what gates the merge.
- **After 2651 merges:** rebase `mission/turn-push` on main, run `-Parked`, then land 3c and 4.
- The Director's own `turns` verb is deliberately still there. Production runs the previous Gateway build,
  which still asks for it. It goes when that Gateway is deployed, not before.

## What is NOT proven

- No live run against a real Director and a real phone. Every proof so far is unit-level plus the parked
  suites.
- The retry schedule's timing has never been watched elapsing in a real session - only with the clock
  injected.
