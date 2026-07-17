# Codex Review: Phase 1 Step 3 Database Helper And Schema

Reviewed 2026-07-15 by the Codex reviewer session for the mission "SQLite on the Gateway".

Reviewed changes:

- `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs`
- `src/CcDirector.Gateway.Tests/GatewayStatsDatabaseTests.cs`
- `src/CcDirector.Gateway/CcDirector.Gateway.csproj`

Repository state observed during review: `6327fcd7eb781297b0fcf921495d7a19cae812a7`.

Verdict: APPROVED.

## Findings

1. APPROVAL POINT: The schema matches the approved Phase 1 shape.

   Code citation: `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs:157-170`

   Review result: approved. `stat_delta` includes `hour_utc`, `session_id`, `modality`, `surface`, `is_voice`, `repo_folded`, `agent_folded`, `wingman`, `turns`, and `chars`. This preserves the approved revision 6 requirements: no synthetic historical rows, explicit voice split, folded repository and agent grouping keys, and wingman attribution independent of modality.

2. APPROVAL POINT: `repo_folded` and `agent_folded` are protective names, not a deviation.

   Code citation: `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs:143-147`, `:164-166`, `:205-218`, `:233-258`

   Review result: approved. The approved brief used `repo` and `agent` as conceptual dimensions, but the Phase 1 plan correctly found that the persisted grouping keys must match `StringComparer.OrdinalIgnoreCase` semantics from `GatewayInputStatsAggregator.cs:55` and `:61`. Naming the stored keys `repo_folded` and `agent_folded` prevents a later reader from displaying folded keys as user-facing repository or agent names. The identity tables are the right place for first-seen display spelling.

3. APPROVAL POINT: `is_voice` is present and correctly documented.

   Code citation: `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs:149-151`, `:164`

   Review result: approved. This preserves the current asymmetry: totals by modality are case-sensitive, while the voice split used by hourly, repository, and agent tallies follows the case-insensitive check at `GatewayInputStatsAggregator.cs:366`. This avoids SQL collation drift.

4. APPROVAL POINT: `wingman` is present and correctly documented.

   Code citation: `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs:153-156`, `:167`

   Review result: approved. The code records that wingman means `SessionDto.VoiceMode` at fold time, not `modality = voice`. This preserves the behavior at `GatewayInputStatsAggregator.cs:425-427`, where the whole session turn delta counts as wingman turns when voice mode is on.

5. APPROVAL POINT: Baseline tables do not synthesize history.

   Code citation: `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs:133-141`, `:187-224`

   Review result: approved. Historical data has baseline projection tables, and post-cutover data has rows. This follows the approved design and does not attempt to reconstruct a historical cross-product that the JSON store never persisted.

6. APPROVAL POINT: The all-time distinct-id tables are modeled separately from `stat_delta`.

   Code citation: `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs:226-244`

   Review result: approved. `wingman_session`, `repo_session`, and `agent_session` preserve the never-pruned distinct set semantics documented at `GatewayInputStatsAggregator.cs:45-61`. They are not derived from `COUNT(DISTINCT session_id)` over prunable delta rows.

7. APPROVAL POINT: Archive marker rules are captured at the database layer.

   Code citation: `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs:40-47`

   Review result: approved. The helper exposes the marker and documents the required query rule: all-time aggregates include archive rows, while hourly and working-day queries exclude the marker.

8. APPROVAL POINT: The migration version guard is correct.

   Code citation: `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs:99-127`

   Review result: approved. Older files migrate forward; files from newer builds throw rather than being opened by an older binary. `GatewayStatsDatabaseTests.Open_FileFromNewerBuild_FailsLoudlyRatherThanDowngrading` pins the guard.

9. APPROVAL POINT: The migration transaction claim is acceptable.

   Code citation: `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs:117-125`

   Review result: approved. I specifically checked whether schema commands executed on the same open SQLite connection, without assigning `cmd.Transaction`, still roll back under the surrounding connection transaction. They do. A temporary probe created a table inside a transaction, rolled back, and the table count was zero. Passing the transaction into every schema helper call would make the intent more obvious, but this implementation is not a blocker.

10. APPROVAL POINT: Fail-loud open paths name the database and reject JSON fallback.

    Code citation: `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs:86-95`

    Review result: approved. Open failure logs and throws with the database path and says the Gateway will not fall back to old JSON stores. `GatewayStatsDatabaseTests.Open_UnusableFile_FailsLoudlyAndNamesThePath` pins this.

11. APPROVAL POINT: The Gateway package reference is correct.

    Code citation: `src/CcDirector.Gateway/CcDirector.Gateway.csproj`

    Review result: approved. `Microsoft.Data.Sqlite` is now an explicit Gateway dependency at version 9.0.2, matching Core. `dotnet restore src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj` completed without downgrade warnings.

12. APPROVAL POINT: Test coverage is appropriate for this foundation step.

    Code citation: `src/CcDirector.Gateway.Tests/GatewayStatsDatabaseTests.cs`

    Review result: approved. The tests cover new file creation, schema tables, reopen without data loss, newer-build rejection, write-ahead logging, archive marker parse safety, and fail-loud unusable-file behavior. Fold, import, parity, pruning, and statement-count tests correctly remain for later Phase 1 steps because this diff intentionally has no fold, import, or prune code yet.

## Verification

- `dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --filter GatewayStatsDatabaseTests --no-restore`
- `dotnet restore src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj`

Final verdict: APPROVED.
