# Phases four and five - closed by the Manager

**What this is.** The Manager's record of the last two phases of "Clean up Your Throttle": the period
selector on the product side (`#2692`), and the mentor report becoming a CONSUMER of the library and linking
to Your Throttle on the internal side (`devthrottle_internal#1680`, ruling R3, ruling R5). The two workers'
own records are `phase4-selector.md` (this folder) and
`docs/missions/clean-up-your-throttle-2026-09-05/phase5-report-consumes-the-library.md` in the internal
repository, and everything below rests on reading those records, the diffs and the evidence files, not on
the workers' summaries.

**Written:** 2026-09-05, by the phase four and five Manager. **Branches:** `mission/clean-up-your-throttle` in
both repositories.

---

## What is true now that was not this morning

- **The report asks the library.** `tools/mentor/metrics.py` runs the product's own Your Throttle code
  (`tools/throttle-conformance`, the code behind `GET /stats/data`) for the account's Monday-to-Monday
  week and carries the answer as the headline group `throttle`. The report's own spoken-against-typed
  computation over the prompt log is DELETED, and a check (`verify_throttle.py`, run by the chain
  immediately before the send) re-asks the library and fails naming every difference between it, the
  metrics and the rendered rings. For the owner's 2026-W35 the library, the page's first ring, the email's
  headline and phase three's conformance figure are one number: 1,786 turns, 1,015 spoken, 57 per cent.
  The report used to print 59 from its own count (58.76); that is disclosed once in the phase five record.
- **The page says which stretch of time it describes, and defaults to a rolling seven days.** The Gateway
  resolves one of four windows (none, `days=N`, `week=YYYY-Www` in the caller's own zone, or explicit
  instants), states which on every answer, and serves the lengths the selector may offer, the longest being
  the ledger's retention. One selector component in client-core is mounted by both the Cockpit and the
  phone; both read the window from the URL and write a choice back to it.
- **The report links to Your Throttle on the week it covers.** One sentence beneath the ring row and
  beneath the two figures in both email parts, pointing at
  `https://gateway.devthrottle.com/your-throttle?week=2026-W35`: no token, no tenant id, no address, behind
  the Gateway's sign-in, which carries the whole path through `next=` (verified against the live Gateway).

## What the Manager checked, beyond reading the records

- Both pushes verified on `origin` before either worker was reaped; both trees clean.
- The phase four Gateway diff read: four forms one at a time, the week resolved through the morning
  report's own midnight rule, the choices derived from the retention sweep, an unknown zone a loud failure.
- The internal branch's committed diff swept for secrets: no tenant id, no address, no connection string
  (the only `Host=` and `Password=` are invented test values).
- No reader of the deleted `prompt_shape.modality_share` and `surface_share` remains in harness code; the
  one stale README sentence that still named the old path was corrected by the Manager (`673374d`).
- The saved ring row (`ring-row-soren-2026-W35.html`) carries the sentence once, outside the ring cards,
  with the exact address, and the rings print 57 and 14.

## The parked suite (R18) over the finished product branch, `8da683f6e`

`.\scripts\test-local.ps1 -Parked`, started 15:26 and finished 16:26 (exit 1), the Gateway lock uncontended.

| suite | total | passed | failed | note |
|---|---:|---:|---:|---|
| nine default projects | 4,974 | 4,969 | 3 | all three in `Gateway.UnitTests`, one class: `HostedSchemaRefusesAnUnownedRowTests` (three facts), each with a Postgres `duplicate key value violates unique constraint "pg_type_typname_nsp_index"`. **A rig collision, not a defect:** this class runs against the local Postgres rig and ran in parallel with `Gateway.Tests`, which uses the same rig; re-run alone after the suite it is 8 of 8 green. Two skipped |
| Core.Tests | 4,394 | 4,386 | 0 | eight skipped by their own gates; the two R10 pins phase three fixed stay green |
| Gateway.Tests | 2,353 | 2,346 | 3 | the pre-existing `ContextLessRouteCensusTests` pair (unchanged from main, needs a ruling; a session named "Voice narration retry" said today it has a fix for `#2679` queued), and `GatewayStatsWritePathPostgresTests.SessionHighWater_ManyConcurrentWriters_LeaveTheLedgerEqualToTheWatermark` with `relation "gateway_stats.repo_identity" does not exist` - the other side of the same rig collision (the unit class above creates and drops that rig's schema while this test writes to it). Exonerated by reachability: this branch's five Gateway files are the stats endpoint, the throttle definition and its window types, and the morning-report midnight helper; nothing in the writer, the data layer or the migrations. NOT re-run alone: a session was waiting on the lock and had been told it was free. Four skipped |

So over the finished branch the only reds are the census pair that predates the mission and two faces of one
Postgres-rig collision between two projects of one run. Nothing this mission wrote is red. The four hosted
facts phase four added (`ThrottleFeedReadsTheLedgerTests`, 6 to 10) passed inside this run, and the class
passed four more times by name during the phase.

## Decisions taken by the Manager, for the Architect

1. **The report reaches the library through its command-line face, not through `GET /stats/data`.** The
   harness holds no device key and reads the hosted database directly for both accounts; the command-line
   tool runs the same compiled code the endpoint runs. This is the library the owner asked for, reusable by
   a caller that is not the Gateway. A run of the report's figure through the live endpoint is a step for
   after the deploy and would need a per-account device key the harness does not hold.
2. **The URL contract is `week=YYYY-Www`, resolved by the Gateway in the tenant's own zone.** The client
   never computes a date; the link carries the report's own ISO week literally (R5), and the Gateway turns
   it into the same Monday-to-Monday bounds the harness uses.
3. **Block 3 of the report no longer carries a second spoken-against-typed count.** R9's settled
   instruction 2 forbids two numbers from two substrates without the page saying so; the transcript's
   classification now draws one bar for the person and a sentence saying which substrate it is.
4. **The report's link goes to the Cockpit page, not the phone page.** The Cockpit holds the detail
   behind the figures, which is what the sentence promises; the phone page honours the same `week` and is
   proven to.

## Ruling R19, implemented after the close

The owner challenged the figure and the Architect ruled R19. Implemented by the Manager in the internal
repository (`1944625`): a required config key `attribution_corrected_utc` (`null` until the corrected
Gateway is deployed), one sentence after the Your Throttle link on the first report whose week spans the
deploy (or the week after, only if the spanning week's report was never sent), naming the date in the
account's zone, on no other week and restating nothing; ten tests over the real render, two mutations
watched, the real week rendered with a planted instant and restored. Detail in the R19 section of the
internal phase five note. **Whoever deploys the product branch sets that key to the deploy instant.**

## For the Architect, after landing

- The harness's real `config.json` (gitignored, main checkout) has `throttle_library` pointing at the
  mission worktree's `tools/throttle-conformance`, the only place the tool exists until the product branch
  lands. Move it to `D:/ReposFred/devthrottle/tools/throttle-conformance` after landing, or `metrics.py`
  fails at the top naming the key.
- The deployed Gateway still runs the old feed. Until the product branch lands and deploys, the link in a
  sent report would open a page that ignores `week`; nothing is sent until then.
- The pre-existing `ContextLessRouteCensusTests` red on main (three `/gateway/rules/{id:guid}` routes with
  no census verdict) is unchanged and still needs a ruling; see the phase three note.

## What is NOT proven, said plainly

- **No report was sent.** The chain change is proven by a test that plants a drift and shows the send never
  runs, and by running the four steps by hand; no email left the machine.
- **The link was not followed end to end.** The sign-in redirect carrying the query was verified live; the
  page honouring `week` is proven in a router with the Gateway stubbed; the live Gateway cannot answer
  `week` until this lands and deploys. The three pieces are each proven; the chain of them was not walked
  in one browser.
- **The pages were not looked at in a browser or on a phone.** Layout of the selector on a phone screen is
  unseen.
- **A daylight-saving boundary week was not tested as a week**; the shared midnight rule has its own tests.
- **The regenerated OpenAPI types carry main's route churn** since they were last generated (21 routes
  gone, 104 added), explained in shape by the worker and not audited entry by entry; nothing reads the
  generated types for the stats route.
- **The definition sentence is carried verbatim by the report and not compared with a copy**, on purpose
  (a copy is a second statement); the product's conformance check is where the sentence is pinned.
- **The second account's page was not rendered**; its metrics and its share of the average were.
