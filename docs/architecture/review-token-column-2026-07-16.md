# Review: Token Column

## Verdict

No blocking findings. I would land this change.

## Findings

None.

## Review Notes

The version 3 migration is atomic with the version stamp. `Migrate` opens one transaction, runs each needed migration step, writes the final `PRAGMA user_version`, and commits only after the steps complete. `MigrateToVersion3` creates `token_delta`, its hour index, and `token_highwater` inside that caller transaction. I do not see a normal on-disk path that can half-apply those tables while still ending at schema version 3.

The token fold uses the same high-water rule as the existing counter lanes. It runs before the input bucket guard, so a poll that has token growth but no new submitted-turn bucket still folds the token increase. For each persisted scalar, growth folds only the increment, a dropped snapshot folds the current value as fresh spend, and a repeated identical snapshot queues neither a token row nor a high-water write.

The prune path preserves token spend. `PruneLocked` now runs when there are input rows or token rows, so token-only growth can trigger old detail cleanup. The token archive insert carries `model_id` in both the `SELECT` and `GROUP BY`, so pruning does not collapse known models into the null bucket or merge different models together. All-time token totals include archive rows, while hourly token totals exclude the archive marker.

The schema stores only summable spend. `token_delta` has input, output, cache-read, and cache-creation token columns plus the nullable model id. There is no context occupancy column, and the aggregate methods sum only those four spend columns.

I did not find a nondeterministic write introduced by this diff. Writes are driven by deterministic high-water comparisons under the aggregator lock, and the in-memory mirrors advance only after the database transaction commits.

## Verification

Ran:

```text
dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --filter "FullyQualifiedName~GatewayStatsDatabaseTests|FullyQualifiedName~GatewayInputStatsAggregatorTests" --no-restore
```

Result: passed, 60 tests.
