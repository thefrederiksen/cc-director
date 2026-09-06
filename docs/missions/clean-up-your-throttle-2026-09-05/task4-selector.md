# Phase four, task 4 - the period selector, the seven-day default, and the window on the page

**What this is.** The worker brief for phase four of "Clean up Your Throttle" (`thefrederiksen/devthrottle#2692`),
written by the phase four and five Manager on 2026-09-05. Read `state.md` and `phase3-library.md` in this
directory first, then rulings R4, R5, R9 (the settled section), R15 and R17 in `brief.md`. Everything below
is built on what phase three already put in place: the feed serves ONE figure under `throttle`, states the
window it covers, and both pages render that statement.

**Worktree:** `D:/ReposFred/devthrottle-throttle`, branch `mission/clean-up-your-throttle`. Work here and
nowhere else. Commit on this branch as you go and push; never touch `main`, never open a pull request.

**Standing requirements, from the Architect:** every change ships with a test that crosses the REAL route
(the mapped endpoint on a real `GatewayHost`; a page rendered inside a real router with the real client),
and no proof rests on a source-text surface (no test that greps a `.cs` or `.tsx` file). Write in plain
English with no abbreviations, ASCII only, and never put your name, your model or any assistant on
anything.

---

## The rulings this task lands, in one paragraph each

**R5 / R15 - the default is a rolling SEVEN days, and it lands directly.** No sequencing, no migration
note, no preserving what an existing viewer saw. The page already states its window (phase three), so the
default can change without a number quietly meaning something else. The brief's phase four caution about
"before or with" is withdrawn as a sequencing constraint; the window statement itself stands.

**R4 - a selector, and the window stated.** The person chooses the length. The selector never offers a
length the store cannot honestly answer: the ledger keeps thirty days (`ThrottleDefinition.RetentionDays`,
which is the product's own retention constant, not a number typed here), so thirty is the longest choice.

**R5 - the report's link carries its own week.** Phase five puts a link in the mentor report that opens
Your Throttle on EXACTLY the calendar week the report covered, Monday to Monday in the reader's own zone,
so following the link shows the identical number. The page therefore has to accept a week from its URL,
and the Gateway has to resolve what that week means. That URL shape is a contract between the two phases
and it is fixed here: **`/your-throttle?week=2026-W35`** on the Cockpit, and **`/throttle?week=2026-W35`**
on the phone (the Cockpit is served at the site root on the hosted Gateway; the sign-in gate already
carries the whole path including the query string in `next=`, verified against the live Gateway today:
`/your-throttle?week=2026-W35` redirects to `/signin?next=%2Fyour-throttle%3Fweek%3D2026-W35`).

**Rule 7 of CLAUDE.md - the client is dumb.** Which lengths are offered, what a week means in the
person's zone, what the window is called: all of that is decided on the Gateway and served. The client
sends what the person chose and renders what it is handed. No `.tsx` computes a date.

**Rule 8's shape, applied here - one selector on two surfaces.** The selector is ONE component in
`packages/client-core/src/stats/`, mounted by both shells. Neither shell has a selector of its own.

---

## The Gateway

`src/CcDirector.Gateway/Stats/StatsPageEndpoint.cs`, `ResolveWindow`, and the window DTO in
`src/CcDirector.Gateway/Throttle/ThrottleFigureDto.cs`.

### The query, four forms, one at a time

| query | meaning | label the Gateway serves |
|---|---|---|
| none | the default: a rolling seven days ending now. `isDefault: true` | `Last 7 days` |
| `days=N` | a rolling N days ending now. N must be one of the served choices | `Last 24 hours` for 1, else `Last N days` |
| `week=YYYY-Www` | the ISO week, Monday 00:00 to the next Monday 00:00 in the CALLER'S display zone (the same `timeZone` the feed already serves for the tenant), converted to UTC | `Week 35 of 2026, Monday 24 August to Sunday 30 August (America/Toronto)` |
| `from`+`to` | explicit UTC instants, exactly as phase three built it | unchanged |

- Two forms in one request is a 400 naming both. Half a window, a window that ends before it starts, or a
  window longer than the ledger keeps: refused as today, with the reason.
- A `days` that is not one of the choices is a 400 that NAMES the choices. A `week` that is malformed is a
  400 saying what a week looks like. A week whose Monday is earlier than now minus the retention is a 400
  saying the ledger keeps thirty days. A week whose Monday is after now is a 400. A week still in progress
  is served (the window ends at the next Monday, which is after now; the record simply stops at now).
- Add `ThrottleDefinition.DefaultWindowDays = 7` beside `RetentionDays`, with the R5 reasoning in its
  comment. The thirty-day choice is `RetentionDays`, never a literal.
- The IANA zone to `TimeZoneInfo`: `TimeZoneInfo.FindSystemTimeZoneById` handles IANA ids on .NET 6 and
  later; `Reports/MorningReportWindow.cs` already does it this way. A zone the runtime does not know is a
  loud failure, not a fallback to UTC.

### What the window DTO carries

Extend `ThrottleWindowDto`:

- `kind`: `default` | `days` | `week` | `explicit`.
- `days`: the length when kind is `default` or `days` (7 for the default), else null.
- `week`: the ISO week label when kind is `week`, else null.
- `choices`: the selector's options, served on EVERY answer, in order:
  `[{days:1,label:"Last 24 hours"},{days:7,label:"Last 7 days"},{days:14,label:"Last 14 days"},{days:30,label:"Last 30 days"}]`,
  the last derived from `RetentionDays`.
- `label`, `fromUtc`, `toUtc`, `isDefault` as before.

`tools/throttle-conformance/Program.cs` sets `IsDefault` and `Label` on the figure it prints; make it set
`Kind = explicit` too, so the JSON it prints is the same shape the feed serves. While you are in that file,
change its header comment: it is the library's command line, run by the conformance check AND, from phase
five, by the mentor report itself (the report asks this tool for its figure). Say so in the README beside it
as well. Do not rename the project.

### The hourly series

The client's "last 24 hours" charts currently take their 24 keys from `now`. With a week from the past
selected they would draw nothing and read as broken. Make the client take the 24 hours ending at the served
window's `toUtc` (clamped to now when the window ends in the future), and let the chart heading say which
24 hours it shows. `last24HourKeys` in `statsClient.ts` is where the keys are made; give it the end instant.

### Tests on the Gateway

- `src/CcDirector.Gateway.UnitTests/Stats/StatsPageWindowTests.cs`: the default pin becomes seven days and
  `Last 7 days`; the four forms; every refusal above, each asserting on the reason's wording; the week
  resolution for `America/Toronto` (`2026-W35` is `2026-08-24T04:00:00Z` to `2026-08-31T04:00:00Z`) and
  for a zone with no daylight saving; the choices, with the last equal to `RetentionDays`.
- `src/CcDirector.Gateway.Tests/Throttle/ThrottleFeedReadsTheLedgerTests.cs` (hosted `GatewayHost`, real
  device keys, rows through `POST /activity-events/batch`, the feed through the real auth gate): the
  default answer's window is seven days with `kind: default` and the choices; `?days=14` is honoured and
  `?days=9` refused naming the choices; `?week=` answers the Monday-to-Monday bounds in the tenant's zone
  with the Gateway's label, counting only the rows inside them; a week older than the ledger keeps is
  refused; two forms at once is refused. If a route exists that sets a tenant's display zone, set it through
  that route; if not, use the process default and say so in the test's comment.

## The client

`packages/client-core/src/stats/statsClient.ts`:

- `ThrottleWindow` gains `kind`, `days`, `week`, `choices` (typed, read from the served JSON, with the
  same refuse-to-guess parsing the file already does).
- `getThrottle(signal, request?)` where `request` is `{ days: number } | { week: string } |
  { fromUtc, toUtc }`, sent as the matching query.
- `last24HourKeys(endUtc)` as above.

`packages/client-core/src/stats/ThrottleWindowSelector.tsx` (new, shared): renders the served `choices`
as a row of buttons, marks the one in effect (the served window's `days`), and when the served window is
a `week` shows that week as the selected item using the Gateway's label. It calls back with the choice.
Style it with the `settings-*` pattern in mind: the shared CSS lives once in client-core, and the phone
re-tunes it for touch under `.screen`.

Both pages read their window from the URL with `useSearchParams` (`week` or `days`), pass it to
`getThrottle`, and write a selection back to the URL. That is what makes phase five's link work: a hard
navigation to `/your-throttle?week=2026-W35` (or the phone's `/throttle?week=2026-W35`) asks the Gateway
for that week and shows the Gateway's label. A URL with neither asks for the default. Both pages keep
auto-refreshing on the same window.

Both `WindowStatement` (Cockpit) and `WindowNote` (phone) keep rendering the Gateway's label verbatim.

### Tests on the client

- `packages/client-core/src/stats/statsClient.test.ts`: the three request shapes produce the right
  queries; `choices` and `kind` parse; a served answer without `choices` is refused, not defaulted.
- A test for the selector: renders the served choices, marks the one in effect, marks a served week, calls
  back with the chosen days.
- `apps/cockpit/src/throttle/YourThrottleView.test.tsx` (new): the view inside a `MemoryRouter` at
  `/your-throttle?week=2026-W35` with `fetch` mocked - asserts the request the view made carries
  `week=2026-W35`, the page shows the served label verbatim, and clicking a choice re-requests with
  `days=N` and puts `days=N` in the URL.
- `apps/mobile/src/pages/YourThrottle.test.tsx` (new): the same at `/throttle?week=2026-W35`.

## The generated client types

`packages/client-core/src/api/schema.ts` is produced by `npm run gen:api` in `packages/client-core` from
a RUNNING Gateway on port 7878. Phase three could not regenerate it. Do it now: start a Gateway from THIS
branch on a port that is free (7878 is the old build; `src/CcDirector.Gateway/Program.cs` forwards
`--port`), point `openapi-typescript` at that port's `/openapi/v1.json`, commit the regenerated file, and
stop the Gateway you started (it is your own process, not a Director; stopping it is fine). Read the diff:
it should be the stats route and whatever else this branch changed since the file was last generated. If
the diff carries something you cannot explain, say so in your report rather than committing it blind.

## Proofs

1. `.\scripts\test-local.ps1` green (the default gate). Run the affected parked class by name:
   `dotnet test src\CcDirector.Gateway.Tests --filter "FullyQualifiedName~ThrottleFeedReadsTheLedgerTests"`
   (it takes the machine-wide Gateway lock; if it says WAITING, that is the queue, not a hang).
2. The four web workspaces' tests and lint on the changed files (client-core, cockpit, mobile, cc-assistant).
3. **Mutation, watched and written down:** set the default back to thirty days and name every test that
   goes red; remove the `week` resolution (make it answer the default) and name every test that goes red,
   on the Gateway AND on the client; restore, all green. A guard that was never watched failing is
   decoration.
4. The phase three note lists `StatsPageWindowTests` at seven tests; your report gives the new count.

## Out of scope - do not do these

- Anything in the internal repository (phase five's worker owns it).
- The mentor report's link itself. You fix the URL contract; phase five writes the sentence.
- Character volume, the wingman ring, the tally: settled in phase three, not reopened.
- A length beyond thirty days, or an "all time" option.

## Report

When pushed, tell the Manager (session `7d0d4251`, "Clean up Your Throttle - Manager") in ONE line:
`cc-devthrottle message send 7d0d4251 "<one line: phase four pushed at <sha>; tests <n> added; mutation
red list <names>; what is NOT proven>"`. Put the long version in
`docs/missions/clean-up-your-throttle-2026-09-05/phase4-selector.md` (what was built, the decisions, the
mutation table, what is not proven), committed with the code.
