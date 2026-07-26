# Work history (#2194) - proof record

Date: 2026-07-26. Everything below was exercised against a Gateway built from this branch
(healthz reported `1.7.4+929b4279...`, this branch's commit), running isolated on port 7899 with
`CC_GATEWAY_NO_TAILSCALE=1` and its own `CC_DIRECTOR_ROOT`, with a slot-5 Director built from this
branch connected over the real SignalR tunnel, running real ClaudeCode sessions given real
(read-only) tasks. The machine's production Gateway and the hosted Gateway were not touched.

## What was proven, in order

1. **Live rows while sessions run.** Three sessions spawned across two repositories
   (thefrederiksen/devthrottle and browser-use/browser-harness). `GET /history/sessions` returned
   three open rows within seconds, grouped repo names present, `endingTone: live`. One row's
   description line was its first prompt (the prompt-log feed), the others the name-plus-repo floor.

2. **A deliberate close is ruled "closed".** `DELETE /sessions/{id}` on the Gateway ended reader B's
   row: `endingKind: closed`, label `Closed`, tone neutral, ended timestamp stamped.

3. **A session can seal its own record.** `POST /history/sessions/{id}/summary` on reader A stored a
   sealed summary (`summaryKind: sealed`), which later survived both a Gateway kill and the
   generator (sealed is never overwritten).

4. **The record survives a power cut.** The test Gateway process was force-killed and relaunched:
   all three rows intact, the closed ending and sealed summary intact, live sessions re-attached on
   the Director's next push.

5. **Silence is concluded "interrupted".** The Director was killed without a farewell (via a real
   pre-existing shutdown bug - see 7). Fifteen minutes after the rows' last refresh the sweep ruled
   both remaining open rows `interrupted`, label `Interrupted - last seen 2026-07-26 20:57 UTC`,
   tone attention, `EndedAtUtc` = the last observation (never an invented instant), summaries marked
   partial where generated.

6. **The Gateway summarises what never sealed.** The background sweep, using the fast wingman model
   over the tenant's prompt log: reader B received an accurate generated summary ("The operator
   asked the agent to list the top-level folders... identified `src/CcDirector.Gateway`. The session
   was then closed."). A session whose transcript was below the model-call floor was honestly marked
   `none` with no model call. Per-repository per-day roll-up paragraphs were generated and cached,
   and the report endpoint served them without any model call on read.

7. **A clean Director stop is ruled "Director stopped" - after fixing a real shutdown bug.** The
   first clean-shutdown attempt revealed that `POST /shutdown` posted `lifetime.Shutdown()`, which
   does not raise Avalonia's `ShutdownRequested` - so the Director's shutdown routine never ran: no
   session kills, no removals, no crash-journal delete (the journal was left behind and later
   claimed as a `.dirty` crash), indistinguishable from a power cut. After the fix (programmatic
   paths run `OnShutdown` explicitly, guarded against double-fire, with the farewell sent FIRST),
   the rerun showed: Director log "sent DirectorStopping farewell", Gateway log
   "[DirectorHub] DirectorStopping ... stamped 1 open row(s)", session D ruled
   `director-stopped` / label `Director stopped`.

8. **The 30-day range API.** `GET /history/report?from=2026-06-27&to=2026-07-26` returned the full
   grouped range (`history-report-30days.json` in this directory).

9. **The Cockpit History page** (`history-page-3days.png`, `history-page-expanded.png`): the rail
   entry, range presets, totals line, repo groups with day headings, model-written roll-up
   paragraphs, tone-dotted session entries with folded ending labels rendered verbatim, and the
   expandable per-session summary detail.

## Test evidence elsewhere

- 51 new unit tests (store, fold, recorder throttle and rulings, summariser parsing) plus the
  57 existing tests on the touched seams (DirectorHub stream, prompt endpoints, stream client) all
  pass. `dotnet ef migrations has-pending-model-changes` is clean on BOTH providers. All web
  workspaces typecheck; all 194 web tests pass (including the updated rail-order pin).

## Known limits recorded during proof

- A Director that dies dirty and RELAUNCHES before the silence threshold will have its dead
  sessions ruled "closed" by the snapshot reconcile (the Gateway cannot tell "removed while the
  tunnel was down" from "died with the old process"). The crash-journal recovery surface (#1862's
  restore screen) is the designed disambiguator; not built here.
- The live fleet reports to the hosted Gateway, so its history starts recording when this ships
  there. Nothing here is retroactive - by design, the record is written as it happens.
