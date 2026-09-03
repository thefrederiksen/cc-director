# Phase 0 plan - the screen store and the push

Manager: session 54d12133 ("Terminal Rules - Manager"). Branch `mission/terminal-rules`,
worktree `D:\ReposFred\devthrottle-terminal-rules`. Written 2026-09-02.

Phase 0 only. Nothing from phases 1 to 5.

---

## What phase 0 lands

1. The Director pushes the turn-end screen it already captures.
2. The Gateway stores it, tenant scoped, 7 day retention, in its OWN store.
3. Every Gateway screen reader reads the store first and falls back to the tunnel pull
   only for a screen the store cannot prove it holds.

## The one design decision this phase turns on: how a stored screen is proven CURRENT

The store holds the TURN-END screen. Several of the six readers sit immediately before a
keystroke goes into live work (the menu guard on the prompt route, the supervisor's
`IsMenuOnScreenAsync`). Serving one of those a screen that has since changed would type
into a picker nobody chose - the exact disaster the menu guard exists to prevent. So the
readers are not split by call site. There is ONE rule, and it is a positive proof:

**A stored screen is served only when the Gateway can prove not one byte has been written
to that terminal since it was captured.**

- The screen push carries the session's `TotalBytesWritten` at the moment of capture.
- The Gateway serves the stored screen only when the session's live pushed snapshot is
  fresh (its Director is connected and pushing) AND its `TotalBufferBytes` still equals
  the mark on the stored screen.
- Anything else - bytes moved, the snapshot is stale, no screen stored - is a tunnel pull,
  exactly as today.

This is not new machinery or a guess. The dictation moved-on guard
(`GatewayDictationEndpoint`, `MovedOnBufferGrowthBytes`) already decides "has this session
moved on since I looked?" from the same counter, and ships. A repaint, a picker opening, a
person typing on the machine - all write bytes, all break the proof, all fall to the tunnel.

The offline case is unaffected and is the point of the phase: when the Director is gone the
stored screen is the ONLY source, so it is served with its capture time and marked not-live.
That is a read a person could not get at all before.

## The slices, in order, each committed and pushed

**A. The store (Gateway).** One table `session_screens`, tenant scoped through the context
filter like every other store. Composite key led by the tenant
(`TenantId, SessionId, CapturedAtUtc`), so a caller-supplied session id cannot squat another
tenant's rows and a re-sent push is idempotent. Columns: the rows as one JSON document, the
cursor row/column, cursor visibility, the alternate-screen flag, `HasGrid`, the buffer-byte
mark, the Director id, the activity state at capture, and the received-at the retention cut
uses. `SessionScreenStore` with `Append`, `ReadLatest`, `ReadRecent`, `PurgeOlderThan`, and a
bounded per-session row cap trimmed at write time so one busy session cannot fill the table
inside the retention window. Migrations on both providers, snapshots in step.

Shape copied from `SessionTurnStore` (validate the whole push before writing anything, one
transaction, idempotent by key, a `FileLog` line per decision). NOT extended: no file of the
turn-push mission is edited. Their retention runs from `SessionHistorySweep` at ninety days;
mine is a separate `SessionScreenSweep` at seven, registered separately in `GatewayHost`.

**B. The push (Director to Gateway).** A `ScreenPush` contract, a `PushScreen` hub method on
`DirectorHub` taking the tenant and the Director id from the connection like every other push,
and the Director side hung on the capture that already exists: `TurnReviewLogger` fires at the
Working -> WaitingForInput flip, so the push is raised from that same flip, from one snapshot,
and sent through `GatewayStreamClient`. No second capture, no second trigger.

**C. The readers.** One `GatewayScreenReader` holding the currency rule above and answering a
`ScreenGridResponse` plus the source it came from. All six `GetScreenGridAsync` call sites go
through it: `GatewayWingmanVoiceEndpoint`, `WaitingScreenReader` (twice), `WingmanVoiceService`,
`GatewaySupervisorEnvironment`. The wingman ones are the point of the phase.

**D. The proofs.** Below.

## Acceptance - each one a presence, and how it is shown

| what must be true | the evidence produced |
|---|---|
| A screen captured on one machine is read back from the Gateway while that machine is offline | The stored row printed out of the database, and the Gateway read that answers it with the Director stopped. Both pasted into the report. |
| A voice turn completes with no tunnel screen read | A COUNTER, not a missing log line: the Gateway counts `screen-grid` commands it sends per session, the report shows the count before and after the turn unchanged at the same number, alongside the reader's own `source=store` lines for every read the turn made. |
| Retention deletes | The sweep is RUN. A row aged past seven days and a row aged six days are both in the table before; only the six-day row is there after. The eight-day row's disappearance is the pass, the six-day row's survival is the control that says the sweep was not simply a delete-everything. |
| Tenant scoping | A NEGATIVE control: tenant B's read for tenant A's session id answers nothing, with tenant A's own read of the same id answering the screen in the same test, so an empty answer cannot be an empty table. |

Plus `.\scripts\test-local.ps1` green before phase 0 is called finished. Not waited on CI.

## What phase 0 does NOT do

No rules, no judge, no actions, no Cockpit screen view, no live streaming, and no edit to any
file the turn-push mission (#2638) owns.
