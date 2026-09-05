# Phase one - the reconciliation

**What this is.** One written account, with arithmetic, of why the owner's mentor report says he is
59 per cent spoken and Your Throttle says he is 92 per cent spoken. It changes no product code and no
harness code. It reads.

**Written:** 2026-09-05. **Issue:** thefrederiksen/devthrottle#2690.
**Account:** `soren@centerconsulting.com`, tenant `9f19679f-2e19-41a7-9acf-8cae7a8a59cc`.
**Week:** 2026-W35 - Monday 24 August 2026 00:00 to Monday 31 August 2026 00:00, America/Toronto,
which is 2026-08-24T04:00Z to 2026-08-31T04:00Z. That is the calendar week, in the time zone, of the
report that carries the 59 per cent.

Every figure below was computed from the live hosted Gateway's own store and from the mentor
harness's own extract of it. The scripts are in `evidence/` beside this file and can be re-run.

---

## The answer in one paragraph

The window is not the explanation. Computed over the SAME week, in the SAME time zone, for the SAME
person, the two figures are still 91.5 per cent and 58.8 per cent apart. The gap is population, and
it is three separate defects stacked on top of each other, none of which is either of the two holes
the mission set out to fix. In order of size: **Your Throttle does not count a typed turn at all when
the person types it into the desktop terminal** - 594 of the week's 771 typed submissions, so more
than three quarters of his typing is missing from the denominator. **The Your Throttle store then
counts a great many turns twice or more** - 2,061 of its 3,279 stored turns for the week are restated
cumulatives or duplicated observations, and 96 per cent of that inflation lands on voice. **And the
number the page serves is not a week at all** - it is every turn since 2 August 2026, unlabelled. The
report's 59 per cent is the figure that is close to the truth: the Gateway's own submission ledger for
that week says 56.8 per cent spoken. The owner's instinct that 59 per cent is too low is not borne out
by his own week's records; the number that is wrong is the 92.

---

## The five populations, as counts

All five describe the same person, the same week and the same Gateway.

| # | Population | Voice | Typed | Total | Spoken share |
|---|---|---:|---:|---:|---:|
| A | Mentor report - human prompts (the published 59 per cent) | 929 | 652 | 1,581 | **58.76%** |
| B | The Gateway's own submission ledger, turns carrying an input origin | 1,015 | 771 | 1,786 | **56.83%** |
| C | Your Throttle store as stored, this week | 2,999 | 280 | 3,279 | **91.46%** |
| D | Your Throttle store with restatements and duplicates removed | 1,014 | 204 | 1,218 | **83.25%** |
| E | What the page actually serves (all time, 2 Aug to 5 Sep) | 13,026 | 1,163 | 14,189 | **91.80%** |

E is the 92 per cent the owner sees. C is what E would be if the page showed his week.

Broken out by where the input came from:

| Bucket | A mentor | B ledger | C stored | D reconstructed |
|---|---:|---:|---:|---:|
| voice / desktop | 752 | 835 | 2,320 | 834 |
| voice / phone | 177 | 180 | 679 | 180 |
| typed / desktop | 583 | 696 | 167 | 135 |
| typed / phone | 66 | 68 | 103 | 63 |
| typed / unknown | 3 | 7 | 10 | 6 |

Read the voice rows across: the ledger and the reconstructed store agree to within one turn
(835 against 834, 180 against 180). Read the typed / desktop row across: the ledger says 696 and the
store says 135. That one row is the whole argument.

---

## Population A - what the report counts, record by record

The mentor harness's own classifier was re-run over its own extract for the week. It reproduces the
published figure exactly (0.5876), so what follows describes the number that was actually sent.

- **2,874** user records in the week's prompt log. That is what it starts from.
- Classified: **1,581 human**, **945 framework**, **296 agent**, **52 unresolved**. They sum to 2,874,
  so nothing is unaccounted for.
- Only the 1,581 human records reach the ring. Their modality split is 929 spoken to 652 typed,
  giving **58.76 per cent**, which the report rounds to 59.
- Of the 1,581, **1,217** were decided by a stamp on the record itself and **364** by matching the
  record to a submission event in the ledger. The 364 are almost entirely typed / desktop (363 of
  them) - they are the terminal typing that carries no stamp of its own.
- The 52 unresolved are disclosed on the report ("9.12 per cent of the week's prompt words ... outside
  every number about you"), which is rule R7 already being honoured on this side.

**What the report does not see.** The ledger recorded 1,786 submissions carrying an input origin that
week; the report attributes 1,581 of them, or 88.5 per cent. The 205 it does not attribute are 86
spoken and 119 typed - a slightly voice-rich remainder, which is why 58.76 sits a little above the
ledger's 56.83. The cause is coverage, not classification: a submission only becomes a prompt-log
record if that session's conversation was ingested and the record survived, and the week lost 15
prompt-log records to torn lines on the share (recovering 14).

---

## Population B - the Gateway's own submission ledger

This is the honest reference, and it deserves its status: the ledger event and the Your Throttle turn
counter are written **at the same choke point, in the same method**, eight lines apart
(`src/CcDirector.Core/Sessions/Session.cs:2584` stamps the event, `:2592` counts the turn). Anything
they disagree about is a defect in one of them, not a difference of definition. The ledger is also
append-only, idempotent on replay and durable across a Director crash.

For the week, `activity_events` where `EventType = turn-submitted`:

| Send source | Input origin | Count | What it is |
|---|---|---:|---|
| UserInput | voice/desktop | 835 | desktop dictation |
| (null) | typed/desktop and typed/phone | 594 | **typing at the terminal** - `Session.SendInput` |
| UserInput | (none) | 502 | a person's submission the product could not place |
| Delivery | voice/phone | 180 | the durable phone dictation |
| UserInput | typed | 177 | the message composer, desktop and phone |
| Framework | (none) | 160 | the seed prompt at session creation |
| | | **2,448** | |

1,786 of the 2,448 carry an input origin. **56.83 per cent of those are spoken.**

Two Directors reported in the week - `SOREN_NORTH` (2,404 submissions) and `DEVTHROTTLE_2` (44) -
and both report to the same hosted Gateway.

---

## Where the 34 points go - the bridge, in arithmetic

Starting from the ledger's 56.83 per cent and ending at the page's 92:

| Step | Voice | Typed | Total | Share |
|---|---:|---:|---:|---:|
| The Gateway's submission ledger for the week | 1,015 | 771 | 1,786 | 56.83% |
| less the 594 terminal-typed turns Your Throttle never counts | 1,015 | 177 | 1,192 | 85.15% |
| (what the store actually holds after de-duplication) | 1,014 | 204 | 1,218 | 83.25% |
| plus restated cumulatives (+1,765 voice, +44 typed) | 2,779 | 248 | 3,027 | 91.81% |
| plus duplicated observations (+220 voice, +32 typed) | 2,999 | 280 | 3,279 | 91.46% |
| widened from the week to all time, which is what the page serves | 13,026 | 1,163 | 14,189 | 91.80% |

The single biggest step is the first: excluding terminal typing moves the spoken share by **28.3
points**, on its own, before any double counting. Double counting adds a further **8.2 points**. The
window adds **0.3**.

Explaining this gap by the window alone would have answered almost none of it.

---

## Defect one - a turn typed at the terminal is not counted as a turn

`Session.SendInput` (`src/CcDirector.Core/Sessions/Session.cs:2286-2325`) is the path every keystroke
typed into the desktop terminal takes. When the bytes contain a carriage return it treats the write as
a submission: it stamps the submission event, it records the origin for the conversation ingest, and
it moves the session to Working. It calls `InputStats.RecordCharacters`. **It never calls
`InputStats.RecordTurn`.** There is exactly one call to `RecordTurn` in the whole product, and it is
in `SendTextAsync`.

So typing at the terminal contributes character volume to Your Throttle and no turns. The ring is a
turn ratio. The effect is that the largest single typed input path is absent from the denominator.

Measured for the week: **594 typed turns**, 591 desktop and 3 phone, being **77.0 per cent of the
week's 771 typed submissions**. The mentor report catches them - they are its 364 `ledger-origin`
prompts plus those it matched by stamp.

The rationale written beside the code ("a bare keystroke is the user composing, not a submitted turn")
is correct about a bare keystroke and does not hold for the branch that has already decided the write
IS a submission.

**The page's own disclosure says the opposite.** `StatsPageEndpoint.NotCaptured` tells the reader:
"The message composer, and terminal typing on the desktop app, are counted." That is true of
characters and false of turns, which is the only unit the ring uses.

---

## Defect two - the store counts many turns more than once

Of the 2,210 stored rows for the week, **370 restate a cumulative the store had already recorded** and
**337 are exact duplicates of another row**. Together they carry **2,061 of the 3,279 stored turns,
62.9 per cent**, and 96.3 per cent of that inflation lands on voice.

This is not inferred from a ratio; the rows say so themselves. Worked example, session
`b47938cd-52bb-4684-a23e-2e29b727d5c1`, bucket voice / desktop, in row-id order:

```
id 8171   1 turn    513 chars      running truth:  1 /  513
id 8172   1 turn     63 chars                      2 /  576
id 8175   1 turn     89 chars                      3 /  665
id 8178   1 turn    292 chars                      4 /  957
id 8183   1 turn    201 chars                      5 / 1158
id 8184   1 turn    425 chars                      6 / 1583
id 8185   5 turns  1158 chars   <-- restates the cumulative as at id 8183, exactly
id 8186   1 turn    425 chars   <-- an exact duplicate of id 8184
id 8187   1 turn    122 chars                      7 / 1705
id 8191   1 turn    110 chars                      8 / 1815
id 8192   1 turn    299 chars                      9 / 2114
id 8195   1 turn    500 chars                     10 / 2614
id 8220   1 turn    531 chars                     11 / 3145
id 8221  10 turns  2614 chars   <-- restates the cumulative as at id 8195, exactly
id 8222   1 turn    531 chars   <-- an exact duplicate of id 8220
```

The restatements carry the running total to the character, which is what identifies them and what
also proves the duplicates: id 8221 says the truth at that point was 10 turns and 2,614 characters,
which counts the 425-character turn once, not twice.

**How the reconstruction was made, and how it checks itself.** Walking each session bucket in row
order and keeping a running true total: a row whose (turns, characters) pair equals a cumulative the
walk has already reached is a restatement and contributes nothing; a row identical to one already
accepted in the same hour is a duplicate and contributes nothing; everything else is accepted. The
method is self-validating, because it is the restatements that adjudicate the duplicates - if the
de-duplication were wrong the later restatements would stop matching. Across the whole week, all 370
restatements matched, and only **9 accepted rows out of 2,210** carry more than one turn (all typed,
all two to five turns, consistent with one poll interval covering several turns).

The independent check is stronger still: the reconstructed spoken turns for the week come to **1,014**
against the ledger's **1,015**, bucket for bucket (834 against 835 desktop, 180 against 180 phone).
Two entirely separate stores, reconstructed by two different routes, one turn apart.

**What is NOT established here.** The mechanism that produces the restatements is not proven. The
shape - a full cumulative re-added, repeatedly, on sessions that are alive for hours - is consistent
with the aggregator's high-water entry for a session being lost and the next observation re-adding
everything the session has ever counted, and the code has a path that does exactly that
(`GatewayInputStatsAggregator.Forget` deletes the high-water; a fold with no high-water treats the
whole cumulative as new). It is also consistent with two writers folding the same roster. **This
account does not claim which. Phase two or three must find that out before it fixes it.**

The typed side of the reconstruction has a residual: 204 reconstructed against 177 ledger submissions
through the composer path, 27 turns unexplained. It is not material to any conclusion here, and it is
named rather than smoothed over.

---

## Defect three - the window is not a week and is not stated

`GET /stats/data` serves `aggregator.CurrentTotals(tenant)`. There is no window parameter and no
window in the response. The figure is every turn the store holds.

For this account the store's record **begins at 2026-08-02T12:00Z** and runs to now: 34 days at the
time of writing, not a lifetime and not a week. Nothing before 2 August exists, because the hosted
Gateway's statistics moved from a file to the shared database on that date and the earlier file was
not carried across - it is still on the hosted file share, last written 2026-07-30T21:04, holding
6,548 turns nothing reads.

So "92 per cent" today means "92 per cent since 2 August", and the page does not say so. Next month it
will mean something else again, silently.

One thing the feed already has: `hourlyTurns` carries voice and typed turns per hour with no range
limit, over the same rows. A week window is therefore derivable from today's feed without a new
endpoint - though only by modality, not by surface, and it inherits the same inflation.

---

## The two holes the mission set out to fix - neither one is biting

### The chat relay (thefrederiksen/devthrottle#2639): zero turns this week, and not reachable

`ChatService.HandleAsync` does send the person's own words as `SendSource.Framework` with no origin,
so a turn through it would be counted by nobody. But it did not happen, and it cannot:

- **No code constructs it.** A search of every C# file in the repository for `ChatService` returns the
  class itself, one comment in `ClaudeSummarizer.cs`, and one test that reads the file as text. There
  is no `new ChatService(...)` anywhere and no route mapped to it. The Control API that used to host
  it was removed by the remove-the-network-port mission.
- **The week's framework submissions are all something else.** All 160 of them landed between 3.0 and
  33.4 seconds after their own session started (median 7.4 seconds), one per session across 160
  distinct sessions. That is the seed prompt at session creation, not a chat.

### Phone voice through the one-shot transcription: zero identified, ceiling 60 turns

`SessionCommandExecutor.SendPromptAsync` marks a prompt as spoken only when the send source is
`Delivery`, which the durable dictation path sets. A phone recording transcribed through
`/wingman/transcribe` and then sent as an ordinary prompt is stamped **typed**. The hole is real in
the code. It did not fire this week:

- Every successful transcription that produced text writes a row to `dictation_transcripts` carrying
  the endpoint that made it. The week holds **1,075** such rows: **861** from `batch`
  (`POST /transcription`) and **214** from `dictation` (the durable phone path). **None** from
  `source: "voice"`, which is what both phone voice endpoints write - the one-shot
  `/wingman/transcribe` and the resumable `/wingman/utterance/complete`.
- The transcripts also account for the voice turns: 1,075 transcriptions against 1,015 voice-stamped
  turns. So at most **60** spoken utterances in the week failed to become a spoken turn. Even if every
  one of those 60 were phone voice recorded as typed, the ledger's spoken share would move from 56.83
  per cent to 60.19 per cent.

**This is a finding the Architect has to act on: rule R2 names the two fixes that make the shared
number right, and on the owner's own week neither of them moves it. The three defects above are what
moves it.**

---

## The owner's question - is one session's messages to another being counted as his?

**No, on both sides, in this week. But neither side is refusing them for the reason you would want.**

- **The mentor report.** 296 records in the week are fleet-message envelopes and all 296 are
  classified `agent`, outside every number about him. Seven of them arrived carrying a HUMAN input
  stamp; they were held out by the shape of the text, not by the stamp. So the classifier's
  envelope-before-stamp ordering earned its keep seven times this week, and if the product's framing
  string ever changes, those records become his prompts.
- **Your Throttle.** Agent-driven turns ride a separate table that the human buckets cannot sum in by
  accident, and the reconstructed spoken tally matching the ledger's human spoken tally to one turn
  is evidence that nothing agent-driven leaked into the buckets.
- **But the product did not record them as agent traffic.** Tracing each of the 296 fleet messages to
  its nearest submission event: **292 of them arrived as `SendSource = UserInput` with no input
  origin.** Four had no event within tolerance. Not one arrived as `SendSource = Agent`. The week's
  ledger contains **zero** agent-sourced submissions.

  So Your Throttle leaves them out only because no surface resolved for them, not because it knew
  they were another agent's. The same fact has a second consequence: those 292 turns are missing from
  the agent-driven lane as well, so that number under-reports too.

  This is also what the 502 origin-less `UserInput` submissions are: the population where agent
  traffic hides. The report puts 20 of them in `unresolved` and lets 289 fleet-message envelopes claim
  the rest. Neither figure counts them as his, which is the right answer arrived at by the wrong road.

---

## Is his week split across more than one Gateway?

**No. One Gateway, two machines.**

- Every submission in the week is in the hosted Gateway's store, under his tenant. Two Directors
  reported: `SOREN_NORTH` (2,404 submissions) and `DEVTHROTTLE_2` (44). Both report to
  `gateway.devthrottle.com`.
- The self-hosted Gateway installed on `SOREN_NORTH` holds `stat_delta` rows only from
  2026-07-16T19 to **2026-07-21T21**, tenant `local`, and **zero rows in the week**. It has recorded
  nothing for six weeks.

Ruling R1 (hosted only) costs him nothing in this week's data.

---

## Other facts the later phases will need

- **Your Throttle's store holds 121 of the 250 sessions that submitted a turn in the week.** That
  sounds worse than it is: only 13 origin-carrying submissions live in the 129 sessions it does not
  hold. 81 of those sessions saw nothing but their own seed prompt.
- **The number nobody has published is the agent-driven one.** All time, for his tenant:
  **23,958** turns other agents drove into his sessions against **14,189** of his own - and that
  14,189 is itself inflated. The ratio a shared library ought to be able to state honestly is not
  currently statable.
- **The store's `hour_utc` is a text hour**, so any window a library offers lands on whole coordinated
  universal time hours. For America/Toronto in August that is exact (a four-hour whole offset); it
  will not be exact for a zone with a half-hour offset.
- **`hourlyTurns` excludes `ARCHIVE` rows while the buckets include them.** There are no archive rows
  for this tenant today, so the two agree; a library that reads one and not the other must not assume
  that holds.
- **A session key cannot call `GET /stats/data`.** It answers 403 `session_key_out_of_scope`. Any
  conformance check in phase three that wants to read the feed the way the agent fleet does will need
  a device key, not a session key.

---

## What this account does NOT establish

- **Why** the restatements happen. The shape is measured and the arithmetic is closed; the mechanism
  is a hypothesis with a plausible code path and is not proven.
- **The 27-turn residual** on the typed side of the reconstruction.
- **Anything about the second account (`mario`).** Phase one was scoped to the owner. Phase three runs
  the conformance check over real weeks for both, and the reconstruction method here is the thing it
  should reuse.
- **Any week other than 2026-W35.** The three defects are structural and the code paths are the same
  every week, but only this week was counted.
- **What a corrected figure would say over a longer period.** The store cannot be corrected in place -
  reconstruction is possible only because the restatements carry their own cumulative, and that is a
  forensic accident, not a facility.

---

## How to re-run this

`evidence/` beside this file holds the scripts. They read the hosted Gateway's database and the mentor
harness's existing extract; they write nothing anywhere.

- `pg.py` - opens the hosted statistics database. It reads the connection string from `pgconn.txt`
  beside it, which is NOT committed; get it with
  `az webapp config appsettings list -g rg-devthrottle-hosted-gateway -n devthrottle-gw --query "[?name=='CC_GATEWAY_DB_CONNECTION'].value" -o tsv`.
- `reconstruct.py` - populations C and D, and the restatement and duplicate accounting.
- `ledger.py`, `ledger2.py` - population B out of the mentor harness's `activity_events` extract.
- `mentor_side.py`, `claims.py` - population A, by running the harness's own classifier.
- `fleet.py` - the 296 fleet messages traced to their submission events.
- `gaps.py` - the framework submissions' timing, and the sessions the store does not hold.
- `compare.py` - the per-session comparison of the store against the ledger.

The mentor extract read by four of those lives at
`D:/Personal/OneDrive/Center Consulting/DevThrottle/mentor-data/accounts/soren/raw/`, outside every
repository, and holds prompt text. Nothing from it is quoted here.
