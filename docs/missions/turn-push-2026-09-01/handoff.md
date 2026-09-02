# Turn push - running handoff note

Read `brief.md` first. This note is the compact state a fresh seat needs; it is rewritten at every
boundary, never appended to.

- **Branch:** `mission/turn-push`, worktree `D:\ReposFred\devthrottle-turn-push`.
- **Issue:** thefrederiksen/devthrottle#2638.
- **Phase 0 (the store): MERGED** to main as pull request 2640. `session_turns` + `session_turn_heads`,
  migrations on both providers, `SessionTurnStore` (validated batches, one transaction per push with a
  retry on a lost race, idempotent rows, paged contiguous watermark, generation switch only to a strictly
  later source with a deterministic tie-break, whole-session retention). Thirty-one tests.
- **Phase 1 (the Director pushes, the Gateway stores): built, green, four Codex passes.**
  - Gateway: `DirectorHub.PushTurns(sequence, batch)` writes through the store under the bound tenant and
    Director and answers the watermark; `Hello` returns this Director's watermarks on `GatewayCapabilities`.
  - Director: `TurnPushBuilder.Snapshot` reads a session's conversation through the ONE resolver
    (`SessionHistoryReader`, pointer-first), resolving the path once and using it for the generation, the
    messages and the history state. `TurnPusher` keeps per-session state under a lock, pushes bounded
    batches from the watermark, stamps each new source strictly later than the last (so a stale read can
    never outrank the current source), reconciles fully on Hello, retires state safely against the sweep,
    and hands outstanding work to a fresh call rather than leaving it for the sweep.
  - Stream client: `PushTurnsAsync`, `GatewaySupports`, an `onHello` callback, capabilities cleared when
    the connection drops. Triggers: the Director's own Working-to-Waiting/Idle edge, any-to-Exited,
    session creation, Hello, and a one-minute safety sweep.
  - Twenty-eight pusher tests plus three hub tests. Local gate green; the parked run was in progress at
    the time of writing.
- **Next: Phase 2** - serve `GET /sessions/{sid}/history` from the store, fold `HistoryState` on the
  Gateway, and remove `history` from `TunnelCatchAllDispatch`.
- **Not proven:** anything live. No reader uses the stored rows until Phase 2, so nothing a person sees
  has changed yet.
