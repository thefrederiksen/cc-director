# Codex Review: Phase 1 Manager Plan

Reviewed 2026-07-15 by the Codex reviewer session for the mission "SQLite on the Gateway".

Reviewed plan: `docs/architecture/gateway-sqlite-phase1-manager-plan.md`.

Repository state observed during review: `624948698da8fb61fb0f284b184564bd1af9ee1c`.

Verdict: APPROVED, with the implementation guardrails below.

## Findings

1. APPROVAL POINT: Finding 1 is real. `HighWater` must be imported.

   Plan claim: Decision 5 imports baseline totals, hourly buckets, repository tallies, agent tallies, wingman turns, and distinct-session tables, but does not explicitly import the existing `HighWater` map into `session_highwater`.

   Code check: `GatewayInputStatsAggregator.cs:448-457` persists eight logical sections: `Totals`, `HighWater`, `Hourly`, `WingmanTurns`, `WingmanSessions`, `Repos`, `Agents`, and `AgentsSinceUtc`. `GatewayInputStatsAggregator.cs:507-533` restores all of them today. `GatewayInputStatsAggregator.cs:336-354` uses high-water state to convert repeated roster observations into only the increase, and `GatewayInputStatsAggregator.cs:422` updates that high-water state.

   Review result: approved. If `session_highwater` starts empty after importing baselines, the first post-cutover `GET /sessions` can refold every live session's whole current tally on top of the baseline. `/stats/data` cannot catch this at import time because `StatsPageEndpoint.cs:40-71` does not expose high-water. The plan is right to import `HighWater`.

2. APPROVAL POINT: The idempotent re-observe leg belongs in the import tests.

   Plan proposal: after import, re-observe the same roster and prove every all-time number is unchanged; prove the test fails when high-water import is removed.

   Review result: approved. This is the right test because it exercises the invariant high-water exists to protect. The production import should still be driven by the stored `HighWater` section, not by assuming the live roster can always be reconstructed from `/stats/data`. In tests, a controlled fixture can create the JSON store by observing a roster, import it, and re-observe that same roster.

3. APPROVAL POINT: Importing all eight `StoreFile` sections is the correct Phase 1 rule.

   Plan proposal: import all eight sections, including `HighWater` into `session_highwater` and `AgentsSinceUtc` into a baseline scalar.

   Review result: approved. This is a faithful elaboration of the brief, not a deviation. The brief's core rule is parity plus no synthetic historical rows. Importing `HighWater` preserves idempotency; it does not synthesize history.

4. APPROVAL POINT: Finding 2 is real. SQLite default grouping does not match the current .NET dictionaries.

   Plan claim: `_repos` and `_agents` are case-insensitive dictionaries, while SQLite default text grouping is case-sensitive. `_totals` is case-sensitive. The voice split is case-insensitive.

   Code check: `GatewayInputStatsAggregator.cs:55` creates `_repos` with `StringComparer.OrdinalIgnoreCase`. `GatewayInputStatsAggregator.cs:61` creates `_agents` with `StringComparer.OrdinalIgnoreCase`. `GatewayInputStatsAggregator.cs:32` creates `_totals` with the default tuple comparer. `GatewayInputStatsAggregator.cs:366` computes `isVoice` with `StringComparison.OrdinalIgnoreCase`.

   Review result: approved. A plain SQL `GROUP BY repo` or `GROUP BY agent` would be a semantic change. The live store having no current case collisions does not make the design safe.

5. APPROVAL POINT WITH GUARDRAIL: `repo_folded` and `agent_folded` plus identity tables are a faithful elaboration only if the C# comparer semantics are exact.

   Plan proposal: use folded grouping keys plus first-seen display identity tables, matching a .NET `Dictionary<string, ...>(StringComparer.OrdinalIgnoreCase)`.

   Review result: approved as an elaboration of the brief's `repo` and `agent` dimensions, not a deviation. The intent is exactly right: preserve current grouping and current display spelling.

   Guardrail: do not implement this as casual `ToLowerInvariant`, `ToUpperInvariant`, SQLite `NOCASE`, or any SQL-side collation that is only approximately `StringComparer.OrdinalIgnoreCase`. The implementation must resolve identities through C# using `StringComparer.OrdinalIgnoreCase`, or an equivalently exact mechanism. Tests must include case-variant repository and agent keys and prove first-seen display spelling wins.

6. APPROVAL POINT: `is_voice` is a faithful and useful elaboration.

   Plan proposal: store `is_voice` computed in C# from the same case-insensitive test at `GatewayInputStatsAggregator.cs:366`.

   Review result: approved. It preserves the current asymmetry: totals by modality remain case-sensitive, while hourly, repository, and agent voice-versus-typed splits use a case-insensitive voice test. The explicit column also prevents future SQL collation drift.

7. APPROVAL POINT WITH CLARIFICATION: The bounded statement-count tests must account for multiple changed buckets.

   Plan claim: a changed fold issues the bounded write mix.

   Review result: approved, but the tests should be precise. A "single delta" test can assert one `stat_delta` insert and one high-water upsert only when exactly one `(modality, surface)` bucket changes. A session observation with multiple changed buckets legitimately produces one delta row and one high-water upsert per changed bucket. The invariant to pin is "bounded by changed buckets and distinct-id first sightings, not by stored history size."

8. APPROVAL POINT: Finding 3 is not fallback programming.

   Plan proposal: keep an in-memory write-through mirror of operational state, loaded from SQLite once and written through on changes, while aggregate reads come from SQLite queries.

   Review result: approved. This is not fallback programming as long as SQLite remains the single durable source of truth, database open/import failures remain loud failures, and there is no JSON fallback path after import. It is the right way to keep unchanged `GET /sessions` roster polls at zero database writes.

9. CLARIFICATION: Do not call all mirrored operational state bounded.

   Plan text describes the mirrored operational state as bounded, including distinct-id sets.

   Code check: `GatewayInputStatsAggregator.cs:45-61` documents all-time distinct sets that are deliberately never pruned. The approved brief also preserves those never-pruned semantics.

   Review result: not a blocker because the plan later says distinct sets are deliberately never pruned, but the implementation must not treat wingman, repository, or agent distinct-id sets as bounded or prunable. The bounded claim applies to per-fold work, not to the lifetime cardinality of all-time distinct identifiers.

10. APPROVAL POINT: The schema respects the "no synthetic historical rows" rule.

    Plan proposal: post-cutover data goes to `stat_delta`; historical data goes to baseline tables and distinct-id tables; no historical `stat_delta` rows are synthesized.

    Review result: approved. This preserves the core design approved in the brief.

11. APPROVAL POINT: The plan correctly keeps the model dimension out.

    Plan schema has no model column.

    Review result: approved. The approved brief records that `SessionDto` on `origin/main` has no model field for the Gateway to fold. Adding model now would create an unpopulated statistics dimension and would be a defect.

12. APPROVAL POINT: The seven semantic tests listed in the plan are the right tests.

    Plan list includes: typed turn under voice mode is a wingman turn; voice-mode session with no input still counts as a wingman session; repository and agent session ids are recorded only on real deltas; counter reset folds current count as fresh activity; zero-turn non-zero-character deltas fold; `AgentsSinceUtc` stamps once; unknown or empty agent tokens are preserved correctly.

    Review result: approved. These are exactly the edge cases likely to change silently during the port.

Final verdict: APPROVED.
