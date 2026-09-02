# Turn push - running handoff note

Read `brief.md` first. This note is the compact state a fresh seat needs; it is rewritten at every
boundary, never appended to. Seats on this mission are named `Turn Push - <Role>`.

- **Branch:** `mission/turn-push`, worktree `D:\ReposFred\devthrottle-turn-push`.
- **Issue:** thefrederiksen/devthrottle#2638.
- **Phase 0 (the store): MERGED**, pull request 2640. `session_turns` + `session_turn_heads`, migrations on
  both providers, `SessionTurnStore`.
- **Phase 1 (the Director pushes, the Gateway stores): MERGED**, pull request 2645. `DirectorHub.PushTurns`,
  watermarks on `Hello`, `TurnPushBuilder` + `TurnPusher` on the Director, deterministic triggers.
- **Phase 2 (Chat reads the store): built, green, two Codex passes.**
  - `GET /sessions/{sid}/history` is a literal Gateway route served from the store
    (`SessionConversationEndpoint`), read INSIDE the caller's tenant scope. The `history` entry is gone from
    `TunnelCatchAllDispatch`, so reading a conversation never travels down the tunnel again.
  - `SessionConversationFold` decides the whole answer including the sentence an empty screen shows. Six
    outcomes, ordered: stored content, unsupported agent, unknown session, offline computer, computer too
    old to send, nothing sent yet, conversation not started.
  - `TurnPushCapabilityRegistry` records from each `Hello`, per (tenant, Director), whether that build sends
    conversations - the input that tells "not sent yet" from "that computer cannot send it".
  - The client renders the Gateway's `emptyText` verbatim and keeps only its own filter line.
- **Next: Phase 3** - feed the wingman from the store, delete the `turns` tunnel read from the voice path,
  and rebase the voice retry schedule and Generate button (branch `feat/voice-retry-schedule-then-button`,
  two Codex findings outstanding) on top of it.
- **Not proven:** no live run against a real Director and phone yet. The proof so far is unit-level plus the
  parked suites.
