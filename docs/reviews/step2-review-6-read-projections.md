# Step 2 Review 6 — read projections

Review range: `origin/nosqlite-stats-w2-model..4bae331b4`.

Verdict: **BLOCKED — 7 findings (3 High, 2 Medium, 2 Low).** I found no output mismatch in the twelve ported projections on the supplied fixture: the rank operations remain in C#, the specified final comparisons are ordinal, the two hourly series exclude `ARCHIVE`, all all-time aggregates include it, display strings come from the identity mirror, and each tenant-bearing projection applies a tenant scope. The branch nevertheless cannot be integrated as reviewed because it is not based on the current worker-2 head and the requested range reintroduces two already-proven broken-store adoption paths plus stale model metadata.

## Scope failure: the stated rebase did not happen

**PROVED BY RUNNING.** `git merge-base origin/nosqlite-stats-w2-model 4bae331b4` returned `b2a3c7ac79160771125c1a3ac61eb9d47f278ddd`; `git rev-parse origin/nosqlite-stats-w2-model` returned `7d9e9f23917ee643de229cfaf0ef3b3164175ce9`; and `4bae331b4^~3` is the merge-base. Worker 2 has six commits after that base (`e43922319` through `7d9e9f239`). Consequently the exact requested diff includes reversals in adoption code, tests, evidence, model snapshots, and migration identities even though the four commits authored on this branch only touch the read port.

## Findings

### 1. High — the branch is not rebased and reverses worker 2's containment work

**File/line:** `src/CcDirector.Gateway/Stats/Data/GatewayStatsSqliteAdoption.cs:119`; `docs/evidence/step2-self-host-adoption.md:175`; migration/model files shown by the requested diff.

**PROVED BY RUNNING.** The ancestry commands above show that worker 2's current head is not an ancestor. `git diff --name-status origin/nosqlite-stats-w2-model..4bae331b4` therefore reports the read branch deleting the later worker-2 guards and tests, removing two refusal reasons, removing eight model defaults, and renaming both migration baselines backward. This is not merely stale prose: findings 2–4 reproduce the behavioral/model regressions at this head. The branch must be rebased onto `7d9e9f239` before its read-port diff can be integrated or reviewed as an isolated worker-3 change.

### 2. High — an interrupted migration is certified usable and then fails outside containment

**File/line:** `src/CcDirector.Gateway/Stats/Data/GatewayStatsSqliteAdoption.cs:119`; `docs/evidence/step2-self-host-adoption.md:252`.

**PROVED BY RUNNING.** A temporary review probe created a real version-5 store by constructing `GatewayStatsDatabase`, added Entity Framework's empty `__EFMigrationsHistory` shape, and called `Adopt`. The method returned `AlreadyTracked`; the subsequent `Database.Migrate()` threw SQLite `already exists`. The probe passed only when it asserted this broken sequence. The implementation checks `history.Exists()` rather than whether the baseline is recorded, even though the evidence document itself says this lets an interrupted first migration escape the non-fatal adoption boundary. Worker 2's current branch already refuses this state as `MigrationHistoryIncomplete`; this branch drops that fix and its test.

### 3. High — a version-5 store missing a required column is stamped, then the new EF read throws

**File/line:** `src/CcDirector.Gateway/Stats/Data/GatewayStatsSqliteAdoption.cs:141`; `src/CcDirector.Gateway/Stats/GatewayInputStatsAggregator.cs:974`.

**PROVED BY RUNNING.** A temporary review probe built a real version-5 store through `GatewayStatsDatabase`, ran `ALTER TABLE stat_delta DROP COLUMN chars`, and called `Adopt`. This head returned `Adopted` and stamped the baseline because lines 145–153 inspect table names only. Constructing the aggregator still succeeded, but `CurrentTotals()` then threw on the missing `chars` column. This directly violates the claimed containment rule and is especially relevant to this branch because the newly ported EF projection is the query that fails. Worker 2's current branch checks required columns and refuses this state; this branch drops that fix and its test.

### 4. Medium — the EF target model has lost the eight version-5 `tenant DEFAULT 'local'` values

**File/line:** `src/CcDirector.Gateway/Stats/Data/GatewayStatsDbContext.cs:125` (and the analogous mappings at lines 145, 159, 170, 185, 194, 203, and 212).

**PROVED BY RUNNING.** A temporary probe read the runtime EF metadata for `StatDeltaEntity.Tenant` and obtained a null default, then built a real store through `GatewayStatsDatabase` and read `pragma_table_info('stat_delta')`, which returned `'local'`. The literal SQLite baseline still happens to reproduce the old file, so the existing baseline-equivalence tests stay green; the divergence is in the target model and snapshots used to scaffold later migrations. A later table rebuild can therefore silently drop a version-5 schema fact. Worker 2's current branch records these defaults explicitly; this branch removes them.

### 5. Medium — the parity fixture does not prove that the final rank comparison is ordinal

**File/line:** `src/CcDirector.Gateway.Tests/Stats/GatewayStatsReadParityTests.cs:164`; `src/CcDirector.Gateway.Tests/Stats/GatewayStatsReadParityTests.cs:217`; production comparison at `src/CcDirector.Gateway/Stats/GatewayInputStatsAggregator.cs:1132`.

**PROVED BY RUNNING (mutation).** I temporarily replaced the repository final tie-break with `StringComparison.CurrentCulture` and ran all three `GatewayStatsReadParityTests`; all 3 passed. The fixture ties `aaa` and `zzz`, whose relative order is the same under ordinal and ordinary culture-aware comparisons, and its shape assertion only checks that a numeric tie exists, not that the chosen pair distinguishes comparers. The production code is currently correct, but the claimed regression proof is not: this load-bearing comparer can silently change while the parity suite remains green. Use comparer-sensitive strings and assert their expected ordinal order.

### 6. Low — `StatementsExecuted` no longer counts every statement

**File/line:** `src/CcDirector.Gateway/Stats/GatewayInputStatsAggregator.cs:137`; `src/CcDirector.Gateway/Stats/GatewayInputStatsAggregator.cs:1478`; representative EF read at line 951.

**INFERRED BY READING.** The comments and property contract say every database statement passes through the raw helpers and increments the counter. All ported reads now execute through `_contexts.CreateDbContext()` and never increment it. The value can no longer support its documented meaning or any assertion about total database traffic. Either instrument EF reads too or narrow/rename the counter to raw write-path statements.

### 7. Low — the membership-mirror comment claims a tenant-scaling optimization the query does not perform

**File/line:** `src/CcDirector.Gateway/Stats/GatewayInputStatsAggregator.cs:321`; queries at lines 333–341.

**INFERRED BY READING.** The comment says the joins stop `LoadMirror` from reading every tenant's membership rows. Neither join has a tenant predicate; both enumerate the complete joined `repo_session`/`repo_identity` and `agent_session`/`agent_identity` result sets. The joins correctly attach a tenant to each mirror key and drop orphaned rows, but they do not reduce the startup read to one tenant. Remove the unsupported performance claim or implement a different loading strategy if bounded startup work is required.

## Sharp-question audit

- **Ranking:** all four rank projections materialize before sorting. `RepoTotals`, `AgentTotals`, and `ModelTotals` rank by turns, then characters, then `string.CompareOrdinal`; `TokenSpendByModel` ranks by total tokens, then `string.CompareOrdinal`. No rank moved into SQL. Finding 5 is a test-proof gap, not a current production comparator defect.
- **Archive marker:** `HourlyTurns` and `TokenSpendByHour` exclude the marker. `CurrentTotals`, `WingmanUsage`, `RepoTotals`, `AgentTotals`, `ModelTotals`, `TokenSpend`, `TokenSpendByModel`, and `AgentDrivenUsage` include all rows (the last lane has no hour/marker column). The two since-stamps and session counts are not delta-series projections.
- **Display spellings:** repository, checkout, agent, and model display values used by projections come from the in-memory identity mirrors. SQL groups by surrogate IDs and never joins/group-orders on display text.
- **Tenant isolation:** every tenant-bearing delta query filters `Tenant`; repository and agent session counts use explicit joins to tenant-filtered identity rows; agent enumeration uses the per-tenant identity map; `AgentsSinceUtc` is keyed by tenant. `ModelsSinceUtc` is intentionally the one tenant-agnostic schema stamp. The supplied three-tenant parity fixture exercises every public accessor with shared bare session IDs and shared display spellings; the dedicated accessor tests exercise the two session-count joins and mirror tenant keys. I found no foreign-row leak in these paths.
- **Fixture provenance:** verified. `GatewayStatsReadParityTests.WriteFixture()` constructs the real aggregator, which constructs and runs `GatewayStatsDatabase`; the ported and frozen readers then consume the same physical SQLite rows. The adoption and baseline fixtures also build the hand-rolled side by constructing `GatewayStatsDatabase`, while only the comparison side of the baseline-equivalence test is migration-built.
- **Detector:** verified by execution. `TheComparison_RejectsAOneNumberDifference_AndNamesTheField` passed only after `Record.Exception` observed the deliberately damaged rendered value and the assertion message contained `Turns`. It tests the serializer/assertion comparison rather than mutating the frozen reader implementation, but it is capable of failing and did detect the perturbation.

## Executed evidence

- Clean head, original focused run: 22/22 passed across `GatewayStatsReadParityTests`, `GatewayStatsReadTenantScopeTests`, `GatewayStatsSqliteAdoptionTests`, and `GatewayStatsSqliteBaselineEquivalenceTests`.
- Temporary defect probes: 3/3 passed while asserting the reproduced missing-column failure, interrupted-history failure, and missing model default. The probe file was removed.
- Temporary ordinal mutation: 3/3 parity tests still passed with a culture-aware repository tie-break. The mutation was reverted.
- The worktree was clean after all probes; the only final filesystem change is this review report.
