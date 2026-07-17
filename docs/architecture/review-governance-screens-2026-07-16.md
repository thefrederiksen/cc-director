# Review: Governance Screens

## Verdict

No blocking findings. I would land this change.

## Findings

None.

The prior blocking finding is fixed. The new activity and model-spend table rows are built through `trow`, which assigns cell text through `textContent`. The records-derived model name no longer reaches the page through string-concatenated `innerHTML`.

## Review Notes

The local date rollup looks correct for the intended hour-key shape. `localYmd` parses the stored UTC hour key as UTC, then asks `Intl.DateTimeFormat` for the configured display zone's calendar date. The week key is computed from that already-local calendar date, and the Monday calculation handles month and year boundaries through `setDate`.

The page remains self-contained. I did not see any added external resource references, and the existing guard still checks for `http://` and `https://`.

The period toggle state is stable across refreshes. `PERIOD` is module-level state, `load` refreshes `LAST` and rerenders with the current period, and a click rerenders the activity table from `LAST` without refetching. I do not see a path that snaps back to Day after a refresh.

The null-model bucket is not filtered. `renderModelSpend` iterates every row in `tokenSpendByModel`; a null model displays as `Not recorded`, and the share denominator is the sum of all rendered rows. The fix keeps that behavior while making non-null model names inert text.

## Verification

Ran:

```text
dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --filter "FullyQualifiedName~StatsPageEndpointTests" --no-restore
```

Result: passed, 5 tests.

Also checked that the served source contains `function trow(` and `textContent = c.t`, and no longer contains the exact vulnerable `"<td>" + name` concatenation.

Residual test gap: a real browser-level guard for hostile model names and a JavaScript rollup test for local day, Monday week start, and year-boundary weeks would make this harder to regress. I agree that a source tripwire is not behavioral coverage.
