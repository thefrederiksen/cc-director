# Codex Review Round 3: Gateway SQLite Mission Brief

Reviewed 2026-07-15 by the Codex reviewer session for the mission "SQLite on the Gateway".

Reviewed revision: `c5cb174533efc76c8bb62f6dbd392cfb8c12c9f8`.

Verdict: APPROVED.

## Findings

1. APPROVAL POINT: The wingman-turn defect is fixed.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:209`

   Revised design: `stat_delta` now carries `wingman`:

   ```
   stat_delta(hour_utc, session_id, modality, surface, repo, agent, wingman, turns, chars)
   ```

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:212-223`

   Revised design: `wingman` records `SessionDto.VoiceMode` at fold time, not `modality = 'voice'`; wingman turns are `SUM(turns) WHERE wingman = 1`.

   Code check: `GatewayInputStatsAggregator.cs:425-427` increments `_wingmanTurns` for the session's entire turn delta when `s.VoiceMode` is true. The session delta is accumulated at `GatewayInputStatsAggregator.cs:342-382`, across all changed input buckets. This means a typed turn while voice mode is on is a wingman turn today.

   Review result: approved. The revised schema can preserve the current semantics.

2. APPROVAL POINT: The archive design now preserves wingman attribution.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:272-280`

   Revised design: archive rows preserve modality, surface, repository, agent, and the `wingman` flag. Pruning collapses only hour and session identifier.

   Review result: approved. This keeps all-time groupings stable while bounding detailed rows.

3. APPROVAL POINT: The archive-marker query rule is explicit and correct.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:282-295`

   Revised design: all-time aggregates include archive rows; hourly and working-day queries filter to real hour keys and exclude the archive marker.

   Code check: `GatewayInputStatsAggregator.cs:185-203` returns an ordered hour series today. `GatewayInputStatsAggregator.cs:310-315` prunes stale hourly buckets rather than folding them into a catch-all bucket.

   Review result: approved. This prevents the phantom archive bucket problem.

4. APPROVAL POINT: The write-cost claim is now accurate.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:232-237`

   Revised design: a correct post-cutover fold is one insert into `stat_delta`, one upsert into `session_highwater`, and possibly one `INSERT OR IGNORE` per distinct-id table. The claim is bounded work independent of history length, not "one row write".

   Code check: `GatewayInputStatsAggregator.cs:336-354` requires high-water comparison, and `GatewayInputStatsAggregator.cs:422` updates high-water state. The round-two objection is resolved.

   Review result: approved.

5. APPROVAL POINT: Acceptance criterion 3 now tests the real performance claim.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:363-371`

   Revised acceptance: the integration test asserts the real statement mix, asserts no JSON store timestamp changes after import, and pins that the mix is unchanged when historical row volume is large.

   Review result: approved. This is falsifiable and aligned with the schema.

6. APPROVAL POINT: Acceptance criterion 7 now pins both prune-safety legs.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:381-385`

   Revised acceptance: tests must prove all-time answers, including wingman turns and per-repository and per-agent tallies, are identical before and after prune; tests must also prove the working-day series has no phantom archive bucket.

   Review result: approved.

7. APPROVAL POINT: Acceptance criterion 8 pins the subtle wingman case.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:386-391`

   Revised acceptance: a typed turn while voice mode is on must count as a wingman turn, matching `GatewayInputStatsAggregator.cs:425-427`.

   Review result: approved. This is the right regression test for the schema change.

8. APPROVAL POINT: The baseline import design remains sound.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:146-196`

   Revised design: historical input data imports as baseline projections, not synthetic rows, because the existing JSON cannot reconstruct the full row tuple.

   Code check: `GatewayInputStatsAggregator.cs:448-457` persists independent projections, and `GatewayInputStatsAggregator.cs:460-481` defines the projection shapes. The data needed to reconstruct historical cross-dimensional rows does not exist on disk.

   Review result: approved.

9. APPROVAL POINT: The fail-loud import sequence remains sound.

   Brief citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:301-314`

   Revised design: import is transactional, stamps the import marker and `PRAGMA user_version`, compares every affected endpoint field back out of SQLite before renaming JSON aside, and fails loudly on mismatch.

   Review result: approved.

10. APPROVAL POINT: The scope, phase boundaries, and constraints remain correct.

    Brief citations: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:134-143`, `:324-349`, `:393-411`

    Review result: approved. Append-only logs remain out of scope, unrelated operational stores remain out of scope, Phase 1 proves only the input-store design and migration mechanism, and reviews are required before commits.

Final verdict: APPROVED.
