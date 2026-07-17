# Codex Final Review - Gateway SQLite Branch

Date: 2026-07-15
Reviewer: Codex
Scope: full diff of `feat/gateway-sqlite` against `origin/main` as reviewed from branch head `230185ef`.

## Verdict

CHANGES REQUIRED for merging this branch as-is.

The test storage-root redirect should land. The unused Gateway SQLite database foundation should not land on `main` until the production fold actually calls it.

## Findings

1. **Blocking - `GatewayStatsDatabase` is dead production code on this branch.**

   Evidence: `rg -n "GatewayStatsDatabase" src -g "*.cs"` finds production references only inside `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs`; every construction is in `src/CcDirector.Gateway.Tests/GatewayStatsDatabaseTests.cs`. `GatewayInputStatsAggregator` still has zero references to the database and still writes `gateway-input-stats.json`.

   This is not a harmless foundation under this repo's dead-code rule. Merging it would add a new production class, a direct `Microsoft.Data.Sqlite` package reference in `src/CcDirector.Gateway/CcDirector.Gateway.csproj`, and a detailed schema contract that no shipped code exercises. The branch's highest-risk behavior - the #1647 fold/back-fill and separate agent-driven lane - is precisely the part not built, so the schema tests only prove a disconnected table shape.

   Required fix: do not merge `src/CcDirector.Gateway/Stats/GatewayStatsDatabase.cs`, `src/CcDirector.Gateway.Tests/GatewayStatsDatabaseTests.cs`, or the Gateway `Microsoft.Data.Sqlite` package reference until the fold is wired and the product has a caller. Keep that work on the phase branch, or merge it together with the first production caller and caller-level tests.

2. **Blocking - stale design/review records in the branch would put deleted plans on `main` without a clear supersession boundary.**

   The current mission brief does record the owner ruling near the top: delete the import, legacy reader, parity check, baseline tables, `AgentsSeeded`-null rule, and old `HighWater` import. But the branch also adds review records and manager-plan text that approve or describe the now-deleted import/baseline/legacy-reader path. The clearest example is `docs/architecture/gateway-sqlite-review-codex-phase1-legacy-store-2026-07-15.md`, whose scope names `GatewayInputStatsLegacyStore.cs`; that file is not in the final diff.

   Historical review artifacts are useful inside the mission, but on `main` this reads as contradictory architecture: one file says the legacy reader was approved, another says it was deleted outright, and the product code contains neither the reader nor the fold. Required fix: either exclude the superseded review/plan documents from the main merge, or mark them explicitly as superseded at the top of each affected file. At minimum, the legacy-store review must not land as an unqualified approval for a file that no longer exists.

## ModuleInitializer

`src/CcDirector.Gateway.Tests/TestStorageRootRedirect.cs` should land independently.

The hazard it fixes is real and not specific to the abandoned SQLite import: many Gateway tests resolve default stores through `CcStorage.Root()`, and without an assembly-level redirect those writes can hit the owner's live `%LOCALAPPDATA%\cc-director` state. The initializer runs before test bodies, unconditionally points `CC_DIRECTOR_ROOT` at a temp directory, and has a guard test proving the redirect is active. The filtered test run for `GatewayStatsDatabaseTests|TestStorageRootRedirectTests` passed all 12 tests.

One caveat: this does not make process-wide `CC_DIRECTOR_ROOT` mutation generally race-free; many existing tests still temporarily change the environment variable. It does, however, change the default and restored value from the owner's live root to a disposable temp root, which is the important live-data protection.

## Verification

- Reviewed `git diff --stat origin/main...HEAD` and `git diff --name-status origin/main...HEAD`.
- Confirmed the database has no production caller with `rg -n "GatewayStatsDatabase" src -g "*.cs"`.
- Ran `dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --no-restore --filter "FullyQualifiedName~GatewayStatsDatabaseTests|FullyQualifiedName~TestStorageRootRedirectTests"`: passed, 12 tests.
- Ran `dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj --no-restore`: passed.

## Merge Recommendation

Split this branch.

Merge the test storage-root redirect and its tests now. Do not merge the unused SQLite database class, its package reference, or disconnected schema tests until the aggregator fold is implemented against it. Clean or supersede the architecture records before main so the branch does not leave deleted import/baseline machinery looking approved.
