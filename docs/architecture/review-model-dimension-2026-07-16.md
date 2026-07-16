# Review: Model Dimension Schema Version 2

## Verdict

No blocking findings. I would land this change.

## Findings

None.

## Review Notes

The version 2 migration is atomic with the version stamp in the current implementation. `Migrate` opens one transaction, runs `MigrateToVersion2`, writes `PRAGMA user_version=2`, and commits only after all steps complete. The migration adds `model_id` with `ALTER TABLE`, creates `model_identity`, and inserts the `models_since_utc` meta row inside that same transaction. A version 1 database cannot be stamped as version 2 unless those steps complete.

The migration preserves existing rows. `ALTER TABLE stat_delta ADD COLUMN model_id INTEGER` appends a nullable column, so preexisting rows read as `NULL` without a table rebuild or data copy. The tests assert both row totals and the new column, which matters because the unconditional final version stamp would otherwise make a missing step look successful.

The prune fold keeps the archive correct. The archive insert carries `model_id` in both the `SELECT` list and the `GROUP BY`, so pruning cannot collapse different models into one archived row or rewrite known models as unknown. SQLite groups `NULL` values together, so unknown model rows aggregate into a single unknown bucket per remaining archive key, which matches the intended absence semantics.

The `bool isRepo` to `IdentityKind` refactor preserves the existing repository and agent behavior. Repository and agent identity lookup still uses the same case-insensitive dictionaries, pending identity dedup still uses ordinal ignore case comparison, and session membership is still only recorded for repository and agent identities. The added model kind is rejected by `SessionsFor`, so a model cannot silently enter a repository or agent session table.

`ModelTotals` includes archive rows and the null bucket without double counting. It groups directly over `stat_delta` by `model_id`, includes all rows like the other all-time totals, and does not join against the identity table in a way that could multiply rows. Each counted human turn lands in exactly one model bucket: a concrete model id or `NULL`.

## Verification

Ran:

```text
dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --filter "FullyQualifiedName~GatewayStatsDatabaseTests|FullyQualifiedName~GatewayInputStatsAggregatorTests" --no-restore
```

Result: passed, 48 tests.

One remaining coverage gap is that the new tests do not directly force pruning of rows with multiple model ids and a null model id. I traced the SQL path above and do not consider this blocking.
