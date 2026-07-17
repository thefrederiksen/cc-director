# Codex Review: Phase 1 Step 3 Revision 7

Reviewed 2026-07-15 by the Codex reviewer session for the mission "SQLite on the Gateway".

Reviewed changes:

- `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs`
- `src/CcDirector.Gateway.Tests/GatewayStatsDatabaseTests.cs`
- `src/CcDirector.Gateway/CcDirector.Gateway.csproj`

Repository state observed during review: `7cf9f67152d5e2815d2f21192d4bc557f1e09ecd`.

Verdict: CHANGES REQUIRED.

## Findings

1. DEFECT: The new shape test does not actually pin "no repository or agent string in any form".

   Code citation: `src/CcDirector.Gateway.Tests/GatewayStatsDatabaseTests.cs:134-168`

   Test claim: `StatDelta_CarriesSurrogateIds_AndNoRepositoryOrAgentStringInAnyForm` pins that `stat_delta` cannot hold repository or agent strings.

   What the test actually checks: it asserts that `repo_id` and `agent_id` are integers, that there is no exact column named `repo`, no exact column named `agent`, and no exact columns named `repo_folded` or `agent_folded`.

   Why this is not enough: `Assert.DoesNotContain("repo", columns.Keys.Where(k => k != "repo_id"))` checks for an exact element named `repo`; it does not reject `repo_raw`, `repo_text`, `repository`, `repo_display`, or any other repository string column. Same issue for `agent`. The schema currently has the right shape, but the test does not enforce the invariant it says it enforces.

   Required fix: make the assertion reject any non-identifier column whose name contains `repo`, `repository`, or `agent`, or otherwise explicitly whitelist the full expected `stat_delta` column set. A whitelist is preferable here because the schema shape is the thing being pinned:

   ```
   id, hour_utc, session_id, modality, surface, is_voice, repo_id, agent_id, wingman, turns, chars
   ```

2. APPROVAL POINT: The surrogate-id schema fixes the comparer problem.

   Code citation: `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs:157-170`, `:205-218`, `:233-258`, `:249-317`

   Review result: approved. `stat_delta`, `baseline_repo`, `baseline_agent`, `repo_session`, and `agent_session` now key by integer identifiers. `repo_identity` and `agent_identity` hold display strings but deliberately do not enforce uniqueness or grouping over those strings. That removes the impossible requirement to normalize strings exactly like `StringComparer.OrdinalIgnoreCase`.

3. ANSWER TO MANAGER QUESTION: I see no remaining schema path that requires SQLite to compare repository or agent strings for correctness.

   The only repository and agent strings in this schema are `repo_identity.repo_display` and `agent_identity.agent_display`. They are not unique, not primary keys, not foreign keys, and not used by `stat_delta` or baseline grouping. SQLite can store and retrieve them as display bytes. Equality and membership must be decided by the in-memory `Dictionary<string, long>` using `StringComparer.OrdinalIgnoreCase`, as the brief now states.

   Guardrail for later implementation: do not introduce queries that `WHERE`, `GROUP BY`, `ORDER BY`, or `DISTINCT` over `repo_display` or `agent_display` for semantic decisions. Sorting for display should also stay in C# if it needs to match current .NET ordering.

4. APPROVAL POINT: Explicit transaction threading is correct.

   Code citation: `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs:119-125`, `:131-317`

   Review result: approved. `MigrateToVersion1` now accepts `SqliteTransaction tx`, and every schema statement is explicitly executed with that transaction. This makes the migration atomicity visible at each call site.

5. APPROVAL POINT: The schema still preserves the prior approved columns.

   Code citation: `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs:157-170`

   Review result: approved. `is_voice` and `wingman` are still present. Historical data is still represented by baseline tables, not synthetic `stat_delta` rows. Archive marker handling remains documented at `GatewayStatsDatabase.cs:40-47`.

6. APPROVAL POINT: Focused tests pass.

   Verification run:

   ```
   dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --filter GatewayStatsDatabaseTests --no-restore
   ```

   Result: 9 passed.

Final verdict: CHANGES REQUIRED.
