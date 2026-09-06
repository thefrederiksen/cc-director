# Fix round two - the six open items, and source logging at every prompt door

**Written:** 2026-09-05 by the Manager seated for the second fix round, on branch
`mission/clean-up-your-throttle` in `D:/ReposFred/devthrottle-throttle` (product) and
`D:/ReposFred/devthrottle_internal-throttle` (report).

**The order changed mid-round.** The Architect relayed the owner's priority while the six items were
being closed: install SOURCE LOGGING at every prompt entry point first, with ruling R20 as its first
piece, and keep the inspection work only if nearly done. All six items were already fixed or one test
run from fixed when the message arrived, so nothing was parked; they are recorded below, then the
logging.

The standing rule of this round, which failed the previous one twice: a test that does not enter the
real route proves nothing. Every fix below was driven through the real route and watched red by a
mutation IN that route. The mutations are listed because a test nobody has seen fail is decoration.

---

## R20 - the ordinary desktop Send (blocker) - CLOSED

**What the inspector said.** `SendPromptCoreAsync` cleared the box at line 5014 and asked the
provenance at 5047; the text-changed hook would forget the transcript in between. The provenance kept
transcript STRINGS, so the same words typed and then dictated could be called spoken after the spoken
copy was deleted. A session switch restored the text with no provenance. The claimed test never ran the
real send.

**What was true, measured.** A probe through the real headless `MainWindow` showed the record intact
right after the clear and gone only after the dispatcher ran its posted jobs: Avalonia POSTS the box's
`TextChanged`, it does not raise it inline, so the shipped order happened to work by that deferral and
never by design. The inspector's mechanism was wrong in its timing and right in its judgement: the
order was a coincidence, and nothing tested it.

**What changed.**

- The composition is taken WHOLE before the box is cleared: the text and its provenance come from one
  projection (`ComposerProvenance.ForSend`) and nothing between reading the box and asking it may yield.
- `ComposerProvenance` holds character RANGES (`SpokenSpan`), not strings. Every change to the box is
  applied to the spans as an edit: the changed region is found from the caret when the caller knows it
  (the only way to tell "deleted the first of two identical copies" from "deleted the second"), else from
  the common prefix and suffix, with the two middles aligned character by character when both are
  non-empty. A span whose characters were touched is forgotten; one whose characters moved follows them.
- The spans ride beside `PendingPromptText` on the `Session` across a session switch, and in
  `PersistedSession` across a Director restart; setting the text clears them, so they are always set
  after the text they describe.
- The text-changed hook is wired in the `MainWindow` constructor with the box's other handlers, so a
  headless window carries the real route.
- `OriginFor` REFUSES a text it was not told about, rather than classify a box it lost track of.

**The proof enters the real route.** `ComposerSendRouteTests` constructs the real `MainWindow`, adds a
real `Session`, selects it through the real `SelectSession`, types and dictates into the real
`PromptInput`, runs the real `SendPromptCoreAsync`, and reads the origin the Session stamped on its own
turn-submitted event: the owner's untouched-dictation case, every row of `SpokenTurnRule.Examples`, an
edit inside the dictation, the duplicate-words case in both directions, a session switch, a switch plus
typing, and a restored pending prompt. Core tests pin the range arithmetic and the persistence round trip.

**Watched red.** The origin forced to typed in the real send: five tests. The switch dropping the spans,
or the restore removed: two tests. The caret ignored: the duplicate case on both suites. The old string
record: the duplicate case.

**Not proven.** A mutation that moves the origin read back after the clear cannot be caught by a test,
because the toolkit's deferral makes it pass; the order is now structural and commented, not tested.
The desktop transcriber returns no transcript identifier, so the desktop's ledger rows carry the spoken
ranges and a null transcript id.

## F-06 - the throwing subscriber ahead of the REAL producer (major) - CLOSED

The fix (each subscriber invoked on its own) was already sound; the required proof was not. Three tests
now register the throwing subscriber BEFORE the real `ActivityEventProducer`, wired through its
production `Wire` seam, and read the turn off the real outbox on the text and the terminal path; a
control unwires the producer and sees nothing. Watched red: the single guarded multicast invoke (five
tests), and the producer never subscribing (exactly the two real-producer tests, the lambda tests staying
green - the gap the inspector named).

## F-03 - the conformance command's no-build route (blocker) - CLOSED

`--library-json` is gone. `run_library` builds first and returns the digest of the dll it built; the
provenance sentence names that digest. `tools/throttle-conformance/tests/test_conformance_cli.py` drives
the real `main()` with `subprocess.run` recorded: the build runs, then the built dll; an existing dll is
rebuilt; the removed option is refused before any command; a failed build runs nothing. Watched red: the
option restored (one test), a presence-only build (two tests).

## F-01 and F-08 - the whole rendered answer (blocker, major) - CLOSED

**The library owns every ratio.** `ThrottleFigureDto` now serves the phone ring's other side
(`headline.phone.remainder`) and every surface's remainder, each hour's spoken and typed share, each
agent and repository row's share of turns and of sessions with its printed percent and its own spoken
share, and the two tab summaries (`agentsSummary`, `reposSummary`: counts, totals, the leading entry and
its percent, the voice percent, the leverage and its printed text, the empty state), all computed once in
`ThrottleDefinition.Fold` with the one half-up rounding. The browser client refuses an answer without
them and never reads an absent ratio as zero; the Cockpit's four tabs, the phone's page and the phone's
Repos page print those fields; `summarizeAgents`, `summarizeRepos` and `formatShare` are gone with the
arithmetic they did. The report's modality bars take their counts from the headline, the rings' other
side is the served remainder, and the shares reach the renderer verbatim, not rounded to four places.

**The fixtures are hostile everywhere.** Row shares that disagree with the counts beside them, summaries
that disagree with the rows, remainders 38 short of the subtraction, a Cockpit surface so the report
draws its third ring. The recorded answer is the WHOLE answer.

**The contracts read the rendered output.** The Cockpit contract reads everything off the real page's
DOM: both rings' percent, arc and both counts, the denominator line, every surface segment's width,
label, count and percent, the surface table, the hour's split, and every card and row on the Agents and
Repos tabs under both rankings. The phone contract does the same for its page and its Repos page. The
report contract reads each ring's percent, arc and both counts, each bar's percent and count, the Counts
line and the email figures off the rendered page and email parts. The inventory's report set is split
into fields PRINTED (proven on the page, by name) and fields CONSUMED (proven at the checker and the
adapter), and the union must be the inventory exactly. `conformance.py` compares the remainders, the
hourly shares, every row ratio and both summaries against its independent division.

**Watched red.** Browser: the ring subtracting again (two fixtures on each page), an agent row dividing,
the hour chart dividing, the phone Repos row dividing. Report: the modality bars reading the top-level
counts (the inspector's 57 per cent beside "you spoke 8"), the phone ring subtracting, the four-place
rounding back, each on two fixtures and both inventory tests.

## F-02 - fresh verification at the send (blocker) - CLOSED

`send_report.py` asks the library AGAIN, in the foreground, for the account and week in hand, and refuses
when the answer's digest is not the one `throttle-verified.json` binds; the record's `utc` is parsed and
bounded (older than a day, or from the future, is refused). The inspector's exact probes - the year 2000
and a zero digest - are tests that refuse with no command, no intent line and no claim. Watched red: the
re-ask removed and the age check removed (five sender tests).

---

## Source logging - what every prompt door knows at entry

**The ruling.** Every prompt entry point records, on the turn-submitted ledger row for that submission,
what it knows AT THE MOMENT OF ENTRY, so nothing downstream ever infers it: the surface, the route it
arrived by, the kind of credential that sent it, whether any characters came from a transcript and
WHICH character ranges, the transcript identifier when there is one, and a content digest plus length
wherever the text is in hand.

**The shape.** `SubmissionProvenance` (Core) - `Route`, `IdentityKind`, `TranscriptId`, `SpokenSpans` -
is a REQUIRED argument of the Session's two entry points, `SendTextAsync` and `SendInput`, so the
compiler enumerates the doors and a new one cannot be added silently. The choke point adds the SHA-256
and length of the text it sends (`SubmissionEvidence`); on the raw keystroke path the text is never in
hand, so the digest is null and the length is the printable keystrokes since the last submit. The real
`ActivityEventProducer` writes all six onto `ActivityEventRecord`, and the Gateway stores them in six
new `activity_events` columns (`Route`, `IdentityKind`, `TranscriptId`, `SpokenSpans` as "start+length"
pairs, `ContentSha256`, `ContentLength`), with the SQLite and Postgres migrations
`AddSubmissionProvenanceToActivityEvents` generated on the current chain head and both snapshots
verified in sync. The surface is the existing `InputOrigin`. On the wire, `PromptRequest.Provenance`
carries what the Gateway's door verified to the Director, which records it untouched.

**The doors, and what each records.**

| door | route | identity kind | transcript id | spoken spans | digest |
|---|---|---|---|---|---|
| desktop terminal control (keystrokes, paste, wheel) | `desktop-terminal` | `local-user` | none | none | none (text never in hand); length = printable keystrokes |
| desktop compose box Send | `desktop-composer` | `local-user` | none (the desktop transcriber has no id) | the box's ranges, projected onto the text as sent | of the text sent |
| desktop Speak dialog Send | `desktop-dictation` | `local-user` | none | the earlier segment and the transcript, where they stand | of the text sent |
| Gateway `POST /sessions/{sid}/prompt` (Cockpit, phone typed, operator) | `gateway-prompt` | `device`, `machine-token`, or `unknown` - what the gate verified | the reserved spoken claim's id | the whole text when the claim was reserved | of the text sent |
| a session calling that route (fleet message, ask, broadcast) | `fleet-message` | `session` | none | none | of the text sent |
| Gateway `POST /dictation/{id}/complete` (the phone's Speak, the Cockpit's Speak) | `gateway-dictation` | `device` or `machine-token` | the upload id, spoken turn or not | the earlier segment and the transcript, between the typed halves | of the text sent |
| browser terminal bytes relayed by the Gateway | `gateway-terminal` | `unknown` (the relay carries none yet) | none | none | none |
| the queue drain | `queue-drain` | `framework` | none | none | of the text sent |
| a handover, a pre-prompt, a chat relay, the wingman, a compaction follow-up | `framework` | `framework` | none | none | of the text sent |

A relay from a Gateway older than the field is recorded as `unknown`, never guessed from the surface.

**Proven through the real routes.** Keystrokes through the real `TerminalControl`'s text-input and
key-down handlers into a real Session with the real producer (`TerminalDoorTests`). The compose box
through the real `MainWindow` send, the row read off the real outbox, including a dictation typed around
and a dictation behind a blank line whose span follows the trim (`ComposerSendRouteTests`). The background
Send through its real run, the spoken pieces located in the text handed on for every row of the shared
table (`BackgroundDictationSendTests`). The prompt route and the durable dictation route through the real
Gateway with the real Director executor, for the operator's typed prompt, a session credential, a
reserved claim, a replay, and every mixture row (`PromptAttributionIsGatewayAuthoritativeTests`). The
store through the real SQLite database (`ActivityEventStoreTests`). The choke point and producer end to end
(`SourceLoggingAtTheChokepointTests`).

**Watched red, one mutation per door.** The composer door dropping its spans (2 tests); the dictation
door dropping its spans (1); the terminal door naming another route (1); the prompt route sending no
provenance (4); the dictation route dropping its spans (1); the producer dropping the fields (3); the
choke point not digesting (2); the store dropping a column (1).

**Not done, said plainly.**

- The Cockpit's and the phone's TYPED composers do not track transcript ranges in the browser; a
  transcript inserted into a browser composer and edited is recorded only as far as the Gateway knows
  it (a reserved claim covers the whole text or nothing). Per-character provenance in the browser
  composers is the next piece.
- The browser terminal relay carries no credential kind; its rows say `unknown` until the relay does.
- The desktop transcriber returns no transcript identifier, so desktop rows carry ranges and a null id.
- The ledger rows already written have none of these fields; they read back null. Nothing is restated.
- The EF migration slot: the migration was generated on the chain head that matched `origin/main` at the
  time; if another migration lands on main first, this one must be regenerated on the new head.

---

## What ran, and what did not

| suite | result |
|---|---|
| default local gate (`scripts\test-local.ps1`) | all nine projects green: 206, 3697 (2 skipped), 377, 63, 88, 113, 25, 25, 456 |
| Gateway.Tests (parked), in foreground chunks by first letter | recorded in the closing note below |
| Core.Tests (parked), in foreground chunks | recorded in the closing note below |
| web workspaces (`vitest run`) | client-core, cockpit, mobile, cc-assistant - recorded below; `typecheck` clean on all four |
| mentor harness (`pytest tests`) | 876 passed, 8 skipped |
| conformance command tests (`pytest tools/throttle-conformance/tests`) | 4 passed |
| EF snapshots | both providers: no pending model changes |
