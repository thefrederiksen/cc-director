# Codex Review Round 2: Gateway SQLite Mission Brief

Reviewed 2026-07-15 by the Codex reviewer session for the mission "SQLite on the Gateway".

Reviewed revision: `797707d7173058d5a0d8720569515725e0e7c69a`.

Verdict: CHANGES REQUIRED.

## Findings

1. DEFECT: The revised `stat_delta` schema still does not preserve wingman-turn semantics.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:208`

   Brief claim: post-cutover rows have this shape:

   ```
   stat_delta(hour_utc, session_id, modality, surface, repo, agent, turns, chars)
   ```

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:247-250`

   Brief claim: archive rows preserve all grouping dimensions, "plus wingman-turn attribution for as long as that stays derived from rows"; concretely, an archive row is a `stat_delta` row with only `hour_utc` and `session_id` replaced by archive markers.

   What the code actually says: wingman turns are not the same as `modality = voice`. `GatewayInputStatsAggregator.cs:425-427` increments `_wingmanTurns` for every submitted turn folded while `s.VoiceMode` is true. The turns being folded are accumulated at `GatewayInputStatsAggregator.cs:342-382` from all input buckets, and those buckets only know modality and surface. Therefore a typed turn submitted while a session has voice mode on counts as a wingman turn today.

   Why this matters: the proposed row does not carry `s.VoiceMode` at observation time, a wingman flag, or a wingman-turn count. Once only rows exist, the system cannot derive post-cutover wingman turns exactly. Once old rows are archived with `session_id` replaced, it also cannot recover wingman attribution from any live session table.

   Required fix: add explicit wingman-turn attribution to the durable design. Acceptable shapes include a `wingman_turns` column on `stat_delta`, a boolean `wingman` flag on each delta row, or a separate wingman delta table. The archive design must preserve that field. The baseline import already calls out baseline wingman turns; the future rows need the same care.

2. DEFECT: The "one row write" claim is no longer true under the revised design.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:63`

   Brief claim: a counter move becomes one row write instead of a rewrite of the file.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:332-335`

   Brief claim: acceptance criterion 3 demonstrates this by observing a single delta and asserting the exact count of `INSERT` and `UPDATE` statements.

   What the code actually requires: the current implementation persists both the delta totals and the high-water state so repeated roster reads and restarts do not double-count. `GatewayInputStatsAggregator.cs:336-354` compares each reported bucket to high-water state, and `GatewayInputStatsAggregator.cs:422` updates that high-water state. The revised brief correctly moves this to `session_highwater` at `docs/architecture/gateway-sqlite-mission-2026-07-15.md:233-238`. The revised brief also correctly adds never-pruned distinct-id tables at `docs/architecture/gateway-sqlite-mission-2026-07-15.md:224-229`.

   Why this matters: a correct post-cutover fold normally needs at least one insert into `stat_delta` and one upsert into `session_highwater`. It may also need `INSERT OR IGNORE` statements into distinct-id tables for repository, agent, or wingman-session identity. That is still a major improvement over rewriting the whole JSON document, but it is not "one row write".

   Required fix: change the design and acceptance wording to "one delta row plus bounded operational bookkeeping" or similar. Acceptance criterion 3 should assert the expected statement mix for the real schema, not force the implementation into an impossible one-write promise.

3. DEFECT: The archive marker needs an explicit query rule so archived rows do not pollute the working-day series.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:214`

   Brief claim: the working-day series is baseline hourly buckets plus `GROUP BY hour_utc` for recent hours.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:248-250`

   Brief claim: pruning collapses the hour and stores archive rows with `hour_utc` replaced by an archive marker.

   What the code actually says today: `GatewayInputStatsAggregator.cs:185-203` returns hourly buckets as an hour series, and `GatewayInputStatsAggregator.cs:310-315` prunes old hourly buckets rather than turning them into an all-time bucket.

   Why this matters: if archive rows live in `stat_delta`, a plain `GROUP BY hour_utc` can produce a fake archive bucket unless every hourly query explicitly excludes archive markers. That is probably intended, but the brief should say it because criterion 7 depends on pruning being provably safe.

   Required fix: state that all time aggregate queries include archive rows, but hourly or working-day queries filter to real hour keys and exclude archive markers.

4. APPROVAL POINT: The baseline import design is now the right answer.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:146-186`

   Brief claim: historical input data imports as baseline projections, not synthetic `stat_delta` rows, because the existing JSON cannot reconstruct the full row tuple.

   What the code actually says: `GatewayInputStatsAggregator.cs:448-457` stores independent projections: totals, high-water, hourly, wingman turns, wingman sessions, repository tallies, agent tallies, and `AgentsSinceUtc`. `GatewayInputStatsAggregator.cs:460-481` defines the projection shapes. Those persisted shapes cannot recover which hour went with which repository and agent for historical turns.

   Review result: approved. This directly fixes the main round one defect. The `_agentsSinceUtc` precedent cited at `GatewayInputStatsAggregator.cs:63-66` is the right model: be honest about what starts at cutover instead of inventing history.

5. APPROVAL POINT: The fail-loud import sequence is now strong enough.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:263-276`

   Brief claim: import happens in one transaction with an import marker and `PRAGMA user_version`; every affected endpoint field is read back from SQLite and compared to JSON output; the JSON file is renamed aside only after a complete match; mismatches fail loudly and leave JSON as source of truth.

   Review result: approved. This fixes the round one data-loss risk. It is specific enough for implementation and test review.

6. APPROVAL POINT: Distinct counts are now modeled correctly.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:224-229`

   Brief claim: all-time distinct counts do not come from `COUNT(DISTINCT session_id)` over `stat_delta`; they use narrow never-pruned identifier tables seeded from the existing lists and extended with `INSERT OR IGNORE`.

   What the code actually says: `GatewayInputStatsAggregator.cs:45-49`, `GatewayInputStatsAggregator.cs:51-55`, and `GatewayInputStatsAggregator.cs:57-61` define all-time session sets for wingman, repositories, and agents. The revised design preserves that semantic.

   Review result: approved.

7. APPROVAL POINT: The citation fixes from round one are correct.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:36-45`

   Brief claim: the concurrency load path is `GatewaySessionConcurrencyStats.cs:205-227`, quarantine is `:249-254`, and silent default-filling is a separate worse failure mode.

   What the code actually says: `GatewaySessionConcurrencyStats.cs:205-227` starts empty on missing, null, or JSON parse failure; `GatewaySessionConcurrencyStats.cs:249-254` renames corrupt files. The claim now resolves.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:100-108`

   Brief claim: `voice-sessions.json` is a flat string array loaded at `WingmanVoiceService.cs:113-122` and saved with direct `File.WriteAllText` at `:125-128`, with no atomic rename and no quarantine.

   What the code actually says: `WingmanVoiceService.cs:113-128` matches that description. The claim now resolves.

8. APPROVAL POINT: The narrowed scope line is correct.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:134-143`

   Brief claim: a store is in scope only if it is one of the named statistics, diagnostics, scheduler-history, or Car Mode telemetry files and it rewrites a whole document for incremental changes.

   Review result: approved. This fixes the overbroad round one scope line and keeps unrelated operational JSON stores out of the mission.

9. APPROVAL POINT: Phase 1 is properly bounded now.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:288-316`

   Brief claim: Phase 1 proves the input-store design, baseline import discipline, and migration mechanism, but does not prove concurrency-peak restoration or Phase 4 store choices.

   Review result: approved.

10. APPROVAL POINT: The revised acceptance criteria are mostly falsifiable.

    Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:321-345`

    Brief claim: acceptance requires captured `/stats/data` before and after, screenshots for all three surfaces, a statement-count test, all seven solution test projects, import refusal tests, and prune-safety tests.

    Review result: approved with the exception of finding 2. The statement-count test is a good idea, but the expected count must match the real multi-table design rather than the obsolete "one row write" claim.

Final verdict: CHANGES REQUIRED.
