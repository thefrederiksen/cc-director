# Clean up Your Throttle - running state

This is the compact handoff note. A fresh Manager needs THIS FILE, the brief beside it, and
`cc-devthrottle workflow instructions mission`. Nothing else. Keep it current and short.

**Updated:** 2026-09-05, by the phase three Manager, closing phase three.

## Where the work lives

- Product repository worktree: `D:/ReposFred/devthrottle-throttle`, branch
  `mission/clean-up-your-throttle`.
- Internal repository worktree: `D:/ReposFred/devthrottle_internal-throttle`, branch
  `mission/clean-up-your-throttle`. Not touched until phase five.

## Current phase

**Phase three - the library - is DONE and pushed, and the four findings of the independent inspection of
phase two (`inspection-phase-two.md`, FAIL) are FIXED on the branch with route-crossing tests** - see
`phase3-library.md`, "The independent inspection of phase two, and its fixes". Phase two needs re-inspection
by the Architect's call; phase four (the period selector, #2692) is next.
Read `phase3-library.md` beside this file before planning it: the feed already states its window, takes
`from` and `to`, and refuses a window longer than the ledger keeps; phase four changes the default from
thirty days to seven (R15) and adds the selector on both surfaces.

## What is done and pushed

- The brief, and its amendments carrying rulings R10 to R18.
- Phase one: `reconciliation.md` and `evidence/`.
- Phase two: defect one and its guard, the R14 disclosure, the fleet-message origin (R12), phone voice
  through the one-shot transcription (R10), the mid-chain containment.
- Phase three: the library (`src/CcDirector.Gateway/Throttle/`), the feed on it, both pages on the feed,
  the conformance check (`tools/throttle-conformance/`), and `phase3-library.md` with the evidence under
  `evidence/conformance/`.
- The inspection fixes: the Gateway sets both attribution markers itself (`AgentDriven` from the credential,
  the spoken claim spent against `Voice/SpokenClaimRegistry`), the durable dictation composes a mixed message
  as typed, the tally observer fan-out is guarded, the source-count test is gone, and the stale Core pin is
  updated.

## Phase three, in one paragraph

Every count of turns on `GET /stats/data` now comes from the submission ledger through ONE definition
whose predicate is R17's sentence, verbatim. The page states its window, discloses the excluded population
beside the share, carries no character volume (R16) and no wingman ring (dropped, same reasoning as R16 -
see the decisions list in `phase3-library.md`), and on a self-hosted Gateway shows one sentence (R1, R6).
The conformance check ran over two real weeks for both accounts against the live hosted database and every
number agreed with the mentor harness's own reading of the same ledger; run with the predicate deliberately
broken it went red with defect one's shape.

## The parked suite (R18) - RUN IN FULL, result recorded

Run on the phase two close commit `050f7174a`, before any library work. Default projects all green.
`Core.Tests`: 4,394 tests, ONE failure - `TerminalPromptInjectionChokepointTests` pinning the mobile
dictated-send call, which phase two changed for R10 without updating the pin. A phase two defect; fixed in
phase three (the pin now carries the new call on both shells). `Gateway.Tests`: see `phase3-library.md`
"Results" for the count; TWO failures in `ContextLessRouteCensusTests`, which PREDATE THE MISSION - three
`/gateway/rules/{id:guid}` routes were mapped on main after the census was written (2026-08-01) with no
request context and no census verdict. Not phase two's, not phase three's, not fixed here: the census
demands a written tenant-confinement verdict per route, which is a ruling, not a repair. **For the
Architect:** the parked Gateway suite is red on main for this reason and will stay red when this lands.

## What phase three did NOT prove, said plainly

- The mentor report does not yet CONSUME the library (R3); that is the internal repository's work. What is
  proven is that the library and the harness's reading of the ledger agree over real weeks.
- The conformance check ran the library in-process against the hosted database, not through the deployed
  `GET /stats/data`, which still runs the old feed until this lands and deploys.
- The generated OpenAPI client types were not regenerated (they come from a running Gateway); phase four
  should regenerate them.
- The per-agent agent-driven split was zero in every measured week (R12 landed after them); proven by test.

## Open, for the Architect only

- The wingman ring was dropped from both pages (decision 1 in `phase3-library.md`). If that is wrong, the
  voice-mode fact has to reach the ledger first; nothing on the tally can come back.
- The pre-existing route-census red on main, above.
- Mario's record: most of his submissions carry no input origin (758 of 976 in W35). The library discloses
  it; something on his side stamps almost nothing. Belongs in the final report.

## Known ground already established (do not re-derive)

- The report's headline is a share of human PROMPTS, the same unit as Your Throttle's turn share.
- `GET /stats/data` is tenant-scoped, refuses when no tenant resolves (403), answers a self-hosted Gateway
  with `available: false` and one sentence, and needs a device key on hosted.
- Two accounts in the mentor configuration, `soren` and `mario`; the conformance check takes either.
- The hosted database connection string for the check: the `DEVTHROTTLE_GATEWAY_DB_CONNECTION` key in the
  credentials file, or `evidence/pgconn.txt` (never committed).
