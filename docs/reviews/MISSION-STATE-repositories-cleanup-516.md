# Mission state - Repositories cleanup fixes (issue 516)

Durable Architect state. Any reseat continues from here. Keep current.

- Branch: `fix/repositories-cleanup-safety-516`
- Worktree: `D:/ReposFred/devthrottle-repo-cleanup-516`
- Base: origin/main 9fde3414 (fetched 2026-07-24)
- Input record committed: docs/reviews/repositories-post-merge-review-2026-07-24.md
- Governance: .claude/skills/mission/SKILL.md (four laws). Architect lands; Codex (different family) inspects before main.

## Conduct (from brief)
- Verify EACH finding against origin/main first. A wrong finding is REJECTED with evidence, not fixed.
- Every fix fail-closed. One commit per finding. Regression test watched failing FIRST, then fixed.
- Plain English commit messages, no abbreviations, ASCII only, NO attribution anywhere.
- Blocker F02 (deleted-remote-branch merge proof): adopt the policy split (signal valid for clean
  worktree removal, NOT sufficient for branch deletion) unless verification shows it wrong -> then
  ask owner ONE question.
- Before landing: Codex inspection of full fix diff, inline stdin, iterate to PASS.
- All three suites green on final tree (Core, Avalonia, Gateway ~16 min). Never two suites at once.
- Then squash-merge to origin/main, delete branch, remove worktree, message owner outcome.
- Do NOT message owner with progress. Only final outcome or a genuinely undecidable question.

## Findings ledger (status: TODO / VERIFYING / REJECTED / FIXED / COMMITTED)

| ID  | Sev     | Area                    | File(s)                                                  | Status |
|-----|---------|-------------------------|----------------------------------------------------------|--------|
| F01 | MAJOR   | PR CLI pipe deadlock    | Core/Git/PullRequestService.cs; Core/Utilities/ProcessRunner.cs | COMMITTED 4b20cc62 |
| F02 | BLOCKER | remote-gone = merge     | Core/Git/GitBranchService, WorktreeSafetyEvaluator, WorktreeInventoryService; Avalonia/RepositoryDetailView | COMMITTED f00705d9 |
| F03 | BLOCKER | worktree remove -> rmdir| Core/Git/WorktreeReaperService.cs                        | COMMITTED f2507264 |
| F04 | MAJOR   | watcher blind + no recov| Core/Git/RepositoryWatcher.cs; Avalonia/App.axaml.cs     | COMMITTED 18e32466 |
| F05 | BLOCKER | reap fails open         | Avalonia/WorktreesView; Core/Git/WorktreeReaperService   | COMMITTED 90ead86a |
| F06 | MAJOR   | probe fail = clean      | Core/Git/GitStatusProvider, RepositoryStatusService      | COMMITTED 27b042e8 |
| F07 | MAJOR   | history non-atomic write| Gateway/Streaming/RepoHistoryStore.cs                    | COMMITTED 243324fd |
| F08 | MINOR   | snapshot no reconcile   | Gateway/Streaming/RepoHistoryStore.cs, DirectorHub       | COMMITTED 80c70f7c |
| F09 | MAJOR   | Hello reclaims ownership| Gateway/Streaming/PushedRepositoryStore                  | COMMITTED af3edd7b |
| F10 | MAJOR   | history unbounded rewrite| Gateway/Streaming/RepoHistoryStore.cs                    | COMMITTED e2c921e1 |
| F11 | MAJOR   | scan no cancel/kill     | Core/Git/RepositoryStatusService, GitSyncStatusProvider, GitStatusProvider, GitCommandRunner | TODO |
| F12 | MINOR   | confirm not bound       | Avalonia/WorktreesView; Core/Git/WorktreeReaperService   | COMMITTED 69298750 |

## Progress log
- ALL 12 findings COMMITTED. Gateway findings F07/F08/F09/F10 build-verified (dotnet build green)
  but their xUnit tests NOT yet RUN due to another session hogging the Gateway suite (vstest hangs
  under concurrency). MUST before merge: run full Gateway.Tests suite green + revert-proof each
  Gateway finding, once the other worktree testhost clears. Core + Avalonia suites already green.
- NEXT GATES: (1) Gateway suite green + Gateway revert-proofs; (2) full three-suite green on final tree;
  (3) Codex inspection of full diff, iterate to PASS; (4) squash-merge, delete branch, remove worktree, email owner.
- F12, F04 COMMITTED. Now on Gateway cluster (F07/F08/F09/F10). NOTE: another session runs the
  full Gateway suite in devthrottle-activity-ledger; Gateway test runs contend/hang per brief.
  Plan: implement all Gateway fixes+tests, verify with as few Gateway test runs as possible.
  F09 (PushedRepositoryStore epoch fix) + F07 (RepoHistoryStore atomic write + load recovery) code done.
- F01/F06/F11 COMMITTED. Shared ProcessRunner (concurrent drain + kill-on-cancel) introduced in
  F01 and reused by F11 git providers. F06 GetCountAsync now returns success flag; status service
  fails closed on any failed probe. All watched failing via temporary reverts. Core git suites green.
- Remaining: F12 (reap confirm binding), F04 (watcher), F07/F08/F10 (RepoHistoryStore), F09 (DirectorHub Hello).

- 2026-07-24: worktree cut, review committed, state file created. Starting verification.
- F02 COMMITTED f00705d9. BranchSafetyEvaluator drops origin-branch-gone as sufficient for branch
  deletion (requires PR merged or contained-in-main). Worktree side unchanged (branch ref survives).
  Tests watched failing first; full branch+worktree evaluator suite green (41).
- Policy split VERIFIED sound, adopted, no owner question needed: git worktree remove preserves the
  branch ref, so origin-gone stays safe for worktree reaping; branch deletion makes commits
  unreachable, so it now requires positive merge proof.
- Decisions for the reaper cluster (F03/F05/F12), to implement next:
  - F03: RemoveOneAsync must capture the git worktree remove result and RE-VERIFY cleanliness right
    before any physical delete; a safety refusal (became dirty) fails closed, never force-deletes.
  - F05: reaper must fail closed when the live-session roster is unknown. WorktreesView.LiveSessionsProvider
    is reap-only (separate from RepositoryMonitor.LiveSessionsProvider) - wire it to a NEW authoritative
    MainWindow provider that throws on fleet failure (union of local + fleet), leave the monitor on the
    best-effort provider. ReapAsync takes the roster/provider, fetches it AFTER fetch --prune, feeds it
    into the inventory recompute (not null), and aborts if it cannot be determined.
  - F12: bind the reap to the approved worktree set shown at confirmation (ReapAsync gains approvedPaths).

## Verification status (2026-07-24, late)
- All 12 findings COMMITTED to fix/repositories-cleanup-safety-516 (see ledger). Working tree CLEAN.
- Non-Gateway findings (F01,F02,F03,F04,F05,F06,F11,F12): full test-first + revert-proof done, green.
- Gateway findings: F07,F09 revert-proofed (bit). F08,F10 tests PASS (in 31-test green run) but the
  watch-on-revert demo is blocked by a concurrent Gateway suite in devthrottle-activity-ledger
  (vstest hangs when two Gateway suites overlap). Retry F08/F10 revert-proof when that clears.
- IN FLIGHT (background): Codex adversarial inspection of full src diff (bwomcgu3i); full Gateway
  suite queued to run when the other worktree's testhost clears (bbw3fbdyw).
- REMAINING GATES before merge: Codex PASS (iterate if FAIL); full Gateway suite green; full Core +
  Avalonia suites green on final tree (run sequentially, never overlapping a Gateway suite); then
  squash-merge to origin/main, delete branch, remove worktree, email owner (soren@centerconsulting.com).
- All caller sites of changed public APIs verified (ReapAsync, ObserveSnapshot, GetCount/GetStatus/
  GetSyncStatus/FetchAsync) - only the intended callers, all compile (Avalonia + Gateway build green).

## Codex inspection round 1 = FAIL -> fixed 9 findings (2026-07-24)
Codex (gpt-5.6-sol) adversarial pass found 9 real defects; all fixed as inspection-follow-up commits:
- Reaper (e02677df): #1 locked-worktree force-delete (respect git refusal; never delete a registered
  worktree), #2 roster TOCTOU (re-read roster right before the destructive loop), #3 failed-fetch abort.
- Branch (a5025440): #4 fetch fresh + abort-on-failure before proving containment in DeleteIfSafeAsync.
- Monitor/Watcher (5337434a): #7 monitor never publishes Success=false over a good row; #8 watcher
  invalidates the status cache before recompute.
- History (e91b2c7d): #5 empty/provisional push never erases (reconcile only on a real observation),
  #6 rows stamped + reconciliation scoped by the BOUND Director (payload DirectorId ignored),
  #9 suppressed save failure retried (pending flag; Save returns bool).
All new fixes have regression tests; Core-side ones revert-proofed and green. Gateway-side tests
build green; RUN pending a clear window (heavy Gateway-suite contention from other worktrees).
- Re-running Codex on the updated diff (brescn4ph). Full Core+Avalonia suites (bt4c1ufal). Patient
  Gateway verification (ba8on8jex). Iterate Codex to PASS, then three-suite green, then merge.
