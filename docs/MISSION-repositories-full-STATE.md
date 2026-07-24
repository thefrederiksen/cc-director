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

## Done (continued)
4. Phase C: Gateway.Contracts RepoStatusDto/WorktreeDto/FleetWorktreeDto; RepositoryDtoMapper
   (ControlApi, folds state strings); GatewayStreamClient repoSnapshot + PushRepoSnapshot (own
   try/catch, old-Gateway safe); ControlApiHost monitor ctor param + WireRepositoryPush (3s
   debounce) + SnapshotRepositories; GatewayClient ListFleetRepositories/WorktreesAsync;
   ControlEndpoints /fleet/repositories + /fleet/worktrees (relay + standalone monitor fallback);
   Gateway PushedRepositoryStore + DirectorHub.PushRepoSnapshot + GET /repositories + /worktrees
   (tenant-scoped via ResolveReadTenant, streamStaleResolved); GatewayHost singletons + Map arg;
   CLI repo_ops.py + cli.py repo/worktree list. All committed through db9f65d9.
5. Phase D (partial): RecommendationEngine (Core, pure, 6 tests green) + Recommendations rail page
   in RepositoriesView (badge, cards, Show me -> detail, Hand to an agent).
6. Live QA loop against slot-5 Director (PID 78204, port 7880): all 8 harness checks PASS
   (scratchpad qa-mission-live.txt). Three live defects found and fixed during that loop:
   /fleet 502 on old Gateway (404 -> local fallback), SPA-fallback route-absent detection
   (200 text/html), CLI stringified-value bug (typed _raw/_int reads).
7. Full Gateway suite green: 3662 passed, 0 failed, 17 skipped.
8. INSPECTION ROUND 1 (Codex, independent): verdict BLOCK - 2 blockers, 8 majors, 2 minors,
   1 untested-claims note. Findings + Architect rulings: docs/MISSION-repositories-full-
   INSPECTION-1.md. A Manager is fixing all findings per ruling, one commit per finding,
   tests included, on this branch.

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
- Manager finishes inspection-round-1 fixes (F1-F14) and pushes.
- Architect: final three-suite pass on the fixed branch; rebuild slot 5 and smoke the changed UI
  (discard confirmation, provisional click block); second Codex inspection over the fix diff;
  QA report artifact; notify owner; WAIT for approval before anything lands on origin/main.
