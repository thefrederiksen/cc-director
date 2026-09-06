# Independent fix-round inspection — Clean up Your Throttle

**Inspected:** 2026-09-05

**Product pin:** `d2a02b53eb40a4c4e8a9be6dfb18f1285d65cf67`

**Internal pin:** `69bab436cb383e153ac3d2a6cc2b2ec0eb1f036d`

## Verdict

**FAIL — four blockers and two majors remain. Three of the nine acceptance items are closed.**

| Item | Disposition | Severity at this round |
|---|---|---:|
| F-01 | **NOT CLOSED** — the library owns a headline DTO, but the report renders a hybrid answer and browser consumers still compute shares | Blocker |
| F-02 | **NOT CLOSED** — the sender accepts an arbitrarily old proof and never re-asks the current library answer | Blocker |
| F-03 | **NOT CLOSED** — the public conformance command still has an unproven no-build path and labels it built | Blocker |
| F-04 | **CLOSED** | — |
| F-05 | **CLOSED** | — |
| F-06 | **NOT ACCEPTED** — the implementation now fans out safely, but the required real-producer regression was replaced by a fake observer | Major |
| F-07 | **CLOSED** | — |
| F-08 | **NOT CLOSED** — the inventory tests stop at normalisation/checking instead of exercising every field through the rendered consumers | Major |
| R20 | **NOT CLOSED** — ordinary desktop Send clears provenance before reading it; the cross-surface tests do not enter that path | Blocker |

The wide green-suite account in `inspection-final-fixes.md` is not acceptance evidence for the open
items below: each open item identifies either a live route which the asserted test does not enter, or a
value which the asserted test does not observe.

## Open findings

### F-01 — the finished headline is still not the one complete rendered answer

The library half is real. `ThrottleHeadlineDto` now owns the denominator, empty-state ruling, counts,
shares and rounded percentages (`src/CcDirector.Gateway/Throttle/ThrottleFigureDto.cs:194-218`), and
`ThrottleDefinition.Headline` computes them once, including null shares/percents at a zero denominator
(`src/CcDirector.Gateway/Throttle/ThrottleDefinition.cs:269-323`). The main browser summary also copies
those headline fields (`packages/client-core/src/stats/statsClient.ts:566-585`).

The report does not render that complete answer. Its modality bar takes the share and printed percent from
`throttle.headline`, but takes the voice/typed counts from the separate top-level `throttle.turns` block
(`D:/ReposFred/devthrottle_internal-inspect/tools/mentor/render_report.py:1091-1101,1124-1141`). The ring
then combines those independently sourced values and re-totals the surface counts for its denominator
(`render_report.py:1146-1174`). `metrics.throttle_values` also rounds each library share to four decimals
before the renderer sees it (`D:/ReposFred/devthrottle_internal-inspect/tools/mentor/metrics.py:1519-1567`).

The shipped hostile fixture makes the defect concrete: its top level says 8 voice / 2 typed while its
headline says 1,015 voice / 771 typed, 57 per cent spoken. The real report prints **57%** beside
**“you spoke 8 / you typed 2.”** That is not the library's finished headline; it is a contradictory hybrid.
The report contract misses it because it reads only the first two rendered percentages
(`D:/ReposFred/devthrottle_internal-inspect/tools/mentor/tests/test_throttle_contract.py:156-164`). Its
empty fixture is also tested as a fatal render refusal rather than as the library-owned no-data state
(`test_throttle_contract.py:142-154`).

Share computation also remains in browser consumers:

- the Activity view divides hourly voice and typed counts (`apps/cockpit/src/throttle/YourThrottleView.tsx:582-600`);
- the Agents view divides totals for row share, voice share, leading-agent share and leverage
  (`apps/cockpit/src/throttle/AgentsTab.tsx:45-60,148-157` and
  `packages/client-core/src/stats/statsClient.ts:648-658`);
- the Repos view does the same for row, voice and leading-repository shares
  (`apps/cockpit/src/throttle/ReposTab.tsx:50-65,153-163` and `statsClient.ts:678-685`).

The real-page contract is too narrow to detect changes to most of the answer. On both Cockpit and mobile it
asserts only the two `.ring-pct` text nodes; it does not assert headline counts, arcs, labels, denominator,
surface shares, surface counts or the surface split
(`apps/cockpit/src/throttle/YourThrottleView.test.tsx:169-196` and
`apps/mobile/src/pages/YourThrottle.test.tsx:166-193`). Replacing a ring count or any surface segment's
rendered value with a constant leaves that contract green while changing what the reader sees. The report
test has the same two-percent blind spot. F-01's required hostile-wire comparison of the **rendered
answers**, rather than two selected fields, has therefore been narrowed around the surviving seam.

### F-02 — a proof on disk is not fresh verification at send time

`send_report.py` now carries a throttle gate, but the gate re-reads only library provenance and passes it to
`throttle_proof.check_record` (`D:/ReposFred/devthrottle_internal-inspect/tools/mentor/send_report.py:304-318,402-416`).
It does not call `ask_library`, `verify_throttle`, or any equivalent current-data read.

`throttle-verified.json` records a timestamp and an answer digest
(`D:/ReposFred/devthrottle_internal-inspect/tools/mentor/throttle_proof.py:36-69`), but `check_record` merely
requires those fields to exist. It never parses or bounds `utc`, and it never compares `answer_sha256` with
a newly obtained answer; its only live comparisons are `commit` and `dll_sha256`
(`throttle_proof.py:93-158`).

A temp-only probe imported the real `check_record`, created matching artifacts/provenance, then changed the
record to `utc = 2000-01-01T00:00:00Z` and `answer_sha256 = 000…000`. The real checker returned the record:

```text
ACCEPTED_AGE=2000-01-01T00:00:00Z
ACCEPTED_UNCHECKED_ANSWER_SHA=0000000000000000000000000000000000000000000000000000000000000000
```

Thus a verified report may wait while the hosted ledger changes, then be sent as long as its local files and
DLL did not change. The sender is bound to an old answer, not the exact answer it is about to send, and the
explicit freshness requirement remains open.

### F-03 — one conformance route still skips the source build

The normal mentor query now builds on every ask, and the normal product conformance path calls
`build_library`; that closes the original presence-only helper on those routes.

The public conformance CLI nevertheless documents `--library-json` as “skip running the library”
(`tools/throttle-conformance/conformance.py:39-50`). With that option, it reads arbitrary JSON, sets the DLL
digest to null, and never calls `build_library` (`conformance.py:396-440`). It can still exit successfully,
and its report unconditionally says **“Library provenance: built from source this run”**
(`conformance.py:448-455`). No test mentions `--library-json`.

That is neither a build from the pinned source nor a content-addressed artifact whose source/project digest
is checked. It is exactly the no-build conformance run F-03 forbids, now with a false provenance sentence.

### F-06 — behavior repaired, required regression tested around the real observer

The production fan-out itself is corrected: `Session.StampSubmission` snapshots the delegate invocation
list and invokes each subscriber under its own guard (`src/CcDirector.Core/Sessions/Session.cs:2525-2553`).
This removes the earlier order-dependent suppression in the code inspected.

Acceptance also explicitly required the throwing subscriber to be registered before the **real
`ActivityEventProducer`**. The new fault tests instead put a lambda which appends to a local `ledger` list
after the throwing lambda (`src/CcDirector.Core.UnitTests/Sessions/SubmissionObserverFaultTests.cs:107-149`).
The normal producer tests separately prove its ordinary wiring, but no test composes that producer with the
fault. `ActivityEventProducer.Wire` remains the production subscription seam
(`src/CcDirector.Core/Activity/ActivityEventProducer.cs:79-86`).

Consequently a regression which removed or changed the real producer subscription could leave the new
F-06 tests green. The runtime fix appears sound, but the exact required presence proof was substituted with
a look-alike observer, so F-06 is not accepted in this round.

### F-08 — the field inventory proves survival, not use by a real consumer

The inventory now matches the DTO shape, which is useful. Its consumer-side claims are not what its tests
prove:

- The browser inventory test feeds the wire through `getThrottle` and then compares each marked value in
  the normalised object. It stops before either rendered page (`packages/client-core/src/stats/statsClient.contract.test.ts:88-104`).
- The report inventory test compares every marked value immediately after `throttle.check_answer`. It stops
  before `metrics.throttle_values` and `render_report` (`D:/ReposFred/devthrottle_internal-inspect/tools/mentor/tests/test_throttle_contract.py:167-220`).
- The rendered-page/report contract then observes only the two headline percentage strings, as described
  under F-01.

This matters, not just formally. The inventory marks `headline.hasData`, headline counts, all four surface
labels/counts/shares/percents, and many window/ledger fields as read by a consumer, yet the cross-consumer
contract can remain green when their rendered uses are replaced by constants. On the report side the hostile
fixture already produces the wrong voice/typed counts while the contract passes. “The adapter retained the
field” is proof about the adapter, not proof that the real consumer used it.

F-08 required every field to be exercised through each real consumer boundary. That requirement remains
open.

### R20 — ordinary desktop Send destroys the evidence before classifying it

The shared rule and its examples are sensible, and the background desktop send plus phone route both use
them. The ordinary compose-box route does not preserve them through the real send sequence.

`PromptInput.TextChanged` calls `_composerProvenance.TextChanged(text)`
(`src/CcDirector.Avalonia/MainWindow.axaml.cs:4763-4770`). `SendPromptCoreAsync` captures the text, then sets
`PromptInput.Text = ""` at line 5014, and only later asks `_composerProvenance.OriginFor(text)` at line 5047
(`MainWindow.axaml.cs:5000-5050`). Clearing the TextBox raises the wired text-change path and removes the
stored transcript before `OriginFor` reads it. An untouched inserted dictation sent with the normal Send
button is therefore stamped `DesktopTyped`.

The claimed consistency test cannot notice. Its background-send half enters
`BackgroundDictationSend.RunAsync`, while its compose-box half constructs `ComposerProvenance` directly and
calls `OriginFor` without executing `MainWindow.SendPromptCoreAsync`
(`src/CcDirector.Avalonia.Tests/BackgroundDictationSendTests.cs:168-229`). Across all test sources, the only
`OriginFor` calls are direct class calls; none invokes the real MainWindow send. Replacing the origin in the
real send path with `DesktopTyped` leaves those cross-surface tests green.

Two further R20 requirements are also narrowed:

- `ComposerProvenance` stores normalised transcript **strings**, not character ranges, and keeps one while
  the same text occurs anywhere (`src/CcDirector.Core/Sessions/SpokenTurnRule.cs:61-119`). With duplicate
  typed and spoken text, deleting the spoken occurrence while leaving the typed occurrence can still be
  classified as voice. It does not know which characters were spoken.
- A session switch persists only `PendingPromptText`; restoring it fires the text-change path with no
  corresponding provenance (`MainWindow.axaml.cs:2110-2125,2173-2178`). The builder's own record acknowledges
  this unfinished case (`inspection-final-fixes.md:161-163`).

The identical-mixture tests cover the phone and one desktop path, but not the ordinary desktop path named by
R20. The surfaces can still disagree without the tests going red.

## Closed findings

### F-04 — closed

`ThrottleDefinition.WindowRefusal` is now the one span-and-age rule, including the inclusive oldest-start
boundary (`src/CcDirector.Gateway/Throttle/ThrottleDefinition.cs:57-78`). Explicit and ISO-week forms call it
(`src/CcDirector.Gateway/Stats/StatsPageEndpoint.cs:203-226,253-269`), as does the command-line face
(`tools/throttle-conformance/Program.cs:123-140`). Tests cover the old short window and just-inside,
exactly-at and just-outside boundaries for explicit and week forms
(`src/CcDirector.Gateway.UnitTests/Stats/StatsPageWindowTests.cs:255-341`).

### F-05 — closed

`disclosure_confirmed_sent` requires a `sent` line with both provider id and the disclosure value, and fails
loud on an unreadable log (`D:/ReposFred/devthrottle_internal-inspect/tools/mentor/common.py:220-241`).
`attribution_step_date` walks every earlier candidate week back to the deploy and keeps the sentence pending
until that confirmed fact exists (`common.py:244-284`). Real-render tests cover failed/unknown attempts and
multiple skipped weeks (`tools/mentor/tests/test_attribution_step.py:111-154`).

### F-07 — closed

`SpokenClaimRegistry` now has atomic free/reserved/spent states and explicit commit/release
(`src/CcDirector.Gateway/Voice/SpokenClaimRegistry.cs:57-70,93-134`). The prompt route reserves before it
locates/delivers, commits only when delivery reports accepted, and releases in every other outcome
(`src/CcDirector.Gateway/Api/GatewayEndpoints.cs:2837-2875,2878-2935`). The route regression performs a real
missing-session attempt, retries the same utterance successfully as voice, then proves a subsequent replay
is typed (`src/CcDirector.Gateway.Tests/Attribution/PromptAttributionIsGatewayAuthoritativeTests.cs:276-305`).

## Inspection method and limits

- Both HEADs were re-read at the pins above. Before this review file was added, the product worktree had
  only the pre-existing untracked `pgconn.txt`; the internal worktree was clean.
- `inspection-final.md`, R20 in `brief.md`, and `inspection-final-fixes.md` were read in full. The builder's
  self-account was used as a route map, not as proof.
- No source, product, test or configuration file was changed. The F-02 probe used a self-deleting system
  temporary directory and the real internal checker.
- The builder's broad suites were not rerun in these pinned inspection worktrees. Their reported green
  results cannot change this verdict: F-02's real checker positively accepted a stale/unbound record, and
  the other blockers are direct production branches paired with the exact assertions that omit them.
- This inspection does not claim to exercise the deployed hosted Gateway or an actual email send. Those
  surfaces are not needed to establish the source-level acceptance failures above.
