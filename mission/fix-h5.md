# MTR gap H5 - turn-brief surface deny + internal-reader quarantine

Branch `fix/audit-h5-turnbrief`, pull request #1994.

## What H5 is

The Gateway turn-brief store (`GatewayTurnBriefStore`) addresses briefs, explain reports,
packages, the latest cache, and the feedback corpus by BARE session id under one shared
directory, with no tenant in any path, file name, or record. Issue #549 retired the only writer,
so it is legacy read-only data that cannot be attributed to a tenant after the fact. On a hosted
Gateway the surface leaked and could be overwritten across tenants:

- the six HTTP read/feedback routes served or overwrote another account's material;
- TWO INTERNAL readers wired in `GatewayHost` embedded a foreign tenant's brief into this
  caller's plane:
  - `interruptedBriefFor(sid)` - enriches each `GET /interrupted` row with the store's last
    RailLine/Headline;
  - `briefHistoryFor(sid)` - seeds `POST /interrupted/{dir}/{pid}/restore`'s continuation prompt
    from the store's full brief history.

The honest fix is a DENY + QUARANTINE (the store cannot be partitioned after the fact), matching
the transcription-analysis (#1897) and recording denies.

## Fix (two parts)

1. ROUTE DENY - the six HTTP routes are refused on hosted through the shared
   `HostedRouteDeny.Group` per-route primitive in `TurnBriefGatewayEndpoints`. Self-host (one
   tenant) is byte-identical. Proven revert-proof by `HostedTurnBriefDenyTests` (17 assertions).
   THIS PART WAS CORRECT AND UNCHANGED.

2. INTERNAL-READER QUARANTINE - the two `GatewayHost` reader lambdas return nothing on hosted:
   `interruptedBriefFor` returns `(null, null)`, `briefHistoryFor` returns an empty list.

## The Codex CHANGES-NEEDED residual (this increment)

The route-deny half was revert-proof. The two internal-reader guards were NOT: removing both
built 0/0 and all 17 turn-brief tests still passed, because NO test drove the ACTUAL
`/interrupted` + restore production path with a seeded foreign-tenant brief. The guards were
untested.

### Added: `HostedInterruptedBriefQuarantineTests` (+ self-host control)

Drives the REAL production paths - a real hosted `GatewayHost`, a real tunnel-connected
`FakeTunnelDirector` answering `interrupted-list` / `create` / `patch` over the stream, tenant
resolved from an authenticated device key - with a foreign brief seeded on disk under the exact
session id the crash journal reports. The same wire path production uses.

- `The_interrupted_list_is_not_enriched_with_a_foreign_brief_on_hosted` - the row is served (its
  own Director reported the journal, which proves the fan-out reached `interruptedBriefFor`), and
  it carries NO foreign RailLine/Headline.
- `The_restore_prompt_carries_no_foreign_brief_history_on_hosted` - the restore returns 201, and
  `ContextSent` (the exact continuation prompt) takes the "No turn briefs survived" branch and
  never embeds the foreign headline / rail line / intent.
- `SelfHostInterruptedBriefControlTests` proves the SAME paths DO embed the brief off hosted, so
  the hosted absence is a gate firing, not a route that never carries a brief.

If the Director is absent the fan-out surfaces no row and the restore 502s, so both hosted
assertions fail loud - no false green.

### Revert-proof (RUN, not described)

In `GatewayHost.cs`, removing EACH guard independently reddens a DISTINCT test with the symptom:

- remove `interruptedBriefFor` guard ->
  `The_interrupted_list_is_not_enriched_with_a_foreign_brief_on_hosted` FAILS:
  `Assert.Null() Failure ... Actual: "Another tenant's private headline"`.
- remove `briefHistoryFor` guard ->
  `The_restore_prompt_carries_no_foreign_brief_history_on_hosted` FAILS: the continuation prompt
  embeds the brief state instead of "No turn briefs survived".

Each guard is individually load-bearing. The route-deny was not touched.

## Gates

- `CcDirector.Gateway` + `CcDirector.Gateway.Tests` build 0 warnings / 0 errors.
- Targeted run - the new interrupted-brief tests, the turn-brief deny + self-host + future-route
  suites, `GatewayInterruptedTests`, and the two solo tenancy filters
  (`OnOneRoute_TheCredentialAloneDecidesWhetherATenantScopeExists`,
  `The_roster_and_the_commands_agree`): 33 passed, 0 failed.

## Merge note

Touches `GatewayHost.cs` (unchanged in this increment - only the test file is new) - serializes
at merge with other `GatewayHost.cs` work.
