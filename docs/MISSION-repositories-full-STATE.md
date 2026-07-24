# Mission state - repositories-full

Branch: mission/repositories-full · Worktree: D:\ReposFred\devthrottle-repos-mission
Spec: devthrottle_internal#510 · Brief: docs/MISSION-repositories-full-2026-07-23.md

## Done (committed on the branch)
1. Phase A: model (worktree list+sizes+dirty-since+provisional), monitor enrich/recompute-one/
   find-for-path, RepositoryWatcher (git-signal scoped, debounced), one-brain unify (session tab
   reads monitor), verifying visual state. Commit "phase A".
2. Phase B services + C2 DATA-LOSS FIX (origin-gone requires upstream; regression test): DiffParser,
   GitDiffService, GitBranchService+BranchSafetyEvaluator, PullRequestService (gh/az parsers),
   GitHistoryService. Commit "phase B services".
3. Phase B UI + E: RepositoryDetailView (tabs: Changes diff viewer / Worktrees reuse / Branches /
   Pull requests / History), ChangesDiffView, list click-through, HandToAgentDialog +
   AgentBriefTemplates (hard rules + no-attribution), MainWindow spawn+stage-brief wiring.
   Commit "phase B UI + phase E hand-off".

## In progress (built, tests running, NOT yet committed)
4. Phase C: Gateway.Contracts RepoStatusDto/WorktreeDto/FleetWorktreeDto; RepositoryDtoMapper
   (ControlApi, folds state strings); GatewayStreamClient repoSnapshot + PushRepoSnapshot (own
   try/catch, old-Gateway safe); ControlApiHost monitor ctor param + WireRepositoryPush (3s
   debounce) + SnapshotRepositories; GatewayClient ListFleetRepositories/WorktreesAsync;
   ControlEndpoints /fleet/repositories + /fleet/worktrees (relay + standalone monitor fallback);
   Gateway PushedRepositoryStore + DirectorHub.PushRepoSnapshot + GET /repositories + /worktrees
   (tenant-scoped via ResolveReadTenant, streamStaleResolved); GatewayHost singletons + Map arg;
   CLI repo_ops.py + cli.py repo/worktree list.
   Gateway store tests written (PushedRepositoryStoreTests) - background run pending.
5. Phase D (partial): RecommendationEngine (Core, pure, 6 tests green) + Recommendations rail page
   in RepositoriesView (badge, cards, Show me -> detail, Hand to an agent).

## Also built (with C, awaiting the same test runs)
- App passes RepositoryMonitor into ControlApiHost (verified in source).
- D persistence v1 FILE-BACKED (Architect deviation from #510's "real tables", documented in the
  store remarks): Gateway RepoHistoryStore (JSONL at CcStorage.Root()/repo-history.jsonl), fed from
  accepted PushRepoSnapshot only, provisional rows excluded; WeeklyTrends (per-week peaks) +
  DirtyOverThreshold; GET /reports/repositories-weekly (tenant-scoped). Postgres = follow-up that
  changes persistence, not shape. Tests written (RepoHistoryStoreTests).

## Not built (honest gaps for the QA report)
- Postgres persistence for the history (file-backed v1 instead - see above).
- Worktree dwell-time events (appeared/reaped/reclaimed-bytes event log) - trends are snapshot-based.
- Fleet-remote reap routing (out of scope per brief ruling; Gateway API is read-only).
- Phase E scheduled cleanup agent (out of scope per brief ruling).
- Weekly report HTML section (the endpoint serves JSON; the report renderer integration is follow-up).

## Next
- Verify App passes monitor to ControlApiHost; run Gateway tests; commit C+D; full three suites;
  slot-5 build + real-machine QA harness (repo list/worktree list CLI against live Director);
  Codex inspection; QA report artifact; notify owner.
