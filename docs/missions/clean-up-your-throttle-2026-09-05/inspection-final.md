# Final independent inspection — Clean up Your Throttle

**Inspected:** 2026-09-05
**Verdict:** **FAIL — five blockers and three major findings.**

The branch contains substantial, real corrections. The served product figure now reads the submission
ledger instead of `stat_delta`; the default is a rolling seven days; the report link carries an ISO week;
the current week conversion is correct across the DST and half-hour cases probed; the original hostile
`AgentDriven` and made-up/replayed delivery-id exploits are closed at the Gateway route; and character
volume is gone from Your Throttle.

Those successes do not establish the mission's central promise. The library does not return the headline
shares: the browser and mentor report still calculate them separately from different fields. The only
send-time route can bypass the throttle drift check, both report-side callers can silently run an old DLL,
an explicit old window is served even though its rows have been purged, and R19 deliberately stops looking
before the first report if two reports are skipped. Inspection finding I2-04 was also moved from one
multicast event to the next rather than closed.

No product or internal implementation was changed. All probes and mutations were made in a source-only
copy under the system temp directory. This file is the only inspection write in either pinned worktree.

## Scope and pins

- Product: `D:/ReposFred/devthrottle-throttle-inspect`, HEAD
  `7c2b30f59a9d7c31a9fc93565f678e7b2785a603`; reviewed from `a0aef2c74..HEAD`.
- Internal report: `D:/ReposFred/devthrottle_internal-inspect`, HEAD
  `19446259d099b746b0ed9ace07ac394080c14fb1`; reviewed against `origin/main` as requested.
- The internal branch forked at `cb50de1f`; `origin/main` has three later commits. The literal two-dot diff
  therefore contains inverse changes unrelated to this mission. The mission contribution was also isolated
  with the merge base (`origin/main...HEAD`) so later main work was not misattributed to this branch.
- Read in full: `brief.md` (R1–R19), `reconciliation.md`, `phase3-library.md`,
  `phase4-5-close.md`, `owner-challenge-is-the-67-real.md`, and `inspection-phase-two.md`, plus the internal
  phase-five record and every production/test path named below.

## Findings

### F-01 — Blocker — R3 is not implemented: two headline computations survive

R3 says the report asks for the answer and does not compute its own. The shared DTO does not contain a
spoken share or phone share; it contains counts and buckets (`src/CcDirector.Gateway/Throttle/ThrottleFigureDto.cs:9-59`).
The two consumers then make different projections:

- The product client ignores `figure.turns`, `figure.voiceTurns`, and `figure.typedTurns` when making the
  headline. It re-totals every bucket and divides those totals
  (`packages/client-core/src/stats/statsClient.ts:468-492`).
- The mentor report uses the top-level `turns`, `voiceTurns`, and `typedTurns` for the spoken ring, but
  independently sums buckets for the surface ring and divides both in Python
  (`D:/ReposFred/devthrottle_internal-inspect/tools/mentor/metrics.py:1492-1512`).

The report's contract checker proves `voiceTurns + typedTurns == turns` and separately proves that all
buckets sum to `turns`; it never proves that the voice buckets equal `voiceTurns` or that the typed buckets
equal `typedTurns` (`tools/mentor/throttle.py:180-208`). A probe with top-level 8 voice / 2 typed and buckets
3 voice / 7 typed was accepted. The report would print 80% while the product page would print 30%.

The browser ingress makes this more than a malformed-DTO thought experiment. `normalizeBucket` maps exactly
`voice` to voice and every other token to typed (`packages/client-core/src/stats/statsClient.ts:232-237`),
while the pure summary tests construct already-normalized buckets. In the temp copy, changing that one
constant comparison from `voice` to `spoken` left all **26** `statsClient.test.ts` tests green. A live page
would then render every served voice bucket as typed; the mentor's **833-test** suite and both conformance
paths would remain green because none exercises this browser normalization.

`verify_throttle.py` does not close the seam. It maps the newly read library answer through the same
report-side `throttle_values` function and checks only the first two percentages in the rendered report
HTML (`tools/mentor/verify_throttle.py:113-139`). It never opens `GET /stats/data` through the product
client, and it never reads the Cockpit or mobile result.

**Required for acceptance:** the library's answer must own the final headline ratios (including denominator
and empty-state semantics), and both consumers must render those fields. At minimum, a contract test must
feed the same hostile wire object through the real browser normalizer and report adapter and compare the
rendered answers.

### F-02 — Blocker — A report can be sent without the throttle verification

`run_report.py` places `verify_throttle` immediately before `send_report`
(`tools/mentor/run_report.py:58-65`), but `send_report.py` is intentionally independently runnable. Its real
send path checks the report contract, call proof, and render proof, then claims the week and invokes
`cc-devthrottle email owner` (`tools/mentor/send_report.py:352-402`). It imports and calls no throttle
verification and requires no throttle-proof record.

Consequently, a caller can regenerate a self-consistent report/page/PDF from drifted metrics and call
`send_report.py` directly. All of the sender's proofs agree with those artifacts, so the email reaches the
owner without the check on which the mission's “no report can be sent” claim depends. The full internal
suite passed **833 tests with 8 skipped** while this route remained open.

This is the same proof-placement class the report pipeline already documents for its call and render
proofs: an independently runnable sender has to carry the gate that matters. Here it does not.

**Required for acceptance:** `send_report.py` itself must require fresh throttle verification bound to the
exact account, week, metrics, rendered artifacts, library answer, and library version/source it is about to
send. Merely keeping the verifier earlier in one wrapper is insufficient.

### F-03 — Blocker — “The product's library” can silently mean an old DLL

The mentor's `library_dll` returns `bin/Debug/net10.0/throttle-conformance.dll` whenever that file exists
and builds only when it is absent (`tools/mentor/throttle.py:91-112`). The product conformance script uses
the same presence-only rule (`tools/throttle-conformance/conformance.py:84-95`). Neither compares source,
project inputs, commit, timestamps, or a content digest.

A no-write probe replaced `subprocess.run` with an exception and called `library_dll` against the existing
product tool. It returned the DLL without attempting a build. At this inspected pin the DLL happened to be
newer than the relevant source by about a minute, so this is not a claim that today's checked-in binary is
stale. It is proof that the gate cannot distinguish that state from a DLL built before a future library
change.

Both `metrics.py` and `verify_throttle.py` call the same helper. Once a stale binary exists, initial metrics,
the pre-send recheck, and `conformance.py` all agree with the same obsolete implementation while a newly
built/deployed Gateway serves the changed source. Every claimed drift check can be green at once.

**Required for acceptance:** build from the pinned source on every report/conformance run, or use a
content-addressed artifact whose recorded source/project digest and commit are checked by the sender. File
existence is not provenance.

### F-04 — Blocker — Old short explicit windows are served after their data has expired

`StatsPageEndpoint.ResolveWindow` rejects an explicit window only when its *duration* exceeds retention
(`src/CcDirector.Gateway/Stats/StatsPageEndpoint.cs:209-226`). Unlike the ISO-week branch, it never checks
whether `fromUtc` precedes `nowUtc - 30 days` (`:257-261`).

In the temp source copy, a one-off assertion asked for 1–8 January 2020 with `nowUtc` fixed at 5 September
2026. The expected refusal failed: the resolver returned a window. The existing **37** window tests all
pass because the only “exactly retention” control uses an August 2026 range and no test asks for an old but
short interval.

The public client type exposes explicit `fromUtc`/`toUtc`, and the endpoint computes the figure over that
accepted range. It therefore returns silent zeroes or a truncated answer for a period the 30-day ledger
cannot hold, contradicting the method's own contract and R4/R9. The CLI face used by the report similarly
accepts arbitrary explicit instants; it relies on callers noticing `earliestUtc` rather than refusing an
unanswerable current request.

**Required for acceptance:** every window form must enforce both maximum span and oldest answerable start,
with boundary tests at just inside, exactly at, and just outside retention.

### F-05 — Blocker — R19 can be skipped forever and mistakes an attempt for a send

R19 says the disclosure appears on the **first report whose week spans or follows the deploy**. The internal
implementation narrows that to the spanning week or exactly the following week. Its docstring explicitly
says that after two skipped reports the correction goes undisclosed and calls that limit deliberate
(`tools/mentor/common.py:214-246`). A direct probe planted the deploy in 2026-W35 and asked for the first
report in 2026-W37; `attribution_step_date` returned `None`. The tests make that contradiction green:
`test_a_week_well_after_the_deploy_does_not_carry_it` requires the disclosure to be absent.

The stop condition is also the wrong fact. The code treats existence of the spanning week's
`send-claim.json` as proof that its report was sent (`common.py:244`). `send_report.py` creates that claim
before it writes the intent or invokes the provider, and deliberately never releases it after a crash,
provider refusal, malformed response, or unknown outcome (`send_report.py:301-325,383-402`). Thus an
unsuccessful attempt in the spanning week also suppresses the following week's disclosure.

The config key is required and the sentence is reachable on the spanning and immediate-following paths;
the render tests prove it appears once in page, HTML email, and text email. What fails is whether it remains
reachable until the first **confirmed sent** report and stops only after that event.

**Required for acceptance:** determine “already disclosed” from a confirmed sent record, and continue
offering the sentence on every later first-report candidate until such a record exists. A claim may prevent
duplicate sends, but it is not evidence that a reader received the disclosure.

### F-06 — Major — I2-04 was moved to the next observer fan-out

The original `InputStats.Changed` exception no longer prevents `StampSubmission` from reaching
`OnTurnSubmitted`; the three focused tests pass. But `StampSubmission` invokes the entire multicast
`OnTurnSubmitted` delegate inside one `try/catch` (`src/CcDirector.Core/Sessions/Session.cs:2504-2542`). The
ledger producer is merely one subscriber (`src/CcDirector.Core/Activity/ActivityEventProducer.cs:78-87`).
In .NET multicast invocation, an earlier subscriber that throws prevents later subscribers from running;
the outer catch then hides that loss from the caller.

A one-off source-copy test registered a throwing `OnTurnSubmitted` subscriber before a ledger observer and
submitted a real text turn. It failed with **expected ledger calls 1, actual 0**. The shipped observer tests
put their ledger listener on `OnTurnSubmitted` and put the exception on the earlier `InputStats.Changed`
event; none tests a sibling ahead of the real activity producer.

The positive production inventory currently finds only `ActivityEventProducer` subscribing to
`OnTurnSubmitted`, so no present production subscriber is known to trigger this split. That limits the
current blast radius, but the earlier inspection explicitly rejected this same latent advertised-observer
seam. The claimed unconditional tally/ledger invariant is still order-dependent.

**Required for acceptance:** record the durable ledger fact directly at the choke point, or invoke each
observer independently so one subscriber cannot suppress the activity producer. The test must place the
throwing subscriber before the real producer.

### F-07 — Major — A spoken claim is spent before any prompt is accepted or delivered

The hostile caller can no longer manufacture speech or replay one successful claim; I2-03's original
exploit is closed. A different attribution loss is reachable, however. The prompt endpoint calls
`SpokenClaimRegistry.TryConsume` at `GatewayEndpoints.cs:2834`, which atomically spends the claim, and only
afterward locates the session at `:2850` and runs the voice menu/delivery path. A missing/stale session, a
confirmed menu refusal, or a later routing failure consumes the only proof even though no turn entered a
session. A retry of the same spoken words is then deliberately classified as typed.

The route tests cover a successful spend, a spend followed by a successful replay, different words, and a
made-up id. They do not cover “accepted claim, no delivered turn, then retry.” This affects the exact
correction the owner challenged: real speech can still enter the typed denominator because of a failed
attempt rather than because the person typed it.

**Required for acceptance:** reserve/commit the claim around a successful prompt acceptance, or restore it
when no submission occurred. The invariant to test is one delivered spoken turn after a failed pre-delivery
attempt, not merely one successful consume.

### F-08 — Major — The conformance proof omits the surfaces and fields that can drift

`conformance.py` is useful evidence that the C# fold agrees with two Python readings of the same extract.
It is not a comparison of the two actual consumers. Its `compare` function checks top-level counts, bucket
counts, exclusions, agent/repository count fields, and hourly **total** turns
(`tools/throttle-conformance/conformance.py:203-243`). It does not compare:

- the product browser normalizer or rendered Cockpit/mobile headline;
- the mentor report adapter or any report/email artifact;
- `unit`, final shares, window kind/days/week/choices, retention, or earliest-ledger instant;
- hourly voice/typed values, repository display names/checkouts, or the endpoint's auth/tenant/window
  resolver.

It also restates the exact predicate as a Python string at `conformance.py:295-296`, despite the narrative
claim that the sentence exists once. This copy will detect some library changes by disagreeing, but it is
still a second authority that has to be maintained.

`verify_throttle.py` covers the report's two HTML rings, but not the product page, email parts, counts,
definition, disclosure, or send-time route. F-01 through F-03 are therefore outside the proof even while
the suites are green. The earlier inspection's `useVoiceMode` forwarding argument also still has no focused
behavioral test; `phase3-library.md:258-260` records that only a source pin covers it.

**Required for acceptance:** define a field inventory for the actual shared answer and require every field
to be exercised through each real consumer boundary. A conformance pass over a second reader does not prove
browser/report equality.

## Earlier phase-two findings

| Earlier finding | Final disposition | Evidence |
|---|---|---|
| I2-01 — mixed recording-stage composition labelled voice | **Closed for the reported exploit** | `GatewayDictationEndpoint` withholds the delivery id whenever before/prefix/after contains text; pure and mixed route tests inspect the real tally and ledger. |
| I2-02 — body-controlled `AgentDriven` | **Closed for the reported exploit** | The public prompt route overwrites the field from the authenticated credential; the hostile body cannot choose agent traffic. |
| I2-03 — arbitrary/replayed nonblank delivery id | **Closed for the reported exploit; new loss in F-07** | The Gateway owns a tenant-scoped, expiring, single-use transcript claim and rejects made-up, mismatched, replayed, and session-key claims. Consumption occurs too early. |
| I2-04 — observer exception splits tally and ledger | **Not genuinely closed** | `InputStats.Changed` is contained, but the whole `OnTurnSubmitted` multicast remains one guarded invocation. The order-reversal probe loses the ledger event (F-06). |

The rebuilt mapped Gateway attribution test class did not produce a completion result in this inspection:
the eight-test run and a one-test filtered run both entered VSTest and then hung until interrupted. That is
recorded as an inconclusive instrument, not a pass. Direct source/caller tracing supports the first three
dispositions; the prior mission's claimed 8/8 result is historical evidence, not this inspector's run.

## Window and calendar conclusions

- **Rolling default:** correct. No query yields `nowUtc - 7 days` through `nowUtc`, and the feed says
  `Last 7 days` (`ThrottleDefinition.DefaultWindowDays = 7`). The report does not rely on this default.
- **Report week:** correct in the current implementation. Its link is
  `/your-throttle?week=YYYY-Www`; the Gateway resolves both local Mondays independently. The mentor asks its
  library face for the same explicit UTC bounds.
- **DST and half-hour:** current source passed two added presence checks: Toronto 2026-W44 was
  `2026-10-26T04:00Z .. 2026-11-02T05:00Z` (169 hours), and Adelaide 2026-W40 was
  `2026-09-27T14:30Z .. 2026-10-04T13:30Z` (167 hours). Python produced the same Adelaide bounds.
- **Mutation gap:** replacing the next-local-Monday calculation with `fromUtc.AddDays(7)` left all 37
  shipped window tests green. The Toronto fall-back probe then failed with expected `05:00Z`, actual
  `04:00Z`. Current code is right; the suite does not protect why it is right, and the conformance/report
  checks use explicit bounds so they would not notice this endpoint-only drift.
- **Retention edge:** fails as F-04. Duration is not age.

## Is 67% real?

Not as a corrected truth. The mission's own owner-challenge record reaches the right, narrower conclusion:

- the observed week had 1,448 voice out of 2,173 counted turns (about 66.6%);
- removing the 25 contaminated `typed/unknown` turns gives 1,448 / 2,148 = 67.4%, a floor;
- matching 55 typed rows to transcriptions within the tight ten-second window gives
  (1,448 + 55) / 2,148 = 70.0%, an evidence-backed estimate, not an exact repaired ledger result.

The branch does not retroactively restamp those historical rows, and the attribution fixes were not yet
deployed when that record was written. Therefore “about 70 after deployment” is a prediction from the
forensic control, while the library can only report what the retained ledger rows actually say. R19 exists
precisely because the number will step when new rows use the corrected attribution. F-05 means that required
qualification is not yet reliably delivered.

This inspection did not rerun the production database measurement: the pinned internal worktree contains no
runtime `config.json`, and no live send or production mutation was appropriate. The arithmetic and code-path
conclusions above are independently checked; the underlying 2,173-row dataset remains evidence recorded by
the mission, not newly reproduced evidence in this run.

## Ruling disposition

| Ruling(s) | Disposition |
|---|---|
| R1, R6 | **Pass:** Your Throttle is hosted-only and self-host returns an explicit unavailable sentence. |
| R2, R10 | **Partial:** the reachable product flow now uses resumable utterance ids and the chat relay remains positively unreachable; failed delivery can still turn speech into typing (F-07). |
| R3 | **Fail:** counts are shared, final answers are not (F-01), and the report may run an obsolete library face (F-03). |
| R4, R5, R15 | **Partial:** selector, rolling seven-day default, stated window, and week link exist; old explicit windows are dishonest (F-04), and week parity lacks a DST mutation guard. |
| R7, R8, R14, R16, R17 | **Pass in the C# definition/feed:** the predicate, submitted-turn unit, excluded counts, revised disclosure, and removal of character volume are present. Report-side validation does not lock every one of these fields (F-08). |
| R9 | **Pass on substrate, fail on end-to-end guarantee:** served turn counts come from `activity_events/turn-submitted`; the second tally is not used. Consumer calculations and stale artifacts can still diverge (F-01–F-03). |
| R11 | **Partial:** terminal submissions now enter the tally and event choke point together, but the durable observer remains suppressible (F-06). |
| R12 | **Pass:** fleet/session callers are classified from authenticated session identity rather than a hostile prompt-body field. |
| R13 | **Superseded by the settled R9 serving choice:** no unvalidated `stat_delta` repair is served; historical ledger rows remain historical rather than being rewritten. |
| R18 | **Historically documented, not independently rerun in full:** the mission records the full parked suite and its pre-existing failures. This inspection ran the changed surfaces and records their limits below. |
| R19 | **Fail:** the implementation expires before the first report and uses a claim rather than confirmed delivery (F-05). |

## Executed evidence

All .NET build/test mutations ran in a clean `git archive` copy, never in either pinned worktree.

| Check | Result | What it proves / does not prove |
|---|---:|---|
| Product throttle definition, ledger reader, and “never reads tally” tests | **20 passed** | C# fold/store substrate at this source pin; not either consumer. |
| Product window tests | **37 passed** | Existing default/choice/week/explicit cases; not old-short or DST-transition weeks. |
| Current-source Toronto fall-back + Adelaide half-hour/DST probes | **2 passed** | Current boundary calculation for those two cases. |
| Client `statsClient.test.ts` | **26 passed** | Pure summary and query behavior; the hostile ingress mutation also passed all 26. |
| Core `SubmissionObserverFaultTests` | **3 passed** | `InputStats.Changed` containment; not subscriber ordering on `OnTurnSubmitted`. |
| Added earlier-subscriber ledger assertion | **failed as expected: 1 vs 0** | Presence proof for F-06. |
| Added old-short explicit-window assertion | **failed as expected** | Presence proof for F-04. |
| DST mutation (`next local Monday` → `start + 7 UTC days`) | **all 37 shipped window tests stayed green** | Concrete constant substitution the suite misses; added fall-back probe observed a one-hour error. |
| Browser modality-token mutation (`voice` → `spoken` at wire normalization) | **all 26 client tests stayed green** | Concrete product-page/report drift the current tests miss. |
| Internal focused throttle/verify/R19/run/sender tests | **141 passed** | The changed report paths are green, including tests that pin the R19 contradiction. |
| Full internal mentor suite | **833 passed, 8 skipped** | Broad regression signal; does not cover the independent send/stale-DLL/browser seams above. |
| Rebuilt Gateway mapped attribution tests | **inconclusive (hung)** | No result; never counted as a clean run. |

## Final acceptance bar

The mission is not ready to merge or deploy as complete. Close F-01 through F-08, then rerun evidence that
crosses these actual boundaries:

1. one library-owned final answer through hosted `GET /stats/data`, browser normalization/rendering, report
   adaptation/rendering, and the independent sender;
2. a content-pinned source build, not an existing DLL;
3. old-short and exact retention edges;
4. DST-transition and half-hour report-link parity, with the known-bad UTC-seven-days mutation red;
5. failed spoken delivery followed by a successful retry;
6. a throwing subscriber before the real activity producer;
7. R19 after any number of skipped reports and after failed/unknown send attempts;
8. a sender invocation that demonstrably cannot reach the email command without the exact throttle proof.

Until those presence checks are red on the known-bad forms and green on the corrected forms, the mission's
tests certify important components, but not the promise made to the reader of Your Throttle.
