# Turn push - running handoff note

Read `brief.md` first. This note is the compact state a fresh seat needs; it is rewritten at every
boundary, never appended to.

- **Branch:** `mission/turn-push`, worktree `D:\ReposFred\devthrottle-turn-push`, cut from `b71baccbf`.
- **Issue:** thefrederiksen/devthrottle#2638.
- **Phase 0 (the store): done, three Codex passes.** Tables `session_turns` (key tenant, session,
  generation key, ordinal) and `session_turn_heads` (one per session: current generation key + source
  text + start, contiguous watermark, agent facts, history state, `Revision` concurrency token).
  Migrations `AddSessionTurns` on both providers, snapshots in sync. `SessionTurnStore`: validated batch,
  one transaction per push with one retry on a lost race, idempotent rows, paged contiguous watermark,
  generation switch only when strictly later (millisecond precision on both sides, ties by key), whole-
  session retention judged on the head and race-free, aged rows of left generations dropped. Retention
  wired into `SessionHistorySweep`. Contracts `PushedTurn` / `TurnPushBatch` / `TurnWatermark`.
  Thirty-one store tests. Local gate green.
- **Next: Phase 1.** `DirectorHub.PushTurns(sequence, TurnPushBatch)` bound to the tenant and Director
  like `PushDelta`, writes through `SessionTurnStore.Append`, returns the `TurnWatermark`; `Hello`
  answers `WatermarksFor(directorId)` on `GatewayCapabilities`. Director side: `TurnPusher` in
  `CcDirector.ControlApi` reading through `SessionHistoryReader.Read`; generation = resolved transcript
  path (Claude) or the session id (others); `GenerationStartedUtc` = when the Director first saw that
  source this process lifetime; triggers: the Director's own Working-to-Waiting edge (where
  `NotifyDelta` fires in `ControlApiHost`), pointer change, reconnect backfill from the Hello watermarks,
  a slow safety sweep. `HistoryState` computed on the Director at push time (`HistoryStateDeriver`).
  Batches of at most `SessionTurnStore.MaxBatchSize`.
- **Not proven yet:** anything live. No reader uses the rows until Phase 2.
