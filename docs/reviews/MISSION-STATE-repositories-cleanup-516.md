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
| F01 | MAJOR   | PR CLI pipe deadlock    | Core/Git/PullRequestService.cs                           | TODO   |
| F02 | BLOCKER | remote-gone = merge     | Core/Git/GitBranchService, WorktreeSafetyEvaluator, WorktreeInventoryService; Avalonia/RepositoryDetailView | COMMITTED f00705d9 |
| F03 | BLOCKER | worktree remove -> rmdir| Core/Git/WorktreeReaperService.cs                        | TODO   |
| F04 | MAJOR   | watcher blind + no recov| Core/Git/RepositoryWatcher.cs; Avalonia/App.axaml.cs     | TODO   |
| F05 | BLOCKER | reap fails open         | Avalonia/WorktreesView; Core/Git/WorktreeReaperService   | TODO   |
| F06 | MAJOR   | probe fail = clean      | Core/Git/GitStatusProvider, RepositoryStatusService      | TODO   |
| F07 | MAJOR   | history non-atomic write| Gateway/Streaming/RepoHistoryStore.cs                    | TODO   |
| F08 | MINOR   | snapshot no reconcile   | Gateway/Streaming/RepoHistoryStore.cs                    | TODO   |
| F09 | MAJOR   | Hello reclaims ownership| Gateway/Streaming/DirectorHub, PushedRepositoryStore; ControlApi/GatewayStreamClient | TODO |
| F10 | MAJOR   | history unbounded rewrite| ControlApi/GatewayStreamClient; Gateway/DirectorHub, RepoHistoryStore | TODO |
| F11 | MAJOR   | scan no cancel/kill     | Core/Git/RepositoryStatusService, GitSyncStatusProvider, GitStatusProvider, GitCommandRunner | TODO |
| F12 | MINOR   | confirm not bound       | Avalonia/WorktreesView; Core/Git/WorktreeReaperService   | TODO   |

## Progress log
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
