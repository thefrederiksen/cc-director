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

## Inspection loop
- Round 1 (BLOCK): 14 findings; all fixed by Manager round 1, pushed through 53f4f97a; three
  suites green (Core 3423 / Avalonia 289 / Gateway 3672, 0 failed). Slot 5 rebuilt on the fixed
  branch; live harness 8/8 PASS (port 7880).
- Round 2 (BLOCK): fixes re-inspected; 6 closed clean, 1 REGRESSION (update-ref delete bypasses
  checked-out protection), 3 new majors (cancelled-scan late publish; /repositories serve-time
  fold missing for old Directors; repeated Hello resets sequence), rest partial. Rulings:
  docs/MISSION-repositories-full-INSPECTION-2.md. All fixed by Manager round 2, pushed through
  c9b429a2; suites green (Core 3433 / Avalonia 290 / Gateway 3678, 0 failed).
- LIVE CATCH between rounds: first live run of the round-2 build exposed the R2-8 wiring as
  non-structural (provider wired in MainWindow_Loaded; ShowMainWindow's scan fires first,
  throws, dies unobserved; model provisional forever). Architect fixed in e3b9d01a
  (constructor wiring + observed task); slot 5 rebuilt; live harness 8/8 with verified states.
  The round-3 inspection found the SAME defect independently - two mechanisms converged.
- Round 3 (BLOCK): 8 of the round-2 items CLOSED. Remaining: branch-restore compensation
  unsafe (create-only restore needed; cancellation can bypass compensation) and five monitor
  stamp/lifecycle interleavings. Rulings: docs/MISSION-repositories-full-INSPECTION-3.md.
  All fixed/verified by Manager round 3, pushed through efdf9baf; suites green (Core 3441 /
  Avalonia 290 / Gateway 3680, 0 failed). Slot 5 rebuilt at efdf9baf; live harness 8/8.
- Round 4 (BLOCK): create-only restore, startup wiring, tombstone basics CLOSED. Remaining
  fixed by Manager round 4 through ef85357f (suites: Core 3448 / Avalonia 290 / Gateway 3680,
  0 failed). Rulings + outcomes: docs/MISSION-repositories-full-INSPECTION-4.md.
- Round 5 (BLOCK): four of five areas CLOSED. Three findings in the round-4 fix code (delete
  command outside the recovery boundary; recovery-path logging able to defeat the restore;
  throwing progress subscriber skips the drain) - fixed directly by the Architect in
  804eb3ee + e4fc3870, tests watched failing first, Core 3450 / Avalonia 290 green.
  Record: docs/MISSION-repositories-full-INSPECTION-5.md. Round-6 inspection in progress;
  final Gateway suite rerun in progress; slot 5 live at ef85357f with harness 8/8 (needs one
  final rebuild at the tip once round 6 passes).

## Next
- Manager round 2 finishes R2-1..R2-12 and pushes.
- Architect: three-suite verification; slot-5 rebuild + live harness re-run; THIRD inspection
  pass over the round-2 diff; QA report artifact; notify owner; WAIT for approval before
  anything lands on origin/main.
