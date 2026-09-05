# Phase three - the library

**What this is.** The record of phase three of "Clean up Your Throttle": the one definition of how a person
drives DevThrottle, the feed that serves it, the two pages that render it, and the check that fails when
its two consumers diverge. Built on rulings R9 (the shared figure derives from the submission ledger),
R16 (character volume is dropped), R17 (the predicate is stated exactly, once) and R18 (the parked suite
runs in full first).

**Written:** 2026-09-05, by the phase three Manager. **Branch:** `mission/clean-up-your-throttle`.

---

## The first act: the parked suite, in full (R18)

`.\scripts\test-local.ps1 -Parked` was started at 11:45 local on commit `050f7174a` (the phase two close),
BEFORE any library work, and left to run while the library was read for and designed. The Gateway suite
queued 35 minutes behind another session's run of the same suite (a `testhost` from the
`devthrottle-tts-retry` worktree, holding the machine-wide lock since 10:41) and acquired the lock at 12:20.

See "Results" at the foot of this note for the outcome, recorded when the run finished.

## What was built

### One definition - `src/CcDirector.Gateway/Throttle/ThrottleDefinition.cs`

The predicate, verbatim from R17, as a constant the feed serves and a test pins:

> The shared figure is computed over activity_events rows where EventType is turn-submitted and
> InputOrigin is present, grouped by the origin's modality and surface.

`Fold` is a pure function over the narrow ledger projection (occurred-at, session, agent kind, input
origin, send source) and a session-history map. It produces every count of turns the page shows: the
modality-by-surface buckets, the hourly series, the per-agent split (the ledger carries the agent kind),
the per-repository split (through the session-history join, resolved name first, checkout folder name
second, unattributed and disclosed otherwise), the distinct sessions, and the excluded population as
counts: every row with no input origin, split into the fleet driving itself (Agent), the product's own
text (Framework), and the remainder - a person's submission nobody could place - which is what the page
discloses beside the share.

The three R17 consequences are true in the code and proven by `ThrottleDefinitionTests`:

- a turn typed at the terminal (null send source, present origin) is IN;
- agent traffic is OUT by record, and reported beside the figure from the same rows;
- a submission with no input origin is OUT and disclosed as a count.

A malformed origin token refuses the whole figure rather than guessing a bucket, which is the same answer
the mentor harness's reader gives on the same row.

### One reader - `ThrottleLedgerReader`

The only thing that feeds the fold from the store: the tenant's turn-submitted rows over a half-open
window, the oldest turn-submitted row the tenant holds (so the page can say where the record begins),
and the session-history facts for the sessions seen. It reads through a context scoped to the tenant the
ROUTE resolved, never the ambient one, and refuses a context scoped to any other tenant. A second
constructor takes a context source so the conformance check can run this same code against the hosted
database without `GatewayDatabase.Open()`, which would check for and apply migrations.

### The feed - `GET /stats/data`

- Every count of turns comes from the library, served ONCE under `throttle`. The old flattened fields the
  tally fed (`buckets`, `hourlyTurns`, `repos`, `agents`, `wingman`, `agentsSinceUtc`,
  `agentDrivenCharacters`) are gone, and so is every `characters` field (R16).
- The window is stated on every answer. `from` and `to` (ISO 8601, UTC) name one; absent, the answer is
  the ledger's whole retention - thirty days - and says `isDefault: true` with the label "Last 30 days".
  Half a window, a window that ends before it starts, or a window longer than the ledger keeps is a 400
  with the reason. Phase four changes the default to seven days and adds the selector.
- On a self-hosted Gateway the route answers 200 with `available: false` and one sentence (R1, R6):
  "Your Throttle works only on the hosted DevThrottle Gateway. This Gateway is self-hosted, so there is no
  figure to show here." Both pages render it verbatim.
- The statistics store no longer takes the page down. It still feeds concurrency, token spend and the
  per-model spend split - nothing that counts a turn - and when it has not published on hosted those
  blocks are null with `statisticsUnavailableReason` beside them, while the figure is served.
- The session-origin block now enters the resolved tenant's scope explicitly for its two history reads.
- The caveats no longer mention characters, and a fourth sentence says what is outside every number and
  where the fleet's own turns are shown.

### The two pages

`packages/client-core/src/stats/statsClient.ts` is the shared client; it now types the feed as a union -
the figure, or the sentence - and carries no character volume. Both Your Throttle pages (Cockpit
`apps/cockpit/src/throttle/`, phone `apps/mobile/src/pages/YourThrottle.tsx`) and both Repos surfaces:

- render the sentence when the Gateway has no figure;
- state the window (the Gateway's label and dates, in the display zone), and say when the record begins
  after the window opens;
- show two rings (spoken, from phone). **The wingman ring is gone** - see "Decisions" below;
- disclose beside the share how many of the person's submissions could not be placed on a surface, and
  how many turns were other sessions prompting theirs;
- rank repositories and agents by turns or sessions only. The characters metric is gone from both tabs and
  the phone page, and the Agents tab's "attributing since" caveat is gone with the tally it described - the
  per-agent split now adds up to the rings over the same window;
- the Cockpit's Breakdown tab carries the definition sentence and a table of the counted and excluded
  populations.

### The conformance check - `tools/throttle-conformance/`

`conformance.py` computes the figure twice for one account over one calendar week and fails on any
difference: the library (the Gateway's own code, run by `ThrottleConformance.csproj` against the hosted
database, read-only) against the mentor harness's own reader of the same ledger over its own extract,
with the predicate applied; plus a plain reading of the extract for the per-agent and per-repository
splits and the hourly series, which the mentor's reader does not carry. It also asserts the library
reports the R17 sentence verbatim.

Run over real weeks for both accounts on 2026-09-05 against the live hosted database
(`evidence/conformance/`):

| account | week | library turns | mentor-side turns | spoken share, both | unresolved, both | verdict |
|---|---|---:|---:|---:|---:|---|
| soren | 2026-W35 | 1,786 | 1,786 | 56.83% | 502 | PASS |
| soren | 2026-W34 | 920 | 920 | 53.26% | 822 | PASS |
| mario | 2026-W35 | 217 | 217 | 1.38% | 758 | PASS |
| mario | 2026-W34 | 167 | 167 | 0.00% | 367 | PASS |

Soren's W35 lands exactly on phase one's population B (1,015 voice, 771 typed, 502 unresolved, 160
framework) - the number the mentor report was reconciled against. Every bucket, every excluded count,
five agents, thirteen repositories and 110 hours agreed.

**The check was run against a known-bad input.** `--break-predicate` makes the mentor side drop the
null-send-source rows - exactly defect one - and the check went red with the defect's shape: typed 771
against 177, turns 1,786 against 1,192, seven named differences, exit 1
(`evidence/conformance/conf-soren-W35-broken.md`).

## Guards, and the proof each can fail

- `ThrottleDefinitionTests` (14) - the predicate text, the three consequences, membership decided by the
  origin alone across all five send sources, the half-open window, the hour keys, the repository join,
  the agent split, the malformed-origin refusal, and the W35 shape reproducing phase one's figure.
- `ThrottleLedgerReaderTests` (4) - over the real EF store on SQLite: tenant scoping (a second tenant's
  rows and history are invisible, a transition row carrying an origin token is not counted), the window,
  the earliest row, the history join, loud refusals.
- `StatsPageWindowTests` (7) - the default window, explicit windows, offsets normalised to UTC, and the
  three refusals.
- `ThrottleFeedNeverReadsTheTallyTests` (3) - the substrate guard: reads the compiled feed with Cecil and
  fails on any call to one of the aggregator's seven turn-counting readers, whichever field it would have
  fed; asserts the list names real methods; asserts the guard sees the feed's real call sites.
- `ThrottleFeedReadsTheLedgerTests` (6, `Gateway.Tests`, hosted) - a real hosted `GatewayHost`, two
  accounts, rows through `POST /activity-events/batch`, the feed through the real auth gate: the numbers
  by shape, tenant isolation both ways, the window and its refusals, the repository join, the
  session-origin block, the absent statistics store, the unbound device.
- `StatsPageEndpointTests` and `HostedStatsSelfHostControlTests` - the self-host sentence, on a 200, with
  no number in the body. `HostedStatsServeTests` - the Postgres-gated hosted serve now asserts the ledger
  figure and that the store-fed blocks are served when the store is up.

**Mutation, watched:** with `row.SendSource is null` added to the exclusion (defect one written back into
the fold) and a `CurrentTotals` call added to the feed, 10 of the 32 unit tests went red; with only the
feed mutation, exactly one - the Cecil guard - went red, naming
`StatsPageEndpoint/<>c__DisplayClass7_0.<Map>b__0 calls CurrentTotals`. Both restored, all 32 green.

## Decisions taken in this phase, for the Architect

1. **The wingman ring is dropped from both pages.** It was a share of TURNS ("turns submitted while a
   session had voice mode on") computed on the `stat_delta` tally, and the ledger carries no voice-mode
   flag. R9 says every turn figure comes from the ledger; R16's reasoning for characters applies word for
   word - it would have been the only turn figure left standing on the untrusted tally, with a footnote
   saying do not believe it. The distinct-sessions count beneath it went with it. If it is wanted back,
   the voice-mode fact has to reach the ledger first.
2. **The feed carries the figure once, under `throttle`,** rather than also mirroring it into the old
   top-level fields. Serving the same numbers twice is a second place for them to drift.
3. **The default window is the ledger's whole retention (thirty days), stated.** Phase four moves the
   default to seven days per R15 and adds the selector; the window statement it needs is already on the
   page and in the feed, so the default can change without a number quietly meaning something else.
4. **The excluded population is served whole and split.** The literal R17 population (every row with no
   input origin) is served as `noInputOrigin`, and beside it the split the reader needs: agent-driven,
   framework, and the unresolved remainder that the page discloses as "outside every number here".
5. **A self-hosted Gateway answers the whole feed with the sentence** - including the session-origin,
   concurrency and token-spend blocks. R1 is about Your Throttle, and those blocks are Your Throttle.

## What this phase did NOT do, said plainly

- **The mentor report itself is unchanged.** It still computes population A from its prompt log and
  reconciles against the ledger. Making it CONSUME the library (R3) is the internal repository's work,
  and phase five is where that repository is touched. What is proven here is that the library and the
  harness's reading of the ledger agree over real weeks; that the report's published ring equals the
  library's figure is not yet true and is not claimed.
- **The check ran the library in-process against the hosted database, not through the deployed
  endpoint.** The deployed Gateway still runs the old feed until the Architect lands and deploys this. The
  library code the check ran is byte-identical to what the endpoint calls, and the endpoint is proven over
  HTTP on a hosted `GatewayHost` in tests, but a run of the check through `GET /stats/data` on the live
  service is a step for after the deploy.
- **The generated OpenAPI client types (`packages/client-core/src/api/schema.ts`) were not regenerated.**
  They are produced from a running Gateway on port 7878, and the only one running here is the old build.
  The statistics client does not read them for this route. Phase four, which adds the selector, should
  regenerate them from a Gateway running this code.
- **Mario's unresolved population is most of his record.** 758 of his 976 submissions in W35 and 367 of
  534 in W34 carry no input origin - his Director stamps almost nothing. The library reports it honestly;
  nothing in this phase changes what his Director records. It belongs in the final report as a finding.
- **The per-agent `agentDrivenTurns` were zero in every measured week** on both accounts, because R12
  (the Agent send source) landed in phase two and no measured week postdates it. The split is proven by
  test, not by a real week.

## The independent inspection of phase two, and its fixes

Mid-phase the Architect relayed the Codex inspection of phase two (`inspection-phase-two.md`): FAIL, three
blockers and one major, with two standing requirements - every fix ships with a test that crosses the
real route, and the test that counted `new PromptRequest` in a source file is deleted. All four are fixed
on this branch, each with a test at the mapped endpoint:

- **I2-01 (blocker) - the recording-stage dictation path labelled a typed mixture as speech.** The Gateway
  composed before + prefix + transcript + after into one message and stamped the delivery id on all of it.
  Fixed at the one place the message is composed (`GatewayDictationEndpoint`): the id rides only when
  before, prefix and after are all empty; a mixed message is delivered exactly the same, as one typed turn.
- **I2-02 (blocker) - the operator prompt body could set `AgentDriven`.** The route now sets it from the
  authenticated credential - a session key is an agent, anything else is the person - and never reads the
  body's value.
- **I2-03 (blocker) - the delivery id was a replayable nonblank marker.** The Gateway now keeps its own
  record of what each utterance upload transcribed (`Voice/SpokenClaimRegistry`, in memory, tenant-keyed,
  fifteen-minute life, spent once). The prompt route clears the body's id and restores it only by spending
  a claim: known, this tenant's, unspent, young, and the submitted words are the transcript. A made-up id,
  a replay, a real id on different words, or any id from a session-key caller is delivered as typed. The
  utterance routes now take the host's transcription service, which is what let the test drive them.
- **I2-04 (major) - a throwing `InputStats.Changed` observer split the tally from the ledger.** The
  fan-out is guarded and logged, so `StampSubmission` always reaches the ledger event.

The route-crossing tests are `CcDirector.Gateway.Tests/Attribution/PromptAttributionIsGatewayAuthoritativeTests`
(8): a real `GatewayHost`, a real SignalR Director connection running the real `SessionCommandExecutor` on
a real `Session`, hostile bodies posted to the mapped prompt route, the utterance upload routes driven with
a faked transcription provider, the durable dictation routes driven with mixed and pure compositions, and
the session's tally bucket and ledger event read at the end. The observer fault is
`CcDirector.Core.UnitTests/Sessions/SubmissionObserverFaultTests` (3), driving both real submission paths.
The source-count test is deleted, with a note at its place saying why.

**Mutation, watched:** all four fixes reverted at once - the body decides `AgentDriven`, the body's id is
trusted, the composed dictation always carries the id, the observer fan-out unguarded - turned six of the
eight route tests red (the two voice happy paths rightly stay green) and all three observer tests red.
Restored, 8 of 8 and 3 of 3 green.

## Results

**The parked suite on the phase two close, `050f7174a` (R18):** started 11:45, finished 13:20, exit 1.

| suite | total | passed | failed | note |
|---|---:|---:|---:|---|
| nine default projects | 4,977 | 4,977 | 0 | |
| Core.Tests | 4,394 | 4,385 | 1 | `TerminalPromptInjectionChokepointTests` pinning the mobile dictated-send call phase two changed for R10 - a phase two defect, fixed here (the pin carries the new call on both shells) |
| Gateway.Tests | 2,340 | 2,334 | 2 | `ContextLessRouteCensusTests`, both facts: three `GET/DELETE /gateway/rules/{id:guid}` routes were mapped on main (`SessionRuleEndpoints.cs`) after the census was written on 2026-08-01, with no request context and no census verdict. PREDATES THE MISSION; the merge-base already fails it. Not fixed here - the census demands a written tenant-confinement verdict per route |

The Gateway suite queued 35 minutes behind another session's run of the same suite and then ran 60 minutes.
Eight Core tests were skipped by their own gates.

**The default gate over everything on this branch** (`.\scripts	est-local.ps1`, 13:38): nine projects,
4,946 tests, 0 failed, by their result files (the script printed FAIL for `Gateway.UnitTests` with "no
summary line" - its result file says 3,641 passed, 2 skipped; the summary-read race is known).
The four web workspaces: client-core 92 files, cockpit 284 tests, mobile 15, cc-assistant 106, all green;
lint clean on the changed files.

**The parked suite over the finished branch** (`.\scripts	est-local.ps1 -Parked` on `18f7b31b3`, 13:43 to
14:20, the Gateway lock uncontended this time):

| suite | total | passed | failed | note |
|---|---:|---:|---:|---|
| nine default projects | 4,946 | 4,946 | 0 | |
| Core.Tests | 4,394 | 4,385 | 1 | the SAME test, one line further: the second pin phase two's R10 change broke (the voice-mode reply now carries the utterance id). Fixed after the run in `7420c63f8`; the class re-run by itself is 4 of 4 green |
| Gateway.Tests | 2,349 | 2,343 | 2 | the pre-existing `ContextLessRouteCensusTests` red on main, unchanged; every test this branch added or changed passed |

So over the finished branch nothing is red that this mission wrote. Two things stay red and are named:
the route census, which predates the mission and needs a ruling, and nothing else.

- **Still not covered:** the client's `useVoiceMode` forwarding argument has no focused test of its own
  (the inspection's audit table); the Core pin above is the only guard on it, and it is a source pin.
