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

- **Two inspection rounds on 3c and 4 (Codex, adversarial): FIXED**, commits `76e2ffa26` and `41c1d928e`.
  Round one found eight, six real - the worst being that a spent schedule retried for ever every ten
  minutes while the screen said the Gateway had stopped. Round two reviewed the FIXES and found nine, two
  of them introduced by round one: a lost wakeup between the rerun marker and the coalescing guard, and a
  two-pass bound that stranded a marker. Both rounds are written up in their commit messages.

## What is in flight right now

- **Main is GREEN.** Pull request 2651 merged at `ad4820fd6`; the parked suite was measured on that branch
  at 48.88 minutes, 2324 tests, 2320 passed, 4 skipped, 0 failed, `--blame-hang` produced no dump.
- **`mission/turn-push` holds 3c, 4 and both fix rounds**, rebased on main, default gate green
  (3269 Gateway unit tests, 1m15s - back inside the 120-second ceiling after the concurrency tests were
  rewritten to park on an await rather than hold a thread).
- **THE LAST GATE IS NOT YET PASSED.** A parked `Gateway.Tests` run against this exact tree ABORTED on
  2026-09-02 at 16:04 local, after 54.3 minutes. Read the next section before re-running it.
- **Holding off main while the hosted Gateway deploys.** The `devthrottle_internal - gateway` seat is
  shipping a statistics-store fix and waiting on a combined-tree run; nothing of this mission lands until
  it reports being out.
- The Director's own `turns` verb is deliberately still there. Production runs the previous Gateway build,
  which still asks for it. It goes when that Gateway is deployed, not before.

## The aborted parked run - what is known, and what is NOT

**Do not merge until this is resolved.** The run ended `Test Run Aborted`, and the shape matters:

- **2314 passed, 4 skipped, ZERO failed.** Nothing failed. The run HUNG, and `--blame-hang
  --blame-hang-timeout 8m` killed it and wrote a dump - the instrument doing its job.
- The hang was in `HostedImagePublishedArtifactTests.Every_published_entry_executable_fails_closed_
  without_the_hosted_contract`, which runs `dotnet publish` and spawns child processes. The same test
  passed in **4 seconds** in the 2026-09-02 run on `fix/turn-push-parked-suite`.
- **No code changed by this mission appears in any stack frame in the whole log** (checked on `   at `
  lines: no `WingmanVoiceService`, `VoiceRetryPolicy`, `SessionTurnStore`, `SessionVerbClient`,
  `TurnPusher`, `SessionConversation`).
- The `[GatewayHost] pipeline exception` entries in the log are disposed-service-provider teardown noise
  reaching `SkillStore` and `GatewayPublicUrl`. One appeared in the earlier PASSING run, four here.
- **Three other Gateway test suites from other worktrees were running on this machine at the same time**
  (`devthrottle-supervised`, `devthrottle-session-rules-p2`, `devthrottle.wt-wingman-too-old`).

**The honest state: this is very likely machine contention, and that is INFERENCE, not proof.** A
publish-and-spawn test exceeding an 8-minute inactivity window on a machine running three other suites
is the obvious reading, and no evidence gathered so far contradicts it - but nobody has yet watched the
test pass or hang again in isolation. Do not write "it was contention" into anything as fact until a
clean run says so.

**How to finish this:** re-run the parked suite against `mission/turn-push` **when the machine is
quiet** - no other `testhost.exe` from another worktree, and the per-user Gateway lock free
(`%LOCALAPPDATA%\cc-director	est-locks\gateway-test-suite.lock`). Green means open the pull request
(body drafted, see below) and merge. A second hang in the same place on a quiet machine means it IS
worth chasing, and the hang dump is the place to start.

Note the lock queue while doing this: any Gateway.Tests run takes a machine-wide lock in a
`[ModuleInitializer]`, so even a single-test filtered run queues behind another worktree's full run,
and gives up after 45 minutes. That is issue #2653.

## What is NOT proven

- No live run against a real Director and a real phone. Every proof so far is unit-level plus the parked
  suites.
- The retry schedule's timing has never been watched elapsing in a real session - only with the clock
  injected. Nobody has seen five attempts happen three minutes apart and the button appear on a phone.
- The Generate button's route was reasoned about and then pinned by an architecture guard; it has not been
  PRESSED after a schedule was exhausted. The guard proves the route does not consult the schedule. It does
  not prove the press produces audio.
- The rerun marker's two race windows are proven by arranged races in tests, not observed in production.
