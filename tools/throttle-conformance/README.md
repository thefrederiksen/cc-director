# throttle-conformance

The check that fails when the two consumers of the Your Throttle figure diverge (mission "Clean up Your
Throttle", phase three). Both consumers read one substrate - the Gateway's submission ledger
(`activity_events`, `turn-submitted`) - through one definition, stated once in
`src/CcDirector.Gateway/Throttle/ThrottleDefinition.cs`:

> The shared figure is computed over activity_events rows where EventType is turn-submitted and
> InputOrigin is present, grouped by the origin's modality and surface.

`conformance.py` computes that figure twice for one account over one calendar week and compares every
number:

1. **The library.** `ThrottleConformance.csproj` runs the Gateway's own `ThrottleDefinition` and
   `ThrottleLedgerReader` - the code behind `GET /stats/data` - against the hosted Gateway database,
   read-only, and prints the figure as the same JSON the feed serves under `throttle`.
2. **The mentor report's side.** The mentor harness's own reader of the same ledger (`origin.py`'s
   `Ledger` over `metrics.py`'s `load_events`), fed from the harness's own extract of the table, with the
   predicate applied.

A third, plain reading of the extract's JSON lines covers what the mentor's reader does not carry - the
per-agent split and the per-repository join through `session_history` - so those are compared as well.

```
python tools/throttle-conformance/conformance.py --account soren --week 2026-W35
python tools/throttle-conformance/conformance.py --account mario --week 2026-W34 --report out.md
python tools/throttle-conformance/conformance.py --account soren --week 2026-W35 --break-predicate   # must exit 1
```

Exit 0 when every number agrees, 1 on any difference, 2 on a setup error. `--break-predicate` misapplies
the predicate on the mentor side on purpose (it drops the terminal-typed turns, which is defect one) so
the check can be shown to go red; it is never green with that flag.

It needs: the mentor harness checkout with its `config.json` (`--mentor-dir`, default
`D:/ReposFred/devthrottle_internal/tools/mentor`), the harness's extract under the configured data root,
and the hosted database connection string (`--connection-file`, or the `DEVTHROTTLE_GATEWAY_DB_CONNECTION`
key in the credentials file the mentor config names). The connection string is never printed.

The tool never opens the database through `GatewayDatabase`, whose `Open()` checks for and applies pending
migrations: a conformance check must never be the thing that migrates the production schema.
