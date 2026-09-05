# Phase five, task 5 - the report ASKS the library for its figure, then links to Your Throttle

**What this is.** The worker brief for phase five of "Clean up Your Throttle"
(`thefrederiksen/devthrottle_internal#1680`, and ruling R3), written by the phase four and five Manager on
2026-09-05. Read `state.md`, `phase3-library.md` and `reconciliation.md` in this directory first (they are
in the PRODUCT repository's mission folder, `D:/ReposFred/devthrottle-throttle/docs/missions/clean-up-your-throttle-2026-09-05/`),
then rulings R3, R5, R7, R9 (the settled section), R16 and R17 in `brief.md`.

**Worktree:** `D:/ReposFred/devthrottle_internal-throttle`, branch `mission/clean-up-your-throttle`
(fast-forwarded to `origin/main` at `cb50de1` today). Work here and nowhere else. Commit on this branch as
you go and push; never touch `main`, never open a pull request. The harness is `tools/mentor/`.

**Standing requirements, from the Architect:** every change ships with a test that crosses the REAL
route, and no proof rests on a source-text surface. Write in plain English with no abbreviations, ASCII
only, and never put your name, your model or any assistant on anything.

---

## Why this phase is the one the mission is FOR

The owner's words: *the report should be using Your Throttle as a library, so we need a Your Throttle
library that is reusable and consistently correct.* Phase three built the library
(`src/CcDirector.Gateway/Throttle/` in the product) and proved that it and the harness's own reading of
the ledger AGREE over real weeks (`evidence/conformance/`: soren W35 1,786 turns, 56.83 per cent spoken on
both sides). But the report still computes its own ring: `metrics.py` derives `prompt_shape.modality_share`
and `prompt_shape.surface_share` from the prompt log through `origin.py`, and `render_report.py` draws the
rings and the bars from those. That is population A of `reconciliation.md` (58.76 per cent for W35), not
the library's figure (56.83). Until the report asks the library, R3 is unmet and the two numbers are not
one number. **Part one below is that. Do it before the link.**

## Part one - the report consumes the library

### How the report reaches the library

The harness has no device key and no HTTP route to a person's Your Throttle; it reads the hosted database
with one read-only connection string for both accounts (`credentials_env` / `db_connection_key` in
`config.json`). The library already has a command-line face that runs the Gateway's OWN code against that
database: `tools/throttle-conformance/` in the product repository (`Program.cs` runs
`ThrottleLedgerReader` and `ThrottleDefinition`, the code behind `GET /stats/data`, and prints the figure
as the same JSON the feed serves under `throttle`). `conformance.py` beside it shows exactly how to call
it: `dotnet <dll> --tenant <id> --from <iso-utc> --to <iso-utc> --connection <string> --out <file>`,
building the project first when the dll is absent. **The report asks THAT.** It is the library, byte for
byte the code the page runs; it needs no new credential; and it does not wait on the deploy.

### Configuration

`config.json` gains two keys, both validated in `common.load_config` with the same exit-1-naming-the-fix
manner as the rest, and both added to `config.example.json` and `README.md`:

- `throttle_library`: the absolute path of the product repository's `tools/throttle-conformance`
  directory. It must exist and hold `ThrottleConformance.csproj`. **For your runs use the mission
  worktree's copy, `D:/ReposFred/devthrottle-throttle/tools/throttle-conformance`** - the tool exists only
  on the mission branch until the Architect lands it; after landing the value becomes the main checkout's
  `D:/ReposFred/devthrottle/tools/throttle-conformance`. Say this in the README and in your report.
- `gateway_public_url`: the hosted Gateway's public base, `https://gateway.devthrottle.com` (the product
  reads the same value from its `CC_GATEWAY_PUBLIC_URL`). Must start with `https://`, no trailing slash.

The real `config.json` is gitignored and lives in the MAIN checkout, `D:/ReposFred/devthrottle_internal/tools/mentor/config.json`
(the worktree has none; every harness command takes `--config <that path>`). Add the two keys there for
your evidence run.

### The new step - `tools/mentor/throttle.py`

`ask_library(cfg, account, iso_week)` returns the library's figure for the account's week:

- the window is `common.week_bounds(iso_week, account["time_zone"])` - Monday 00:00 to the next Monday
  00:00 in the account's own zone, the mentor's own week bounds - converted to UTC ISO instants;
- the connection string is read from the credentials file AT THE MOMENT OF USE (`common.read_credential`)
  and never printed, logged or written; the tool's stderr summary line is printed, the connection is not;
- the tool runs in the FOREGROUND through `subprocess.run` (never detached), building first when the dll
  is absent, exactly as `conformance.py` does; a non-zero exit fails naming the tool and its stderr;
- the answer is parsed and CHECKED: `turns`, `voiceTurns`, `typedTurns`, `sessions`, `buckets`,
  `excluded` (all four counts), `agentDrivenTurns`, `definition` (a non-empty sentence), `window`
  (`fromUtc`, `toUtc`), `ledger` (`retentionDays`, `earliestUtc`) must be present and of the right kind;
  anything missing fails naming the key. No defaults.

### The metrics document - a `throttle` group, and the second implementation DELETED

In `metrics.py`, a new group `throttle`, FIRST in `GROUP_ORDER` (it is the headline), with these
metrics in the `DEFINITIONS` table (unit, source, definition sentence each, in the table's own style):

- `throttle.turns`: `{counted, voice, typed, sessions}` - the library's `turns`, `voiceTurns`,
  `typedTurns`, `sessions`.
- `throttle.modality_share`: `{voice, typed}` over counted turns (zero counted turns: shares null, never
  zero - the same rule `share()` already applies).
- `throttle.turns_by_surface`: `{desktop, phone, cockpit, unknown}` counts from the buckets. `unknown` is
  any surface not one of the three; if a bucket carries a surface name outside those four the run fails
  naming it - a new surface is a product change the report must not fold quietly.
- `throttle.surface_share`: the same four, as shares of counted turns.
- `throttle.excluded`: `{no_input_origin, agent_driven, framework, unresolved}` - the R17 population and
  its split, verbatim from the library.
- `throttle.agent_driven_turns`: the turns the fleet drove into the person's own sessions.
- `throttle.definition`: the library's `definition` sentence, verbatim (a string: no baseline).
- `throttle.window`: `{from_utc, to_utc, label}` from the library's answer (no baseline).

The source of every one is a NEW source name, `throttle_library` ("the Gateway's submission ledger, read
live through the product's own Your Throttle library"). Its coverage for baselines: a prior week feeds a
baseline only when the library's own `ledger.earliestUtc` is at or before that week's start and the
week's end is at or before the moment asked - the library says where the record begins, so use what it
says, through the same `source_covers` machinery (`World.source_start` / `source_end` for the new source
name). The ledger keeps thirty days, so at most three complete prior weeks can ever feed it; the README
already says as much for `activity_events`. Baselines for the throttle metrics come from asking the
library for each eligible prior week, the same way `compute_all` runs over prior weeks today.

**DELETE `prompt_shape.modality_share` and `prompt_shape.surface_share`.** They are the second
implementation. Every reader moves to `throttle.*`: `render_report.py` (`_share_bars`, `_rings`, the
counts line, the text email's bar lines), `average.py` (`MODALITY_PATH`, `SURFACE_PATH` and its
docstring), the tests, and anything in `packet.py`, `contract.py`, `check_call.py` or `prompts/` that
names them (grep; today only `average.py`, `metrics.py`, `render_report.py` and the tests do). The other
"human prompt" metrics (rhythm, prompt shape, outcomes, voice words, origin, repos) rest on the prompt
log's classification and are out of scope - they are the prose's evidence, not the figure.

**Block 3 of the page ("Prompts by origin") may not carry a second spoken-against-typed count.** R9's
settled instruction 2: no two numbers on the page may come from different substrates without the page
saying so. Today `ORIGIN_LABELS` draws `human.by_modality.typed` and `human.by_modality.voice` as two
bars, and those are prompt-log counts (929 spoken, 652 typed for W35) that sit two screens below rings
now reading the ledger's (1,015 and 771). Collapse the two human bars into ONE bar, "you" (the human
count), keep agent, framework and unresolved, and add one sentence under the block saying what it is:
this block classifies the week's user records in the transcripts; the rings above count submissions in
the Gateway's ledger, which is the same figure Your Throttle shows. Adjust the baseline handling for the
fixed categories accordingly (`_origin_bars` insists every fixed category is present).

`refuse_population_counts`, `refuse_cadence`, `refuse_provider_names`, `refuse_workings_in_rings` and
`refuse_identifiers` all run at render time; every new sentence must pass them. In particular: no
cadence word ("weekly", "each week", "next week"); no number beside a plural population word; nothing
resembling a session id.

### The drift check - `tools/mentor/verify_throttle.py`

The mission's promise is *a check fails if they ever drift apart again*. Phase three's conformance check
compared the library with the harness's reader; after this phase the report's figure IS the library's,
so the check that matters is: **the figure the report was rendered from equals what the library answers
for the same window, now.** `verify_throttle.py --week --account --config` re-asks the library for the
account's week, compares it with `metrics.json`'s `throttle` block (turns, voice, typed, sessions, every
bucket, every excluded count), and then reads the rendered page (`report-<label>-<week>.html`) and
asserts the FIRST ring's percentage is the rounded spoken share of that block (the same rounding
`render_report.pct` uses) and the second ring's is the phone share. Exit 1 naming every difference. Add
it to `run_report.py`'s chain immediately before `send_report`, so no report can be sent whose figure is
not the library's figure. Its own tests: agreement passes; a planted difference in `metrics.json` is
named; a page whose ring disagrees is named.

### Tests (pytest, `tools/mentor/tests/`)

- `test_throttle.py`: the instants sent to the tool are the account's local Monday-to-Monday in UTC
  (Toronto W35 is `2026-08-24T04:00:00Z` to `2026-08-31T04:00:00Z`); the tool is run in the foreground
  with the tenant id and the window and never with the connection string on stdout; a non-zero exit fails
  naming the tool; a missing key in the answer fails naming it; the figure maps to the metrics (shares are
  voice over counted turns; a bucket with an unknown surface fails naming it).
- The `make_world` / `built` fixtures in `conftest.py` and `test_render_report.py` run `metrics.py` over
  a fake world. The library is a process boundary, so the fixture supplies it at that seam: monkeypatch
  `throttle.ask_library` (the way `fake_gateway` supplies the database) with a recorded real answer -
  the shape of the library's JSON, trimmed - and the prior-week answers. That is a test double at a
  process boundary, not a fallback in the code; the code path from the answer to the page is real.
- `test_render_report.py`: the rings, the bars, the counts line and the text email read `throttle.*`;
  with `throttle.modality_share.voice` = 0.5683 the first ring says 57 and the email headline says
  "57% spoken, not typed"; a `prompt_shape.modality_share` planted back into the document changes NOTHING
  on the page (the second implementation has no reader left); a document without `throttle` names the
  path. Block 3 has one "you" bar and the sentence.
- `test_average.py`: the paths moved.
- `test_run_report.py`: the chain runs `verify_throttle` before `send_report`, and a drift stops the
  chain before anything is sent.

## Part two - the link (issue #1680, ruling R5)

One sentence, in the place the two figures are drawn, pointing at the reader's own Your Throttle on the
Gateway, behind the sign-in that already gates everything about them. Never a public or signed link:
no token, no tenant id, no email address in the URL. The link opens the page on the week the report
covers.

- `common.your_throttle_link(cfg, iso_week)` returns
  `<gateway_public_url>/your-throttle?week=<iso_week>` - for W35, `https://gateway.devthrottle.com/your-throttle?week=2026-W35`.
  This URL shape is the contract phase four fixes on the product side: the Cockpit is served at the site
  root, reads `week` from its URL, asks the Gateway for that ISO week in the reader's own zone, and the
  sign-in gate carries the whole path including the query through `next=` (verified against the live
  Gateway today). Do not link to the phone surface; the Cockpit page holds the detail behind the figures,
  which is what the sentence promises.
- **On the page:** directly beneath the ring row (the rings ARE the two figures), outside the ring
  cards - `refuse_workings_in_rings` refuses extra sentences INSIDE a card, and rightly. One sentence,
  the link as the page's own accent link style. Not in the footer.
- **In the email, both parts:** beneath the two headline figures in the drive block - after the counts
  and average sentences in `drive_charts(..., for_email=True)` for the HTML part, and the same position
  in `email_text`, with the bare URL on its own line so a plain-text reader can copy it.
- The wording is yours, in the report's own voice, but it must survive the cadence guard (no "weekly",
  "each week", "next week") and must say two things: that the detail behind these two figures is on the
  reader's own Your Throttle, and that it opens on this week. Something like: *The detail behind these two
  figures is on your own Your Throttle page, behind your sign-in, opened on this week: <link>.*

Tests: the page carries the sentence exactly once, beneath the ring row and not inside a card, not in
the footer; the HTML email carries it once; the text email carries the bare URL once; the URL's week is
the report's week and its host is the configured one; the URL carries no `@`, no token and no tenant id;
a config without `gateway_public_url` exits 1 naming the key.

## The evidence run - the real route, over a real week

Both accounts have `2026-W35` extracted and derived on disk (`data_root` in the main checkout's config).
Do NOT extract again (the share and the database are the owner's live data; the extracts on disk are the
week). Run, with `--config D:/ReposFred/devthrottle_internal/tools/mentor/config.json`:

1. `python metrics.py --week 2026-W35 --account soren` - asks the library live for W35 and the prior
   weeks, writes `metrics.json` with the `throttle` group.
2. `python average.py --week 2026-W35` (needs both accounts' metrics: run metrics for mario too).
3. `python render_report.py --week 2026-W35 --account soren` (the report.md, the call and its proof
   record exist for W35 in the derived folder).
4. `python verify_throttle.py --week 2026-W35 --account soren`.

**Never run `send_report.py` or `run_report.py`.** They email the owner. The chain change in
`run_report.py` is proven by its test, not by a live send.

Then record, in `D:/ReposFred/devthrottle_internal-throttle/docs/missions/clean-up-your-throttle-2026-09-05/phase5-report-consumes-the-library.md`
(the internal repository's own mission folder; create it): the library's answer for W35 (turns, voice,
typed, spoken share), the number the rendered page's first ring prints, the number the email headline
prints, and the phase three conformance figure for the same week (1,786 turns, 56.83 per cent) - they must
all be one number. Also what the report USED to say for W35 (58.76, from `reconciliation.md`), so the
change is disclosed once, going forward. Save the `verify_throttle` output and the rendered page's ring
row beside it. Nothing with a tenant id, an email address or a connection string goes into the repository.

**Mutation, watched and written down:** make `ask_library` return a figure with `voiceTurns` doubled and
name every test that goes red; plant a difference into `metrics.json` and show `verify_throttle` naming
it; restore, all green. `python -m pytest tests -q` must be green at the end (768 tests today; your report
gives the new count).

## Out of scope - do not do these

- The product repository. If phase four's worker changes the URL contract you will hear from the Manager;
  otherwise it is `/your-throttle?week=YYYY-Www`.
- The report's prose, rubric, spoken call and video. The other human-prompt metrics.
- Restating past weeks' published figures. The change is disclosed once, going forward.
- Any public or signed link.

## Report

When pushed, tell the Manager (session `7d0d4251`, "Clean up Your Throttle - Manager") in ONE line:
`cc-devthrottle message send 7d0d4251 "<one line: phase five pushed at <sha>; W35 library/ring/email
numbers; tests <n>; mutation red list; what is NOT proven>"`. The long version is the phase note above,
committed with the code.
