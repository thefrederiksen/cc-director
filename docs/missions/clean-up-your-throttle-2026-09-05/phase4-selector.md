# Phase four - the period selector, the seven-day default, and the window on the page

**What this is.** The record of phase four of "Clean up Your Throttle" (`thefrederiksen/devthrottle#2692`):
the period selector, the default moved to a rolling seven days, the week form of the window that the
mentor report's link will carry, and the regenerated client types. Built on rulings R4, R5, R9 (settled),
R15 and R17, over what phase three put in place (one figure under `throttle`, the window stated on every
answer, both pages rendering that statement).

**Written:** 2026-09-05, by the phase four worker. **Branch:** `mission/clean-up-your-throttle`.

---

## What was built

### The Gateway - `GET /stats/data` takes one of four windows

`src/CcDirector.Gateway/Stats/StatsPageEndpoint.cs`, `ResolveWindow`, now takes `from`, `to`, `days`,
`week` and the caller's display zone:

| query | window | label served |
|---|---|---|
| none | a rolling seven days ending now, `isDefault: true`, `kind: default`, `days: 7` | `Last 7 days` |
| `days=N` | a rolling N days ending now, `kind: days`; N must be one of the served choices | `Last 24 hours` for 1, else `Last N days` |
| `week=YYYY-Www` | the ISO week, Monday 00:00 to the next Monday 00:00 in the tenant's display zone, converted to UTC; `kind: week`, `week: "2026-W35"` | `Week 35 of 2026, Monday 24 August to Sunday 30 August (America/Toronto)` |
| `from` and `to` | explicit UTC instants, unchanged from phase three; `kind: explicit` | the dates, as before |

Refusals, each a 400 with its reason: two forms at once (naming both); a `days` that is not a choice
(naming the choices, `1, 7, 14, 30`); a malformed `week` (saying what a week looks like); a week whose
Monday is before now minus the retention (saying the ledger keeps thirty days); a week whose Monday is after
now. A week still in progress is served, ending at the next Monday, and the record simply stops at now.
Half a window, a window that ends before it starts, and a window longer than the ledger keeps are refused
as before.

The display zone is read for the caller's tenant before the window is resolved (the same `timeZone` the
feed already serves), and turned into a `TimeZoneInfo` with `FindSystemTimeZoneById`. A zone the runtime
does not know throws - a loud failure, never a fallback to UTC. The Monday midnight is resolved by the
same rule the morning report uses (`Reports/MorningReportWindow.StartOfLocalDayUtc`, made internal for this
one caller) so a skipped or repeated midnight is handled once, not twice.

Two new constants beside the retention in `ThrottleDefinition`: `DefaultWindowDays = 7`, with the R5
reasoning in its comment, and the choices in `Throttle/ThrottleWindowChoices.cs` - `1, 7, 14,
RetentionDays`, the last derived from the retention sweep and never typed. `ThrottleWindowKinds` names the
four kinds.

### The window DTO

`ThrottleWindowDto` gains `kind`, `days`, `week` and `choices`
(`[{days:1,label:"Last 24 hours"},{days:7,label:"Last 7 days"},{days:14,label:"Last 14 days"},{days:30,label:"Last 30 days"}]`),
served on every answer. `label`, `fromUtc`, `toUtc` and `isDefault` are as before.

`tools/throttle-conformance/Program.cs` sets `Kind = explicit` and the choices on the figure it prints, so
its JSON is the shape the feed serves. Its header comment and the README beside it now say it is the
library's command line with two callers: the conformance check, and from phase five the mentor report
itself. The project is not renamed.

### The client

- `packages/client-core/src/stats/statsClient.ts`: `ThrottleWindow` carries `kind`, `days`, `week`,
  `choices`. `getThrottle(signal, request?)` takes `{ days } | { week } | { fromUtc, toUtc }` and sends the
  matching query (`throttleWindowQuery`). `throttleWindowFromSearch` reads `week` or `days` from a page's
  URL. A served window WITHOUT `choices`, with a malformed choice, or with a kind this client does not know
  is refused with an error the page shows - never defaulted into a list the Gateway did not serve.
  `last24HourKeys(endUtc)` takes its end instant; `hourlyChartEnd(window, now)` is the served window's end,
  clamped to now.
- `packages/client-core/src/stats/ThrottleWindowSelector.tsx` (new, shared, with `throttleWindow.css`
  beside it): renders the served choices as a row of buttons, marks the one in effect from the served
  window's `days`, and when the served window is a week shows that week as the selected item under the
  Gateway's label with no length marked. It calls back with the chosen days. The phone re-tunes the
  sizing for touch under `.screen` in its own stylesheet, exactly as the shared Settings cards are
  re-tuned. Exported from the package as `./stats/ThrottleWindowSelector`.
- Both pages (`apps/cockpit/src/throttle/YourThrottleView.tsx`, `apps/mobile/src/pages/YourThrottle.tsx`)
  read their window from the URL with `useSearchParams`, pass it to `getThrottle`, write a choice back to
  the URL as `days=N`, keep auto-refreshing on the same window, and go back to the loading state the
  moment the window changes so the old window's numbers never sit under the new selection. Neither page
  computes a date. `WindowStatement` and `WindowNote` render the Gateway's label verbatim, as before.
- The Cockpit's two 24-hour charts end at the served window's end (clamped to now) rather than at the
  clock, and their headings say which 24 hours they show ("Turns per hour (24 hours to 31 Aug 2026,
  00:00)"). With a past week selected they used to draw nothing.

### The URL contract with phase five

Fixed here, as the brief states it: **`/your-throttle?week=2026-W35`** on the Cockpit and
**`/throttle?week=2026-W35`** on the phone. A hard navigation to either asks the Gateway for that week and
shows the Gateway's label for it. Proven by the two router tests below, each rendering the real page in a
`MemoryRouter` at that URL against a stubbed Gateway and asserting the request the page made.

### The generated client types

`packages/client-core/src/api/schema.ts` was regenerated from a Gateway started from this branch
(`CcDirector.Gateway.exe --port 7979` over a scratch storage root under the session's temporary
directory, its OpenAPI document read with that Gateway's own token, then the process stopped). **The diff
is large and it is explained:** the file had not been regenerated since the missions-flat change
(`32286762f`), so it carries every route main added or retired since then - 21 routes gone (car mode
diagnostics, the brain, the wingman instruction records, turn briefs, explain, autostart, addressing mode)
and 104 added (governance, skills, workflows, work history, dictionary ingest, rules, the ledger ingress,
voice quality, the browser routes, and more) - plus the stats route's new `days` and `week` query
parameters. The feed's body is an anonymous object, so the window DTO has no named schema entry, exactly
as before; the statistics client does not read the generated types for this route. All four web
workspaces type-check against the regenerated file.

## Decisions taken in this phase, for the Architect

1. **A week and a length are the only two things the URL carries.** `from` and `to` stay on the feed for
   the conformance check and any explicit caller, but neither page reads them from its URL: the two
   consumers of the page are a person choosing a length and the report's link naming a week.
2. **A served week marks no length.** The selector shows the week as its own selected item under the
   Gateway's label; choosing a length is how a person leaves the report's week. The alternative - marking
   the seven-day button because a week is seven days long - would say the page is showing the last seven
   days when it is not.
3. **A window change resets the page to loading.** The old window's numbers are never shown under the
   new selection, at the cost of a brief loading state on every choice.
4. **The choices are refused, not defaulted, when absent.** A client that filled in `1, 7, 14, 30` for
   itself would be a client ruling on which lengths the ledger can answer (rule 7).
5. **The refusal for a too-old week names the ledger's retention, not the oldest week it holds.** The
   thirty days is the fact the reader can act on; the oldest servable week changes every day.

## Tests

| where | count | what |
|---|---:|---|
| `Gateway.UnitTests/Stats/StatsPageWindowTests` | **37** (was 7) | the seven-day default; the choices on every form with the last equal to the retention; each served length; a length that is not a choice, five ways; the Toronto week (`2026-W35` is `2026-08-24T04:00:00Z` to `2026-08-31T04:00:00Z`) and its label; a zone with no daylight saving (Tokyo); a week in progress; eight malformed weeks; two weeks older than the ledger keeps and one it still holds; a week after now; an unknown zone throwing; two forms at once, three ways; the explicit form and its three refusals as before |
| `Gateway.Tests/Throttle/ThrottleFeedReadsTheLedgerTests` | **10** (was 6) | hosted `GatewayHost`, real device keys, rows through `POST /activity-events/batch`, the feed through the real auth gate: the default is seven days with `kind: default` and the choices (a row eight days old is outside it); `?days=14` honoured and `?days=9` refused naming the choices; `?week=` with the tenant's zone set to `America/Toronto` through the real `PUT /gateway/time-zone` - the Monday-to-Monday bounds, the Gateway's label, and only the two rows inside them counted of four placed one minute either side of each bound; a week eight weeks old refused, two forms refused naming both, a malformed week refused |
| `client-core/stats/statsClient.test.ts` | 26 (was 16) | the three request shapes and the default produce the four queries, through a stubbed `fetch`; `kind`, `week`, `days` and `choices` parse as served; an answer without `choices` is refused; an unknown kind and a malformed choice are refused; the self-host sentence passes through; `throttleWindowFromSearch`; `hourlyChartEnd` |
| `client-core/stats/ThrottleWindowSelector.test.tsx` | 5 (new) | renders the served choices in order, marks the one in effect, marks a served week with no length, offers only what was served, calls back with the days |
| `cockpit/throttle/YourThrottleView.test.tsx` | 3 (new) | the real view in a `MemoryRouter` at `/your-throttle?week=2026-W35` with `fetch` stubbed: the request carries `week=2026-W35`, the page shows the served label verbatim, the selector shows the week; clicking `Last 14 days` re-requests with `days=14` and puts `days=14` in the URL; a URL with neither asks for the default |
| `mobile/pages/YourThrottle.test.tsx` | 3 (new) | the same at `/throttle?week=2026-W35` |

Tests added: 30 Gateway unit facts, 4 hosted facts, 10 client facts, 5 selector facts, 6 page facts -
**55 added**. No test greps a source file.

## Mutation, watched and written down

| mutation | red on the Gateway | red on the client | restored |
|---|---|---|---|
| `DefaultWindowDays = 30` (the default back to thirty days) | unit: `StatsPageWindowTests.NoWindowAsked_AnswersARollingSevenDays_AndSaysSo`; hosted: `The_default_window_is_a_rolling_seven_days_and_carries_the_choices`, `The_feed_serves_the_ledger_figure_for_the_callers_own_tenant_and_nobody_elses` (1 of 272 unit tests under the Stats and Throttle namespaces; 2 of 10 hosted) | none, and none expected: the default is a Gateway fact and the client tests stub the Gateway. No client test pins seven. | 37 of 37, 10 of 10 |
| `week=` answers the default (the week branch of `ResolveWindow` returns the no-window answer) | unit: 15 of 37 - `AWeek_IsMondayToMonday_InTheCallersZone_ConvertedToUtc`, `AWeek_InAZoneWithNoDaylightSaving_IsThatZonesMidnight`, `AWeekStillInProgress_IsServed_EndingAtTheNextMonday`, all eight `AMalformedWeek_IsRefused_SayingWhatAWeekLooksLike` cases, both `AWeekOlderThanTheLedgerKeeps_IsRefused_SayingTheLedgerKeepsThirtyDays` cases, `AWeekAfterNow_IsRefused`, `AZoneTheRuntimeDoesNotKnow_IsALoudFailure_NotAFallbackToUtc`; hosted: 2 of 10 - `A_week_is_Monday_to_Monday_in_the_tenants_own_zone_counting_only_the_rows_inside_it`, `A_week_older_than_the_ledger_keeps_is_refused_and_so_are_two_forms_at_once` | not run against this mutation (the client cannot see it; its own mutation is the next row) | 37 of 37, 10 of 10 |
| the page ignores a week in its URL (`throttleWindowFromSearch` drops the `week` branch, so the page asks for the default) | not applicable | client-core: `throttleWindowFromSearch > reads a week, else a length, else nothing` (1 of 31); cockpit: both link tests - `asks the Gateway for exactly the week in the URL...` and `choosing a length re-asks the Gateway with days=N...` (2 of 11); mobile: the same two (2 of 3) | 31 of 31, 11 of 11, 3 of 3 |

Every guard above was watched failing before it was trusted.

## Results

- **The default gate** (`.\scripts\test-local.ps1`, run once over the finished code, before the schema
  commit): nine projects, all green except ONE test in `cc-director-setup-engine.Tests` -
  `GatewayAccountEnrollRunnerTests.EverySignInCancelledMessage_StatesTheFact_AndNamesNoButton`, failing with
  "DEVTHROTTLE_HOSTED_GATEWAY_URL is set to ...". That variable is not set in any shell here (checked from
  both PowerShell and bash); it is set process-wide by `HostedGatewayUrlOverride` in
  `GatewayHostedEnrollRunnerTests`, which runs inside the xunit collection `hosted-gateway-url`, while the
  failing class is OUTSIDE that collection and so runs in parallel with it and can observe the variable
  mid-scope. A pre-existing parallel race in the installer engine's tests, untouched by this branch; the
  project re-run on its own is 456 of 456 green, twice. Not fixed here (out of this phase's scope); named
  so nobody chases it as this branch's red.
- **The parked class by name:** `ThrottleFeedReadsTheLedgerTests` 10 of 10, four times over the day (the
  lock was uncontended each time).
- **The four web workspaces:** client-core 93 files, 999 tests; cockpit 33 files, 287 tests; mobile 3
  files, 18 tests; cc-assistant 8 files, 106 tests - all green. `tsc --noEmit` clean in all four, before
  and after the schema regeneration. `eslint` clean on every changed file and on the regenerated schema.

## What this phase did NOT prove, said plainly

- **The link itself is not written.** Phase five puts the sentence in the mentor report; this phase fixed
  the URL it will carry and proved both pages honour it. That a real report's link, followed through the
  hosted sign-in gate, lands on the week is not exercised here - the sign-in redirect carrying the query
  was verified live by the Manager, not by a test in this repository.
- **The week tests use a real zone but not a real daylight-saving boundary week.** Toronto and Tokyo
  are proven at fixed offsets; a week whose Monday midnight is skipped or repeated relies on the morning
  report's shared midnight rule, which has its own tests, not on a week test of its own.
- **The hosted week test computes its expected bounds with `TimeZoneInfo.ConvertTimeToUtc`,** the same
  runtime the Gateway uses. It proves the route resolves the tenant's zone through the real settings route
  and counts only the rows inside the bounds; it is not an independent oracle for the conversion itself.
  The unit test's fixed instants (`2026-08-24T04:00:00Z`) are that oracle.
- **The pages were not driven in a browser.** Both are proven inside a router with `fetch` stubbed, which
  is what the brief asked for; the live Gateway still runs the old feed until this lands and deploys, and
  the selector has not been looked at on a phone screen.
- **The default gate's one red is exonerated by mechanism and by re-run, not by a fix.**
- **The regenerated schema carries main's route churn since `32286762f`,** explained above route by
  route in shape but not audited entry by entry; nothing in this repository reads the generated types for
  the stats route.
