# Codex Review: Gateway SQLite Mission Brief

Reviewed 2026-07-15 by the Codex reviewer session for the mission "SQLite on the Gateway".

Verdict: CHANGES REQUIRED.

## Numbered Findings

1. DEFECT: The brief contradicts itself on exact all-time distinct counts.

   Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:123`

   Brief claim: `stat_delta(hour_utc, session_id, modality, surface, repo, agent, turns, chars)` is the row schema.

   Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:132`

   Brief claim: distinct session counts become `COUNT(DISTINCT session_id)`, exact, with no persisted `HashSet`.

   Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:148-151`

   Brief claim: pruning folds departing rows into an archive row before deleting them, and distinct-session sets keep their own narrow unpruned table.

   What the code says today: exact distinct session counts are deliberately persisted as unpruned sets. `GatewayInputStatsAggregator.cs:45-49` stores all-time wingman session ids, `GatewayInputStatsAggregator.cs:51-55` stores all-time per-repository session sets, and `GatewayInputStatsAggregator.cs:57-61` stores all-time per-agent session sets. `GatewayInputStatsAggregator.cs:562-577` writes those sets to JSON.

   Required fix: The brief must define the durable distinct-count schema explicitly. `COUNT(DISTINCT session_id)` over `stat_delta` is exact only while all relevant detail rows remain. It stops being exact after pruning unless the distinct ids are preserved elsewhere. The brief should stop implying that `stat_delta` alone replaces the all-time sets.

2. DEFECT: The archive-row pruning design is too vague to preserve all existing all-time answers.

   Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:145-151`

   Brief claim: pruning old delta rows is safe if departing rows are folded into an archive row before deletion.

   What the code says today: hourly data is the only pruned input data. `GatewayInputStatsAggregator.cs:310-315` removes stale hourly buckets only. All-time totals, wingman sessions, repository tallies, and agent tallies survive because they live in separate structures. The query surfaces read those structures through `CurrentTotals`, `WingmanUsage`, `RepoTotals`, and `AgentTotals`.

   Required fix: "An archive row" is not enough. If old detail rows are deleted, archived data must preserve all grouping dimensions needed for all-time sums: modality, surface, repository, agent, and wingman-turn attribution if that remains derived from rows. Exact distinct counts still need unpruned id tables.

3. DEFECT: The one-time JSON import cannot reconstruct true historical delta rows from the current aggregate JSON.

   Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:153-157`

   Brief claim: on first run, import the existing JSON store into the database and make all-time totals, peaks, and distinct counts read identically before and after.

   What the code says today: the input JSON contains aggregate sections, not historical per-event or per-delta rows. `GatewayInputStatsAggregator.cs:448-457` defines `StoreFile` as totals, high-water, hourly buckets, wingman turns, wingman sessions, repository tallies, agent tallies, and `AgentsSinceUtc`. `GatewayInputStatsAggregator.cs:507-533` loads each aggregate back into separate in-memory structures. There is no persisted row containing the full tuple `hour_utc, session_id, modality, surface, repo, agent, turns, chars`.

   Required fix: The brief must specify a lossless import contract for the existing aggregate document. If it creates synthetic rows, it must say which rows are synthetic, which historical cross-dimensional questions they cannot answer, and how before/after parity is proven for every existing endpoint field before the JSON file is renamed aside.

4. DEFECT: The import plan must protect the owner's real numbers before moving the JSON aside.

   Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:153-157`

   Brief claim: import existing JSON into SQLite, then rename JSON aside rather than deleting it.

   What the code says today: current load paths start empty when a JSON file is missing or quarantined. `GatewayInputStatsAggregator.cs:483-489` starts empty when the input store file is absent. `GatewaySessionConcurrencyStats.cs:205-211` starts empty when the concurrency store file is absent.

   Required fix: The brief should require an import transaction, a complete parity read from SQLite, and only then a JSON rename. On parity failure, the gateway should fail loudly or leave JSON as the source, not mark it imported. It should also define an import marker or version gate so a crash between database writes and JSON rename cannot double-import or strand partial data.

5. APPROVAL POINT: SQLite is the right answer for the hot rewritten statistics stores.

   Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:40-49`

   Brief claim: the justification is schema evolution, migration safety, and avoiding whole-document rewrites, not disk size.

   What the code says today: `GatewayInputStatsAggregator.cs:123` and `GatewayInputStatsAggregator.cs:137` call `Save()` after changed folds, and `GatewayInputStatsAggregator.cs:555-583` serializes and rewrites the whole document. `GatewayEndpoints.cs:813` folds input stats on every `GET /sessions` roster read, and `GatewayEndpoints.cs:820` folds concurrency on the same path. The premise is real; this is not database ceremony over a non-problem.

6. APPROVAL POINT: The scope line is directionally correct for append-only JSON Lines logs.

   Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:80-85`

   Brief claim: prompt and transcription JSON Lines logs stay out of scope.

   What the code says today: `GatewayPromptLog.cs:51-55` writes daily `conversation-yyyyMMdd.jsonl` files. `GatewayPromptLog.cs:26-27` explicitly says retention is unbounded and nothing prunes it. `TranscriptionTelemetryLog.cs:49-54` writes daily `transcription-yyyyMMdd.jsonl` files, and `TranscriptionTelemetryLog.cs:67-70` appends one line. These logs do not have the whole-document rewrite problem.

7. DEFECT: The scope line is overbroad if read literally.

   Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:96-97`

   Brief claim: a store moves to SQLite if and only if it rewrites its whole document to record an incremental change.

   What the code says today: many Gateway JSON stores use whole-document persistence but are not part of this statistics mission. For example, `WorkListStore`, `SnoozeRegistry`, `PushSubscriptionStore`, and other operational stores appear in the same repository with rewrite-style persistence.

   Required fix: Narrow the scope line to the listed Gateway statistics, diagnostics, scheduler history, car mode telemetry, and voice-session files, or say Phase 4 evaluates only the named remaining files.

8. DEFECT: `voice-sessions.json` is inaccurately described as one of the seven hand-rolled JSON stat stores with the same atomic and quarantine pattern.

   Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:60`

   Brief claim: there are seven hand-rolled JSON files, each with the same load, serialize, atomic temp-write-and-rename, and quarantine-on-corrupt code.

   Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:72`

   Brief claim: `voice-sessions.json` is one of those seven stores, described as "Session records".

   What the code says today: `WingmanVoiceService.cs:105` sets the file path to `voice-sessions.json`. `WingmanVoiceService.cs:113-122` loads it, catches every exception, logs, and does not quarantine. `WingmanVoiceService.cs:125-128` saves it with direct `File.WriteAllText` and catches every exception. It is not atomic temp-write-and-rename, not quarantine-on-corrupt, and not a statistics rollup like the other cited files.

   Required fix: Remove `voice-sessions.json` from the "same pattern" premise or describe it separately. If it remains in Phase 4, the brief should explain why a persisted set of voice session ids belongs in this SQLite statistics mission.

9. DEFECT: The concurrency quarantine citation is imprecise and partially points at unrelated save code.

   Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:31-35`

   Brief claim: a shape change is treated as corrupt, renamed to `.corrupt-{timestamp}`, and the store restarts empty, citing `GatewaySessionConcurrencyStats.cs:249-298`.

   What the code says today: `GatewaySessionConcurrencyStats.cs:205-227` is the load path that starts empty on missing, null, or JSON parse failure. `GatewaySessionConcurrencyStats.cs:249-254` is the quarantine method. `GatewaySessionConcurrencyStats.cs:256-298` is save code, not corrupt-load behavior. Also, a shape change is treated as corrupt only if deserialization fails or returns null; missing fields can deserialize as defaults.

   Required fix: Cite `GatewaySessionConcurrencyStats.cs:205-227` and `GatewaySessionConcurrencyStats.cs:249-254`, and qualify the shape-change statement.

10. APPROVAL POINT: The input-store dictionary citation is accurate.

    Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:21-25`

    Brief claim: `GatewayInputStatsAggregator` keeps separate dictionaries for totals, high-water, hourly, wingman sessions, repositories, and agents.

    What the code says today: `GatewayInputStatsAggregator.cs:31-66` contains `_totals`, `_highWater`, `_hourly`, `_wingmanSessions`, `_repos`, `_agents`, and `_agentsSinceUtc`. The claim resolves.

11. APPROVAL POINT: The whole-document rewrite citations for the input store are accurate.

    Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:36-39`

    Brief claim: the stats stores reserialize and rewrite the file whenever counters move.

    What the code says today: `GatewayInputStatsAggregator.cs:123` and `GatewayInputStatsAggregator.cs:137` call `Save()` when changed. `GatewayInputStatsAggregator.cs:555-583` builds the full `StoreFile`, serializes it, writes a temp file, and moves it over the original. The claim resolves for the input store.

12. APPROVAL POINT: The `GET /sessions` fold citations are accurate.

    Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:39`

    Brief claim: folding runs on every `GET /sessions` read.

    What the code says today: `GatewayEndpoints.cs:813` calls `inputStats?.ObserveSnapshot(all, DateTime.UtcNow)`, and `GatewayEndpoints.cs:820` calls `concurrency?.Observe(all, DateTime.UtcNow)`. The claim resolves.

13. APPROVAL POINT: The SQLite dependency and house-pattern citations are accurate.

    Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:51-58`

    Brief claim: `Microsoft.Data.Sqlite` is already referenced in Core, Gateway references Core, and the repository has a raw `SqliteConnection` plus `CREATE TABLE IF NOT EXISTS` pattern.

    What the code says today: `CcDirector.Core.csproj:18` references `Microsoft.Data.Sqlite` 9.0.2. `CcDirector.Gateway.csproj:49` references Core. `DatabaseService.cs:47-60` opens a raw `SqliteConnection` and begins `CREATE TABLE IF NOT EXISTS communications`. The claim resolves.

14. APPROVAL POINT: The storage-root citation is accurate.

    Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:64-65`

    Brief claim: these files resolve through `CcStorage.Root()` to `%LOCALAPPDATA%\cc-director`.

    What the code says today: `CcStorage.cs:28` returns `Base()`, and `CcStorage.cs:36-37` returns `%LOCALAPPDATA%\cc-director` when no override is set. The claim resolves.

15. APPROVAL POINT: The store-size table is consistent with the current machine.

    Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:66-72`

    Brief claim: the seven listed files total roughly 174 KB.

    What the local files say today: `gateway-input-stats.json` is 60,538 bytes, `carmode-telemetry.json` is 33,013 bytes, `netdiag-rollup.json` is 22,440 bytes, `netdiag-devices.json` is 20,459 bytes, `gateway-concurrency-stats.json` is 15,869 bytes, `cronruns.json` is 15,907 bytes, and `voice-sessions.json` is 13,261 bytes. The total is close to the brief's rounded 174 KB.

16. APPROVAL POINT: The Car Mode telemetry citations are accurate.

    Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:67`

    Brief claim: `CarModeTelemetryStore.cs:46` defaults to `carmode-telemetry.json`.

    What the code says today: `CarModeTelemetryStore.cs:45-47` sets the default path to `Path.Combine(CcStorage.Root(), "carmode-telemetry.json")`.

    Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:176-180`

    Brief claim: `RetentionDays = 90` and `MaxRecords = 10000` are intentional and should carry over.

    What the code says today: `CarModeTelemetryStore.cs:26-31` defines exactly those constants with the stated comments. The claim resolves.

17. APPROVAL POINT: The concurrency-store path citation is accurate.

    Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:70`

    Brief claim: `GatewaySessionConcurrencyStats.cs:69` defaults to `gateway-concurrency-stats.json`.

    What the code says today: `GatewaySessionConcurrencyStats.cs:68-70` sets the default path to `Path.Combine(CcStorage.Root(), "gateway-concurrency-stats.json")`. The claim resolves.

18. APPROVAL POINT: The all-time distinct-session set citations are accurate and must remain requirements.

    Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:91-94`

    Brief claim: `_repos[].Sessions`, `_agents[].Sessions`, and `_wingmanSessions` are deliberately never pruned and must be preserved.

    What the code says today: `GatewayInputStatsAggregator.cs:45-47`, `GatewayInputStatsAggregator.cs:51-54`, and `GatewayInputStatsAggregator.cs:57-61` state all-time and never-pruned semantics. The claim resolves.

19. APPROVAL POINT: The high-water design requirement is correct.

    Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:137-142`

    Brief claim: `FoldLocked` is the idempotent high-water fold and should move to `session_highwater` without semantic simplification.

    What the code says today: `GatewayInputStatsAggregator.cs:336-354` compares current reported counters against previous high-water counters, and `GatewayInputStatsAggregator.cs:350-354` handles reset by treating the current count as fresh activity. `GatewayInputStatsAggregator.cs:422` updates the high-water value. This logic is the core correctness path and should be preserved.

20. DEFECT: The row-not-counter design is sound for new observations but the "no schema change" claim is overbroad.

    Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:121-134`

    Brief claim: one narrow delta row lets the next owner question be a query rather than a deploy.

    What the code says today: new observations can supply session id, modality, surface, repository, and agent through `SessionDto` and `InputStats`. `GatewayInputStatsAggregator.cs:343-417` already folds deltas across those dimensions.

    Required fix: The brief should qualify that new questions are query-only when they use dimensions already captured in `stat_delta`. A new dimension still requires a schema change.

21. APPROVAL POINT: Phase 1 is the right proof target, with one caveat.

    Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:166-170`

    Brief claim: Phase 1 proves the database foundation by porting the largest, worst-behaved input store with numbers intact.

    What the code says today: the input store has the most complex aggregate shape and is folded on the frequent `/sessions` path. That makes it the right first proof. The caveat is that Phase 1 proves only the input-store design and import discipline, not concurrency-peak restoration or Phase 4 store choices.

22. DEFECT: Acceptance criterion 2 is not fully falsifiable as written.

    Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:192-193`

    Brief claim: `/stats`, cockpit Your Throttle, and mobile Your Throttle render unchanged.

    What the code says today: the surfaces exist. `StatsPageEndpoint.cs:74-79` maps `/stats`, `apps/cockpit/src/throttle/YourThrottleView.tsx` implements cockpit Your Throttle, and `apps/mobile/src/pages/YourThrottle.tsx` implements mobile Your Throttle.

    Required fix: Require concrete evidence, such as captured JSON plus screenshots or route smoke checks, so "render unchanged" is not subjective.

23. DEFECT: Acceptance criterion 3 needs a concrete demonstration method.

    Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:194`

    Brief claim: a counter move performs one row write, not a document rewrite, demonstrated not asserted.

    Required fix: Define the demonstration. Examples: instrument the SQLite command path in a test seam, assert no JSON store file timestamp changes after import, or use a focused integration test that counts `INSERT` or `UPDATE` statements for one observed delta.

24. APPROVAL POINT: Acceptance criteria 1, 5, and 6 are strong and falsifiable.

    Citation: `docs/architecture/gateway-sqlite-mission-2026-07-15.md:189-201`

    Brief claim: compare `GET /stats/data` before and after field by field, keep all seven solution test projects green, and pin migration with a real JSON fixture.

    What the code says today: `StatsPageEndpoint.cs:40-71` maps `/stats/data` from the aggregator outputs. The solution contains seven test projects under `cc-director.sln`: Avalonia, Core, Engine, Gateway, HostedAgent, Launcher, and Terminal.Avalonia tests. These criteria are measurable.

25. APPROVAL POINT: The prompt-log and all-time distinct-session issues explicitly excluded by the owner are correctly preserved in this review.

    Citation: `GatewayPromptLog.cs:26-27`

    What the code says today: prompt-log retention is deliberately unbounded.

    Citation: `GatewayInputStatsAggregator.cs:45-61`

    What the code says today: all-time distinct-session sets are deliberately never pruned.

    Review stance: These are not defects to fix. The SQLite design must preserve their semantics.

Final verdict: CHANGES REQUIRED.
