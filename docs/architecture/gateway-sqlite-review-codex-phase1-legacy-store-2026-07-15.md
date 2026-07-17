# Codex Review - Phase 1 Legacy Store Reader

Date: 2026-07-15
Reviewer: Codex
Scope: `src/CcDirector.Gateway/Stats/GatewayInputStatsLegacyStore.cs`, with spot checks against `GatewayInputStatsAggregator.Load`, `GatewayStatsDatabase`'s `agents_seeded` home, and the Phase 1 mission text.

## Verdict

APPROVED.

## Findings

No blocking findings.

## Review Notes

The legacy reader's `Load` path matches the current JSON aggregator's post-deserialization semantics for the sections that matter to import parity:

- `Totals`, `HighWater`, `Hourly`, `WingmanTurns`, `WingmanSessions`, `Repos`, `Agents`, `AgentsSinceUtc`, `AgentDrivenTurns`, `AgentDrivenCharacters`, and `AgentDrivenHighWater` are read into the same in-memory shapes.
- The issue #1633 branch is preserved: when `AgentsSeeded` is absent/null, the already-read `_agents` tally is cleared; when `AgentsSeeded` is present, the stored tally is retained and the seed set is populated.
- The deliberate quarantine deviation is correctly constrained to unreadable JSON/null documents: the aggregator's `Quarantine(...)` calls are replaced with loud `InvalidOperationException`s that leave the legacy JSON in place. There is no `Quarantine`, `File.Move`, `File.Replace`, or `Save` path in `GatewayInputStatsLegacyStore`.

On the Architect's `AgentsSeeded` schema question: I do not see a post-`Load` consumer that needs the raw absent-versus-empty key. The distinction is consumed inside `Load` itself:

- Raw `AgentsSeeded: null` resolves to `_agents.Clear()` plus an empty `_agentsSeeded`.
- Raw `AgentsSeeded: []` resolves to the stored `_agents` tally being kept plus an empty `_agentsSeeded`.

Those outcomes remain distinguishable through the resolved `Agents`/`AgentsSeeded` state that the importer will consume, without encoding raw null-vs-empty in SQLite. The existing `agents_seeded(session_id TEXT PRIMARY KEY)` membership table is therefore sufficient for the post-`Load` state, assuming the importer continues to use `GatewayInputStatsLegacyStore` rather than reparsing the JSON document directly.

## Verification

- Inspected `GatewayInputStatsAggregator.Load` lines 605-689 and `GatewayInputStatsLegacyStore.Load` lines 96-180.
- Confirmed `GatewayInputStatsAggregator.Save` writes `AgentsSeeded` unconditionally, so raw null is a one-time legacy upgrade state.
- Confirmed no quarantine/write path remains in `GatewayInputStatsLegacyStore` with `rg -n "Quarantine|File\.Move|File\.Replace|Save\(|Unreadable|corrupt"`.
- Ran `dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj --no-restore`: passed.
- Attempted `dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --no-restore`; the command timed out after 124 seconds, so no full test result is available from this review pass.
