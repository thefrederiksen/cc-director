# Codex Review - SQLite Fold Final

Date: 2026-07-15
Reviewer: Codex
Scope: blocking-only review of `fold-final.diff` and current worktree, focused on #1647 fold fidelity, aggregate isolation, and mirror/commit ordering.

## Verdict

APPROVED.

## Blocking Findings

None.

## Blocking Checks

1. #1647 fold fidelity holds.

- `FoldAgentDrivenLocked` is called before the empty-buckets guard, so sessions driven only by other agents are still counted.
- Agent back-fill reads `_highWater` before the loop records the new bucket high-water, and `agents_seeded` is persisted/reloaded to stop restart double-counting.
- `VoiceMode` is read once into `wingman` and written to `stat_delta.wingman`; wingman usage is not derived from modality.
- Human agent attribution and agent-driven attribution share agent identity/session membership but write separate delta lanes.

2. Human aggregates cannot accidentally include agent-driven rows.

- Human totals, hourly, wingman turns, and repo totals read from `stat_delta`.
- Human agent totals read from `agent_delta`.
- Agent-driven totals read only from `agent_driven_delta`.
- `agent_driven_delta` is a separate table, so the human queries cannot include it by forgetting a filter.

3. Archive rows are handled deliberately, not accidentally.

- All-time totals, repo totals, and wingman turns include archive rows by reading `stat_delta` without excluding `ARCHIVE`.
- Hourly working-day series excludes `ARCHIVE` explicitly.
- Agent and agent-driven tables are not pruned and have no archive lane.

4. The mirror is not advanced before commit.

- `FoldBatch` collects intended writes first.
- `CommitLocked` writes inside one SQLite transaction and calls `tx.Commit()` before updating `_agentsSinceUtc`, identity maps, high-water maps, `agents_seeded`, wingman sessions, and identity-session sets.
- If a statement fails before commit, transaction disposal rolls back and the mirror remains unchanged, so the next poll can retry instead of silently losing a delta.

## Non-Blocking Note

`FoldAgentDrivenLocked` queues an `agent_driven_highwater` upsert for repeated nonzero snapshots even when the delta is zero. That can cost an unnecessary write on an otherwise idle agent-driven poll, but it does not change totals, include the wrong lane, or advance the mirror before a commit. I would not hold the owner's stats landing on this.

## Verification

- Inspected `GatewayInputStatsAggregator.cs` fold, commit, query, prune, and mirror-load paths.
- Inspected `GatewayStatsDatabase.cs` schema changes: `stat_delta` has no `agent_id`, `agent_delta` and `agent_driven_delta` are separate.
- Ran `dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --no-restore --filter "FullyQualifiedName~GatewayInputStatsAggregatorTests|FullyQualifiedName~GatewayStatsDatabaseTests|FullyQualifiedName~StatsPageEndpointTests"`: passed, 42 tests.
- Ran `dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj --no-restore`: passed.
