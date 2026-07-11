# Cockpit Review and Top Recommendations

Date: 2026-07-10
Author: review session (devthrottle - codex review)
Scope: the React Cockpit web app (apps/cockpit, served by the Gateway at /c), the shared
packages/client-core it runs on, and the Gateway remote-control path behind it.
Method: full read of the Cockpit source, the shared client, and the Gateway session/stream
endpoints, plus three focused deep-dive passes (UI and front-end quality, the remote-control
mechanism end to end, and the most recent code changes).

---

## Why the Cockpit exists (the yardstick for every recommendation)

The Cockpit is the remote control room for the whole fleet. From any browser - a laptop, a
second machine, a phone on the couch - one person needs to see every agent session running on
every machine, know which ones are stuck and waiting for a human, and drive any one of them
(type into it, stop it, queue work, hand it an image) without ever touching that machine
directly. The browser only ever talks to one central Gateway; the Gateway reaches out to each
machine's Director on the browser's behalf.

So the two questions that judge the Cockpit are: (1) can I trust what it shows me about ten
machines at once, and (2) when I pick a session, is driving it fast, clear, and safe. The
review is organized around those two questions, then around the product surface that surrounds
them.

## What most likely frustrated you in the cockpit-UI session

Reading the code against how you actually use it, these are the friction points that stack up
in a normal driving session, and every one of them shows up in the recommendations below:

- The live terminal - the whole point - is boxed into the smallest part of the screen, and on
  a laptop it is narrow enough that the agent's output overflows and needs sideways scrolling.
- The session you are driving is labelled by a raw identifier (a long code), not the friendly
  name you gave it, so the header never confirms which session you are typing into.
- Machines and sessions flicker in and out of the list when one machine has a single slow
  moment, which reads as "did it crash?" when nothing is wrong.
- There is a row of tabs above the terminal with only one tab in it, an empty button row when
  a session is still loading, and pages that exist but cannot be found from the menu - all of
  which read as "this is half-built."
- Starting a new session from the Cockpit gives you no control over the agent's model or
  permission mode, so the session comes up blocked on a permission prompt and running the
  wrong model.
- The session rail can get stuck showing orange ("transcribing / dictation queued") on a
  session that is actually red and needs you - so the one signal you rely on lies to you.

---

## The recommendations

Ranked by impact. Each says what is wrong, where it lives, what to do, and a rough size.
The first five make the Cockpit trustworthy and pleasant for its core job; the next set
raise the whole product's polish; the last set are new capabilities worth building.

### 1. Stop the fleet list from blinking, and never silently drop a machine

**Problem.** When one machine has a single slow poll, its sessions can vanish from the list
for up to 30 seconds and then reappear. Worse, the two main views disagree: the Fleet page
shows unreachable machines as an explicit warning, but the Sessions page (the one you live in)
silently drops them - a whole machine's sessions just disappear with no explanation. The
Gateway probes each machine with a 2-second timeout and, after three misses, benches that
machine for a 30-second cooldown; a healthy-but-briefly-slow machine (common over a relayed
tail-network hop) gets benched and its sessions disappear.

**Where.** Gateway fan-out and cooldown: `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:406-456`;
timeouts `src/CcDirector.Gateway/Discovery/DirectorRegistry.cs:44-53`, `DirectorEndpointClient.cs:28`.
Silent-drop on the Sessions page: `SessionsView` consumes the plain `GET /sessions` array
(`apps/cockpit/src/sessions/SessionsView.tsx:45-59`), which skips failed machines
(`GatewayEndpoints.cs:456` `if (sessions is null) continue;`), while the Fleet/Fleet-Map pages
use the richer envelope with `machineErrors` (`packages/client-core/src/fleet/fleetClient.ts:89-100`).

**Do.** Give the Gateway a last-known-good session snapshot per machine and keep serving it for
a short grace window (two or three failed polls), marking that machine as "wobbly" instead of
removing its rows. Present three states, not two: Online, Wobbly (dimmed, "last seen 20s ago"),
Offline - and only repeated consecutive failures reach Offline. Never let the list reflow or
collapse on a transient miss; rows change appearance in place. Make the Sessions page consume
the same envelope so it shows the wobbly/offline state instead of silently dropping machines.
Raise the roster probe timeout for known cross-machine hops (the action path already does this),
and require more than one miss before the 30-second bench.

**Size.** Medium. This is Phase 6 of the existing improvement plan (issue #1215); this review
raises its priority to number one because it is the thing that makes the whole control room feel
untrustworthy.

### 2. Give the terminal room, and label it with the session's name

**Problem.** The terminal - the load-bearing surface - is the most cramped thing on the screen.
Fixed chrome eats roughly 780 pixels before the terminal starts: the 220px nav rail, the 264px
session rail, and the 300px right-hand dock. On a 1366-wide laptop the agent's terminal gets
about 580px, narrow enough that output overflows and the code carries a sideways-scroll
work-around to cope. There is no way to collapse the rails or go full-screen. And the terminal's
header shows a raw session identifier, not the friendly name you gave the session, so nothing on
the driving surface confirms which session you are in.

**Where.** Fixed columns: `apps/cockpit/src/styles.css:47` (shell), `:227` (roster 264px),
`:667` (`.session-detail: 1fr 300px`). Overflow work-around: `:198-208`. GUID header:
`apps/cockpit/src/panes/TerminalPane.tsx:47`. The session's real name, repo, machine, and status
are all already in hand at `apps/cockpit/src/sessions/SessionDetail.tsx:25` and are simply not shown.

**Do.** Add a collapse control for the session rail and for the right-hand dock, plus a
full-screen ("zen") toggle for the terminal, so you can hand the driving surface the whole
window when you want it. Replace the identifier in the terminal header with the session name,
its repository, its machine, and a status dot - the same identity you see in the rail - so the
header always confirms what you are typing into.

**Size.** Small-to-medium, and the single biggest day-to-day quality-of-life win.

### 3. Fix the orange rail that hides a session that needs you

**Problem (a real bug).** A dictation clip that is permanently parked - blocked because you are
out of credits, or because the composer is busy - still counts toward the session's
"pending dictation" tally, and that tally paints the rail orange ("dictation queued")
indefinitely. Because the orange state is resolved before the red "needs you" state, a session
that genuinely needs you can be masked as orange forever and never drain on its own. The one
signal you steer by is showing the wrong colour.

**Where.** The count is taken over every stored record with no status filter:
`src/CcDirector.Avalonia/MainWindow.axaml.cs:3246-3248` (`store.LoadAll()`); the orange overlay
wins over red in `src/CcDirector.Core/.../SessionStatusWingman.cs:413-414`; parked statuses
(`NeedsAttention`, `ComposerBlocked`) are explicitly not retry-eligible in
`PendingDictation.cs`.

**Do.** Count only genuinely deliverable (still-pending) clips toward the orange state; parked
clips are already surfaced through the notification banner, so they must not colour the rail.
Add a test that a parked clip leaves a red session red - the current Wingman tests set the count
directly and would not catch this.

**Size.** Small. High value because it corrupts the primary attention signal.

### 4. Let a browser-created session choose its model and permission mode

**Problem.** Starting a session from the Cockpit gives it no arguments: it is hard-coded to the
default agent with no model and no permission flags. In practice that means a Cockpit-created
session comes up blocked on a permission prompt and running the default (smaller-context) model
rather than the one you want - so the first thing you have to do after creating it is fix it by
hand, or it silently runs under-powered.

**Where.** `packages/client-core/src/api/client.ts:688` (`createSession` sends only
`{ repoPath, agent:"ClaudeCode", wingmanEnabled:false }`, no launch arguments); the dialog that
calls it has no such controls: `apps/cockpit/src/sessions/NewSessionDialog.tsx:124`. This matches
the known behaviour that session creation applies no default permission or model - the arguments
must be passed explicitly or the session blocks and runs at the default.

**Do.** Add model and permission-mode choices to the New-session dialog (with your usual
defaults pre-selected), thread them through `createSession` as launch arguments, and remember
the last choice per machine. This is the difference between "create and immediately babysit" and
"create and it just runs."

**Size.** Small-to-medium.

### 5. Poll the fleet once, share it everywhere, and pause when the tab is hidden

**Problem.** Three different pages each poll the full fleet roster independently every two to
three seconds, with no shared cache and no push. Open the Sessions, Fleet, and Fleet-Map pages
and the Gateway runs three overlapping full fan-outs to every machine. Nothing pauses when the
browser tab is in the background, so a parked Cockpit tab hammers the Gateway - and every machine
behind it - forever. And because each keystroke you type is routed by re-scanning every machine
to find the session's owner (rather than using the owner cache the stream path already keeps),
typing latency and Gateway load both grow with the size of the fleet.

**Where.** Independent polls: `SessionsView.tsx:12` (3s), `FleetView.tsx:28` (2s),
`FleetMapView.tsx:22` (2s), plus Directors/Schedule/Wingman/Exes on their own timers - the same
`AbortController + setInterval + keep-last-on-error` block is copy-pasted about nine times.
Keystroke fan-out: `POST /sessions/{sid}/prompt` resolves the owner with a full fleet scan
(`GatewayEndpoints.cs:909`, `LocateSessionAsync` at `:1530`) instead of the owner fast-path cache
used by `SessionWsProxyEndpoints.cs:192-203`.

**Do.** Three moves. (a) Consolidate the roster into one shared client-core store with a single
poll and a `usePolling` hook, so every fleet view reads one cached roster and stays in sync.
(b) Gate polling on tab visibility so a hidden tab goes quiet, and prefer pushing roster changes
over the existing channel rather than re-pulling the whole thing. (c) Route the prompt, interrupt,
and escape calls through the owner fast-path cache so a keystroke no longer scans the whole fleet.
Together these make the control room scale to ten-plus machines without feeling heavy or laggy.

**Size.** Medium. High leverage for a many-machine control room.

### 6. Adopt one small web UI kit and one documented palette

**Problem.** The desktop app and the web Cockpit are two different-looking products. The style
guide the project mandates describes a charcoal, editor-style palette for the desktop app; the
Cockpit invented its own navy palette and is held to no written web standard. Inside the Cockpit,
roughly twenty near-duplicate button classes, a hand-rolled header on every page, and a different
wording for "loading" and "error" on every page guarantee drift. Destructive actions are handled
three different ways - some pop a blocking browser dialog, some confirm inline, some delete with
no confirmation at all.

**Where.** Divergent palettes: `docs/VisualStyle.md` (charcoal/editor tokens) vs
`apps/cockpit/src/styles.css:4-23` (navy tokens). Confirmation drift: inline confirm in
`account/AccountView.tsx` (the good pattern); blocking browser dialogs in `exes/ExesView.tsx`
and `transcripts/TranscriptsView.tsx`; no confirmation at all on Schedule delete
(`schedule/ScheduleView.tsx:272`) or Clear-context (`sessions/SessionActionBar.tsx:87`).

**Do.** Build a tiny shared kit - one Button (with variants), PageHeader, LoadingState,
EmptyState, ErrorBanner, and a single ConfirmDialog/toast - and write a short "Cockpit visual
style" section (or a companion doc) that documents the navy tokens the web app actually uses.
Route every destructive action through the one ConfirmDialog, and add confirmation to the two
that currently have none (Schedule delete, Clear-context). This collapses the drift and makes the
whole app feel like one product.

**Size.** Medium. Pays for itself across every future page.

### 7. Make every real page reachable, and remove the half-built ones

**Problem.** Five fully-built pages have no menu entry and can only be reached by typing a URL:
the Wingman pipeline, the voice recordings/transcripts, the executables/slot manager, the
feedback corpus, and the (being-retired) work lists. Meanwhile there are dead surfaces on show:
a tab strip above the terminal with only one tab, an empty action-bar row while a session loads,
a Fleet-Map legend and card label still advertising a "Wingman narration" feature that is turned
off, and CSS classes that style states that never render. All of this reads as "unfinished."

**Where.** Undiscoverable but built: routes at `apps/cockpit/src/main.tsx:136-166` with no entry
in `AppShell.tsx:19-35`. Single-tab strip: `sessions/SessionDetail.tsx:55-66`. Empty action bar
on a cold deep link: `sessions/SessionActionBar.tsx:61` (capabilities undefined). Retired-but-shown
narration: `fleet/FleetMapView.tsx:58,177,181,417`. Home-route is even named three different
things across pages ("Sessions", "Dashboard", "Cockpit").

**Do.** Group the nav into sections (Fleet / Data / System) and add entries for the built pages
worth keeping; decide the fate of Lists and delete it if it is truly retired. Remove the
single-tab strip until Chat/Voice land, show a "loading session..." state instead of an empty
action bar, and finish retiring the narration overlay (legend, label, and dead CSS). Standardise
the home-route name. Small individually; together they move the app from "beta" to "finished."

**Size.** Small per item.

### 8. Standardise loading, empty, and error states - and announce actions

**Problem.** Every page shows "Loading..." as italic text with different wording, many pages have
no retry on failure, and several destructive results are reported with un-styled browser pop-ups.
There are no skeletons or spinners anywhere, so the app feels slower than it is. And action
results ("turn stopped", "sent") are plain text with no accessible announcement, and several
clickable table rows cannot be reached by keyboard at all.

**Where.** Italic loading text and inconsistent copy across nearly every view; no retry on Fleet,
Fleet-Map, Directors, Schedule, Wingman, Dictionary, About. Keyboard-inaccessible rows:
`fleet/DirectorsView.tsx:115`, `fleet/DirectorDetailView.tsx:171`, `schedule/ScheduleView.tsx:381`
(Fleet-Map's `NodeCard` does it correctly and is the model). Un-announced action status:
`sessions/SessionActionBar.tsx:109-110`.

**Do.** Use the shared LoadingState/EmptyState/ErrorBanner from recommendation 6 everywhere,
with skeleton rows for lists and a consistent Retry. Wrap clickable rows so the whole row is a
real keyboard target. Add a live-region announcement to action and composer status. This is the
polish layer that makes the app feel trustworthy.

**Size.** Small-to-medium (mostly mechanical once the kit exists).

### 9. Reconcile the two clients: one "needs you" order, one behaviour

**Problem.** The same "Needs you" group is ordered differently on the phone and in the Cockpit.
The phone shows it as a wait-time queue (longest wait on top), which is the agreed behaviour; the
Cockpit still shows it in your manual drag order. The shared helper for the queue order exists but
was only wired into the phone. So the same fleet reads differently depending on which screen you
pick it up on.

**Where.** Phone uses the queue order (`apps/mobile/src/pages/Home.tsx:59`, `inWaitingOrder`);
the Cockpit attention view still uses manual order (`apps/cockpit/src/sessions/SessionRoster.tsx:101`,
`inBucket`). The shared helper is `packages/client-core/src/sessions/ordering.ts:71`.

**Do.** Decide the intended behaviour once and wire both clients to it. If "oldest-waiting first"
is the rule (the phone's behaviour, and what the team already recorded as intended), switch the
Cockpit attention group to the same shared helper. Add the missing tie-break tests while there.

**Size.** Small.

### 10. A real command-center home instead of "pick a session"

**Problem.** The Cockpit's front door, before you select anything, is a single line of text that
says "pick a session." For a control room that is a wasted screen - the moment you most want an
at-a-glance read of the whole fleet, you get nothing.

**Do (new page).** Make the landing screen a fleet command center: the "needs you" queue front
and center (who has been waiting longest, and a one-line reason), a strip of fleet vitals (how
many machines online / wobbly / offline, how many sessions working / idle / on hold), any
unreachable machines called out, and the most recent activity across the fleet. Every item links
straight into the session or machine. This turns dead space into the single most useful screen in
the product and directly answers "what needs me right now, across everything."

**Size.** Medium. High-visibility new capability that reuses data the roster already returns.

### 11. The Schedule page is the worst grid in the app - redesign it (case study)

This one is worth a full worked example, because it is the clearest instance of a pattern that
hurts the whole app: putting a big block of free text into a grid cell, and mislabelling the
column while doing it.

**What is wrong today.**

- The grid has a column headed **"Runs"**, but the cell renders the whole skill/prompt text -
  the code is literally `` `skill ${seed}` `` (`apps/cockpit/src/schedule/ScheduleView.tsx:775-778`,
  rendered at `:387` under `<th>Runs</th>` at `:368`). So a column whose name promises a run
  count or run history is filled with hundreds of words of instructions.
- The word **"skill" is glued onto the front of the prompt** with a bare space, so it reads as
  the first word of the instruction rather than a type label. It is a raw internal kind-tag
  leaking into the UI.
- Each row grows to whatever height the instruction text needs, so the grid is a wall of prose
  you have to read past. You cannot scan it. You cannot compare two jobs. The thing a schedule
  grid is *for* - "what runs, where, when next, did the last one work" - is buried.
- When you open one job's editor, the **"Skill / prompt" field is a single-line input**
  (`:541`), so the same multi-paragraph instruction you could not read in the grid is now edited
  through a one-line slot. The two places that touch this text get it exactly backwards: the
  grid shows too much, the editor shows too little.
- The grid cannot be **sorted** or **searched**. For a schedule - which is inherently "things
  ordered in time" - you cannot even sort by what fires next.

```
  WHAT IT LOOKS LIKE NOW  (the "Runs" column is the whole prompt)

  Name                  Target        Runs                                     Schedule     Next run
  ----------------------------------------------------------------------------------------------------
  Shorts stats          SOREN_NORTH   skill You are a DevThrottle marketing    once @       2026-06-28
  analysis (48h                       analytics run (fresh session, no prior   2026-06-28   22:00 UTC
  after batch)                        context). It is about 48 hours after the 18:00:00
                                      full week's batch of animated YouTube
                                      Shorts finished publishing. Do this: 1)
                                      cd into marketing/shorts and run `py
                                      -3.11 youtube_stats.py` to pull the
                                      latest per-video views/likes/comments
                                      ... [hundreds of words, pushing every
                                      other column off to the right] ...
  ----------------------------------------------------------------------------------------------------
        ^ column says "Runs"          ^ but it is the instructions, with the raw word "skill"
          (should be a count/history)   pretending to be the first word of the sentence
```

**The principle.** A grid cell holds one short, scannable scalar - a name, a state, a time, a
count. Long free text (an instruction, a description, a log) is *detail*: it belongs in a
row-detail panel, a drawer, or the editor, never spilled into the grid. The grid answers "which
one and is it healthy"; the detail answers "what exactly does it do." This same rule fixes the
Schedule page and prevents the next person from doing it again elsewhere.

**How it should look - the grid.** One fixed-height row per job. A leading status light. A short
"what it runs" cell that is a *type chip plus a one-line title* (the job's name-derived summary
or the first line of the prompt, truncated), never the body. Human-readable schedule and next/last
run. Sortable headers (default: next run, soonest first). A search box that matches name, machine,
repo, and the prompt text.

```
  HOW IT SHOULD LOOK  (scannable grid; the prompt is NOT in it)

  Search [ youtube________ ]                        Sort: Next run v         [ + New schedule ]

   *  Name                       Runs                Target             Schedule          Next run    Last run
  -----------------------------------------------------------------------------------------------------------
   o  Shorts stats analysis      Skill  youtube-     SOREN_NORTH        Once              --          --
      (48h after batch)          stats rollup        cc-consult         Jun 28  6:00pm    disabled
   *  Inbound Watch (daily)      Skill  inbound-     SOREN_NORTH        Daily  8:00am ET  in 3h       2h ago  OK
                                 watch               devthrottle
   *  Upwork auto-run            Skill  jobs-auto    SOREN_NORTH        Mon-Fri           in 46m      6h ago  OK
      (twice daily)                                  cc-consult         8:14a & 2:14p ET
   *  LOT baggage - check reply  Skill  reminder     SOREN_NORTH        Mon-Fri 1:00pm    in 1d       never
                                                     private            ET
  -----------------------------------------------------------------------------------------------------------
    ^ status   ^ short name       ^ type chip +      ^ machine +        ^ plain English   ^ relative, absolute
      light                         short label        repo               (cron on hover)   on hover
```

Notes on the columns:
- **Runs** becomes a real column again: a small type chip (Skill / Work list / Prompt) and a
  short label, not the body. If you want a number, this is also the natural home for a run count
  ("42 runs, 2 failed").
- **Schedule** is rendered in plain English ("Mon-Fri 8:14a & 2:14p ET"), with the raw cron
  (`13 8,14 * * *`) available on hover - not the reverse.
- **Next run / Last run** are relative ("in 46m", "2h ago") with the absolute UTC on hover, and
  the last run carries its outcome badge (OK / FAILED + reason).
- Every header is sortable; the whole row is one keyboard target; Run now / Edit / Delete live in
  a hover action set or a kebab menu so they do not clutter the default scan.

**How it should look - the detail (click a row).** The full instructions live here, read-only and
scrollable, next to the plain-English schedule, the resolved next run, and the recent run history
with outcomes. This is where you actually read what a job does.

```
  ROW DETAIL / DRAWER  (opens when you click a row)

  +- Upwork auto-run (twice daily) --------------------- [Run now] [Edit] [x] -+
  |  Enabled - SOREN_NORTH - D:/ReposFred/cc-consult - Claude Code             |
  |                                                                            |
  |  Schedule    Every Mon-Fri at 8:14 AM and 2:14 PM Eastern  (13 8,14 * * *) |
  |  Next run    in 46 minutes   (2026-07-11 12:14 UTC)                        |
  |  Notify      Always (success or failure)                                   |
  |                                                                            |
  |  Instructions -----------------------------------------------------------  |
  |  | Run the jobs-auto skill for Center Consulting's Upwork account. In    | |
  |  | the morning, once in the afternoon, check open/message/hire status    | |
  |  | for proposals submitted via ... [full text, scrollable, monospace]    | |
  |  |______________________________________________________________________| |
  |                                                                            |
  |  Recent runs                                                               |
  |    2026-07-11 12:14 UTC   OK       42s                                     |
  |    2026-07-10 20:14 UTC   OK       1m03s                                   |
  |    2026-07-10 12:14 UTC   FAILED   gateway timeout                         |
  +----------------------------------------------------------------------------+
```

**How it should look - the editor.** The prompt becomes a real multi-line, resizable, monospace
text area (it is a multi-paragraph instruction, so give it room), and the schedule shows a live
plain-English preview so you know what the cron string actually means before you save.

```
  EDITOR  (the prompt is a real text area; the schedule explains itself)

  Skill / prompt
  +--------------------------------------------------------------+
  | Run the jobs-auto skill for Center Consulting's Upwork       |
  | account. In the morning, once in the afternoon, check        |
  | open/message/hire status for proposals submitted via ...     |
  |                                                      ( ^ )    |  <- grows / drag to resize
  +--------------------------------------------------------------+

  Schedule  [ Recurring (cron) v ]   Cron [ 13 8,14 * * * ]
     -> Runs every Mon-Fri at 8:14 AM and 2:14 PM Eastern. Next run: in 46 minutes.
```

**What this looks like normally, and the broader take.** Every mature scheduler - a continuous-
integration schedules table, a task scheduler, a hosting provider's cron dashboard - follows the
same shape: a dense sortable/searchable table of *scalars* (name, state, what-it-runs summary,
next/last run, outcome) with the long definition tucked into a detail view or an editor. The
Schedule page is the loudest violation, but the same "big text in a grid cell" and "grid cannot
be sorted or searched" pattern shows up wherever the Cockpit lists things (the Directors table,
the Lists page, the Wingman queue). So the fix here should be built as a reusable table with three
capabilities every list in the app then inherits: **column sort, a search/filter box, and a
row-detail panel** for the long content. Do it once for Schedule, and it becomes the pattern that
retires the wall-of-text grid everywhere.

**Do (summary).** (1) Rename the "Runs" column and stop rendering the prompt body in it - show a
type chip plus a short one-line label. (2) Strip the raw "skill" kind-tag out of the visible text.
(3) Add column sorting (default: next run) and a search box over name/machine/repo/prompt. (4) Add
a row-detail panel that shows the full instructions, plain-English schedule, resolved next run, and
recent run history. (5) Make the editor's prompt a proper multi-line resizable text area, and add a
plain-English schedule preview. (6) Build 3 and 4 as a shared sortable/searchable table so the rest
of the app's grids can adopt it.

**Size.** Medium for the Schedule page; the shared table it produces pays back across the app.

### 12. Grid and list audit - apply the same standard everywhere

Having found the Schedule grid, I audited every other list and table in the Cockpit against the
same standard. The standard, stated once so it can be reused as a checklist:

- **A cell holds one short scalar.** Long free text (instructions, descriptions, transcripts,
  endpoints) belongs in a row-detail panel or an expander, never spilled into a cell.
- **Rows are a fixed, scannable height.** No row grows to fit prose.
- **People and sessions are named, not shown as an identifier.** A raw code fragment is not a
  label.
- **Columns are sortable**, with a sensible default (usually "what matters next").
- **There is a search/filter box** over the fields you would actually look something up by.
- **The whole row is a keyboard target**, and destructive actions confirm consistently.

The headline finding: **no list in the entire Cockpit is user-sortable or searchable.** Not one
grid has a clickable column sort; not one has a search box (the Fleet Map title-search is only
planned, not built). For a control room whose whole job is finding the right thing among many,
that is a systemic gap, not a per-page nit. The good news is the app already contains the right
pattern to copy - the Voice Recorder page - so this is about spreading an existing good idea, not
inventing one.

**Per-surface verdict:**

| Surface | File | Verdict | What it needs |
|---|---|---|---|
| Schedule cron jobs | `schedule/ScheduleView.tsx:363` | Broken (see rec 11) | Prompt out of the grid; sort; search; row detail; multi-line editor |
| Directors table | `fleet/DirectorsView.tsx:96` | Poor | 9 columns force horizontal scroll on a laptop; fixed sort by machine name only; no search. Add column sort + a search box; collapse low-value columns into the detail page |
| Wingman recent briefs | `wingman/WingmanQueueView.tsx:155` | Minor (dead page) | Sessions shown by an 8-character identifier prefix (`shortId`), not their name - the recurring "drive a code, not a name" problem again |
| Lists items | `lists/ListsView.tsx:390` | Being removed | Manual drag-priority (sort intentionally off), but no search within a long list; the page is slated for removal regardless |
| Session roster (rail) | `sessions/SessionRoster.tsx:84` | OK, but no search | Has ordering toggles; has no way to type a word and find a session (Fleet Map search is planned, the rail has none) |
| Voice Recorder (Transcripts) | `transcripts/TranscriptsView.tsx:270` | Good - the model to copy | A card list where each card shows a short summary and expands to the full transcript. This is exactly "scalar in the list, long text in the detail." Reuse this shape |
| Exes | `exes/ExesView.tsx:146` | Separate concern | Card-based, fine structurally; uses blocking browser confirm/alert and an all-caps header inconsistent with every other page |

**The two reusable pieces this produces:**

1. **A sortable, searchable data table** with an optional row-detail drawer - for surfaces whose
   rows are scalars (Schedule, Directors, and any future fleet/data grid). Column sort, a search
   box, fixed-height rows, keyboard-reachable rows, one confirm dialog. Build it for Schedule
   (rec 11), then adopt it in Directors.

2. **The card-with-expander** the Voice Recorder page already uses - for surfaces whose items are
   inherently long (a transcript, a brief, an instruction you sometimes want to read in full).

Between these two patterns, every list in the app has a correct home, and the "wall of text in a
grid cell, and you can't sort or search it" problem stops recurring.

**Also worth fixing while in these files (recurring across the audited grids):**

- **Identity by name, not code.** The Wingman briefs table (`WingmanQueueView.tsx:168`) and
  several other surfaces show an 8-character identifier where the session's name belongs. Same
  root issue as the terminal header in recommendation 2 - show the name.
- **Consistent confirm.** Exes and Voice Recorder use blocking browser `confirm`/`alert`
  (`ExesView.tsx:62,69`; `TranscriptsView.tsx:170,184`); Schedule deletes with no confirm at all.
  Route all of them through the one in-app confirm dialog (recommendation 6).
- **De-duplicate the helpers.** `repoBasename`, `relativeTime`, `humanizeState`, `portOf`, and
  `shortId` are re-implemented inside `ExesView.tsx:252-313` and `WingmanQueueView.tsx:220`
  despite already existing in `fleet/format.ts`. One shared formatting module.

**Size.** The audit is done (this section). The shared table is the medium item from rec 11;
adopting it in Directors and fixing the name/confirm/helper nits are small follow-ups.

### 13. Audit of the remaining screens - the long tail of correctness and consistency

Every remaining Cockpit screen was read in full (the Fleet page, Director detail, Settings,
Account, Telemetry, Transcription Health, About, Dictionary, Learning, Feedback, the two session
dock panels, the sign-in and device-callback screens, and the placeholder/404 panes). Most are
competently built; the value here is a list of concrete, ticket-sized defects plus the systemic
consistency gaps that a shared UI kit (recommendation 6) would close.

**Per-screen quality:**

| Screen | Quality | One-line |
|---|---|---|
| Account (`account/AccountView.tsx`) | Good - the model | Inline confirm, distinct signed-in / loading / error / empty / signed-out states. Copy its confirm pattern everywhere |
| Settings (`settings/SettingsView.tsx`) | Good | Immediate render, loading line, explicit error, no fabricated values; one global `busy` disables every field during any one save |
| Telemetry (`telemetry/TelemetryView.tsx`) | Good | Careful load-vs-save error split; toggle is a plain button (not a real switch); "Saved" banner never auto-dismisses |
| Transcription Health (`transcription/TranscriptionHealthView.tsx`) | Good | Full state matrix; but recomputes a server value client-side (see bugs); no refresh affordance |
| About (`about/AboutView.tsx`) | Good | Correct states; no refresh, so "Gateway time" freezes at page load |
| Director detail (`fleet/DirectorDetailView.tsx`) | Good | Best states of the fleet screens; raw-JSON settings editor has no dirty tracking; mouse-only rows |
| Fleet page (`fleet/FleetView.tsx`) | Good | Solid, but the rename guard can freeze the whole dashboard (see bugs) |
| Screenshots dock (`sessions/ScreenshotsPanel.tsx`) | Good | Well-bounded; optimistic delete with no re-sync, no image-error fallback, Del has no confirm |
| Queue dock (`sessions/QueuePanel.tsx`) | Ok | Server-authoritative, but optimistic Pop and edit-teardown lose data on failure (see bugs) |
| Dictionary (`dictionary/DictionaryView.tsx`) | Ok | Silently drops duplicate entries; load failure is terminal (no retry); no unsaved-edit guard |
| Learning (`learning/LearningView.tsx`) | Ok, one real bug | Ask-Wingman fails silently on a thrown error (see bugs); one hard-reload link |
| Feedback (`feedback/FeedbackView.tsx`) | Good but orphaned | Not in the nav; reimplements `shortId`/`formatTime` instead of reusing `format.ts` |
| Sign-in / Device-callback (`packages/client-core/src/auth/*.tsx`) | Poor styling | Inline-styled, background-less buttons with no theme/focus/hover; callback has no enrollment timeout |
| Placeholder / 404 (`panes/*.tsx`) | Mixed | 404 is fine; PlaceholderPane still ships "ported in a later issue" developer copy and uses a second page-shell vocabulary (`.pane` vs `.page`) |

**Correctness defects worth a ticket each (found in the read):**

- **Ask-Wingman fails silently.** `LearningView.tsx:31-42` has a `try/finally` with no `catch`, so
  if the request throws (Gateway down), the page shows nothing - directly contradicting its own
  header comment about the no-silent-failure rule.
- **The Fleet dashboard freezes while you rename.** `FleetView.tsx:68` bails the 2-second roster
  poll whenever any name input is focused, so every machine's colours/states/new sessions stop
  updating until you blur that one field. A focused input left alone stalls the whole page.
- **The prompt queue loses text on a failed action.** Pop pastes into the composer *before* the
  delete round-trips (`QueuePanel.tsx:59-63`), so a failed delete leaves the item both pasted and
  still queued (a duplicate); and edit-save tears down the edit box before the async save resolves
  (`:50-53`), so a failed save discards the typed edit with only an error banner.
- **Transcription Health can contradict itself.** `failures` is derived as `total - successful`
  client-side (`TranscriptionHealthView.tsx:66-67`), which can disagree with the authoritative
  `byOutcome` map the same page renders; the "all N succeeded" banner trusts the subtraction.
- **Screenshot delete drifts from disk.** Optimistic local delete with no re-sync
  (`ScreenshotsPanel.tsx:76-77`), no image `onError` fallback (`:118`), and no confirm on Del.
- **Director settings editor has no dirty tracking.** "Reload" silently discards unsaved edits and
  "Save" is always enabled even when unchanged (`DirectorDetailView.tsx:344-347`); Dictionary
  likewise drops edits on navigate and silently swallows duplicate entries
  (`DictionaryView.tsx:58,75,106`).

**Systemic consistency gaps (these are what recommendation 6 fixes):**

- **The home screen is named three different ways.** The nav calls it "Sessions"
  (`AppShell.tsx:20`), the Dictionary back-link calls it "Dashboard" (`DictionaryView.tsx:156`),
  and Learning calls it "Cockpit" (`LearningView.tsx:92`). Same destination, three words.
- **Eighteen distinct page-header classes** across the app; only eight uses share `page-head`, and
  the panes use a second, incompatible `.pane` shell vocabulary. No shared PageHeader.
- **Confirmation is chaotic** (restating rec 6 with the full evidence): no confirm at all on
  Schedule delete (`ScheduleView.tsx:272`), Clear-context (`SessionActionBar.tsx:87`), Clear-queue
  (`QueuePanel.tsx:71`), and Screenshot Del (`ScreenshotsPanel.tsx:126`); blocking browser
  `confirm`/`alert` in Exes (`ExesView.tsx:62`) and Voice Recorder (`TranscriptsView.tsx:170`);
  and one good inline confirm in Account. Route them all through one dialog.
- **The auth screens do not match the app.** Sign-in and device-callback are inline-styled with
  background-less buttons and no focus/hover/theme (`SignIn.tsx:49`, `DeviceCallback.tsx:80`) - the
  first screen a new user sees looks unfinished. Give them the app's real button and surface tokens.

**The polling census (reinforces recommendation 5).** Twelve `setInterval` timers across nine
files, and **not one of them checks whether the tab is visible** - a backgrounded Cockpit keeps
polling forever:

| File:line | Interval | Polls |
|---|---|---|
| `fleet/FleetView.tsx:102` | 2s | fleet roster |
| `fleet/FleetView.tsx:112` | 15s | interrupted sessions |
| `fleet/FleetMapView.tsx:76` | 2s | fleet-map roster |
| `sessions/SessionsView.tsx:64` | 3s | sessions roster |
| `fleet/DirectorsView.tsx:53` | 5s | directors list |
| `fleet/DirectorsView.tsx:54` | 1s | clock re-render only |
| `fleet/DirectorDetailView.tsx:77` | 5s | director detail |
| `fleet/DirectorDetailView.tsx:78` | 1s | clock re-render only |
| `schedule/ScheduleView.tsx:147` | 5s | schedule |
| `wingman/WingmanQueueView.tsx:40` | 3s | wingman queue |
| `exes/ExesView.tsx:46` | 3s | executables |
| `lists/ListsView.tsx:161` | 10s | lists |

Three different pages poll the same fleet roster (2s / 2s / 3s), and the two Director views each
run a *second* one-second interval purely to re-render relative-time labels. A single
visibility-aware `usePolling` hook and one shared roster store (recommendation 5) would collapse
all of this.

**Size.** The audit is done (this section). The correctness defects are small individual tickets;
the consistency gaps are absorbed by recommendations 5 and 6.

---

## Bigger new capabilities worth building next

These go beyond fixing what exists. Listed roughly in order of value.

- **Cockpit notifications.** The phone already gets a "needs you" badge via web push; the Cockpit
  has none, so you must keep the tab in view. Reuse that push work so a browser tab (even
  backgrounded) can tell you a session needs you. This is the difference between watching the
  Cockpit and being called by it.

- **Triage from the rail.** For a "needs you" session, let a common answer - approve, deny, or a
  one-line reply - happen right on the rail card without opening the session. When you are
  shepherding ten agents, most interactions are one word; opening each one is the slow path.

- **Jump to a session by number.** Sessions already carry a short number ("102", "104"). Let you
  press a key, type the number, and land on that session - a fast keyboard address for the fleet,
  the way you would switch tabs.

- **Chat and Voice tabs on the session page.** Bring the phone's chat and voice-mode surfaces to
  the Cockpit session page as tabs beside Terminal, sharing the same code (this is Phase 4 of the
  existing plan). Not everyone wants to read a raw terminal to drive an agent.

- **Search what the fleet is doing.** Today you can filter sessions by title. The higher-value
  version is "which agent is touching X" by searching recent terminal output or transcripts across
  the fleet, through the Gateway. That is how you find the right session when you do not remember
  its name.

---

## Quick wins (a day or less each)

- Replace the terminal's identifier header with the session name (part of recommendation 2).
- Remove the single-tab tab strip above the terminal (`SessionDetail.tsx:55`).
- Add confirmation to Schedule delete and Clear-context (`ScheduleView.tsx:272`,
  `SessionActionBar.tsx:87`).
- Fix the one full-page-reload link on the Learning page (it should be an in-app link, not an
  anchor that reloads the whole app) - `learning/LearningView.tsx:98`.
- Delete the dead CSS and the retired narration legend/label on the Fleet Map
  (`FleetMapView.tsx:177,181,417`).
- De-duplicate the copy-pasted date/time and repo-name helpers into one shared client-core module
  (re-implemented verbatim in `exes/ExesView.tsx:252-313` and several other views).

---

## Root causes - the handful of patterns behind most findings

Thirteen recommendations and a page of bugs sound like thirteen problems. They are not. Almost
everything above traces back to six underlying patterns. Fix the pattern and a whole column of
findings closes at once.

1. **The Gateway is pulled, not pushed, from three places at once.** The browser re-fetches the
   entire fleet roster every two to three seconds from several pages independently, the Gateway
   re-scans every machine on every fetch, and even a single keystroke re-scans the fleet to find
   its owner. This one pattern produces the blinking list (rec 1), the load-and-latency problem
   (rec 5), and half the polling census (rec 13). Root fix: one shared roster store, pushed and
   cached, visibility-aware.

2. **There is no shared web UI kit.** Every page reinvents its header, its buttons, its loading
   line, its empty state, its error banner, and its idea of confirmation. That single absence
   produces the eighteen header classes, the twenty button classes, the "loading..." wording drift,
   the four different confirm behaviours, and the un-themed auth screens (recs 6, 8, 12, 13). Root
   fix: build the small kit once.

3. **Long free text keeps landing in grid cells.** The Schedule "Runs" column is the loudest case,
   but the same instinct shows up wherever the app lists things. It produces rec 11 and half of
   rec 12. Root fix: the two reusable patterns - a sortable/searchable table with a detail drawer,
   and the card-with-expander the Voice Recorder already uses.

4. **Sessions are shown as identifiers, not names.** The terminal header, the Wingman briefs table,
   and other surfaces show a code where the name belongs. It produces part of rec 2, rec 12, and
   the general "which one am I looking at" friction. Root fix: always render the name the roster
   already carries.

5. **State is recomputed on the client instead of trusted from the Gateway.** Transcription Health
   subtracts to get a failure count, several pages re-derive times and colours, and the desktop
   rail re-tallies dictation state - each a place where the client can drift from the server. It
   produces the orange-rail bug (rec 3), the transcription contradiction (rec 13), and a class of
   latent inconsistencies. Root fix: the Gateway owns presentation state; the client renders it.

6. **Optimistic actions with no rollback, and destructive actions with no guard.** Pop and delete
   apply locally before the server confirms and lose data on failure; several destructive buttons
   fire with no confirmation. It produces the queue and screenshot bugs (rec 13) and the confirm
   chaos (rec 6). Root fix: confirm through one dialog, and either wait for the server or roll back
   on failure.

If you only internalize one thing from this review: the Cockpit is not thirteen broken things, it
is six missing foundations. The recommendations are ranked for impact, but the cheapest path is to
lay foundations 1 and 2 first, because they retire the most findings per unit of work.

## A suggested sequence

The recommendations are ranked by impact individually; this is how to actually stage the work so
each step ships something and the later steps get cheaper.

- **First - earn trust (make the room reliable).** Recommendation 1 (stop the fleet blinking; never
  silently drop a machine) and recommendation 3 (the orange-rail bug that hides a red session).
  These are the two things that make the control room lie to you; nothing else matters until the
  screen can be believed. Recommendation 4 (model/permission on session creation) rides along here
  because it is small and removes a daily annoyance.

- **Second - lay the two foundations.** Recommendation 5 (one shared, pushed, visibility-aware
  roster store) and recommendation 6 (the small UI kit: Button, PageHeader, LoadingState,
  EmptyState, ErrorBanner, one ConfirmDialog). Everything after this is faster and more consistent
  because these exist. Sweep the ticket-sized correctness bugs from recommendation 13 in here too -
  most are a few lines once the kit's confirm/rollback helpers exist.

- **Third - make the driving surface and the grids right.** Recommendation 2 (give the terminal
  room and a real name), recommendation 11 (rebuild the Schedule page on a sortable/searchable
  table with a detail drawer), then recommendation 12 (adopt that table in Directors). Recommendation
  7 (reachable, consistently-named navigation) and recommendation 8 (standardised loading/empty/error
  and accessibility) fall out cheaply now that the kit exists.

- **Fourth - the new capabilities.** The command-center home (rec 10), then the bigger items -
  Cockpit notifications, triage from the rail, jump-to-session-by-number, Chat and Voice tabs,
  fleet content search - in whatever order matches how you actually work the fleet.

Recommendation 9 (reconcile the two clients' "needs you" order) is a small independent fix that can
land any time. The quick-wins list can be picked off opportunistically whenever someone is already
in the relevant file.

## Relationship to the existing improvement plan

The approved plan at `docs/architecture/cockpit-improvement-plan.md` already covers the composer
Speak button, rail/Fleet-Map polish, folding the Fleet page into the Fleet Map, Chat/Voice tabs,
the session menu, and fleet-sweep stability. This review agrees with that direction and adds the
things the plan does not yet cover or under-weights: the terminal getting real room and a real
identity (recommendation 2), the model/permission gap on session creation (recommendation 4),
the shared UI kit and single palette (recommendation 6), the orange-rail masking bug
(recommendation 3), and the command-center home (recommendation 10). It also raises fleet-sweep
stability (plan Phase 6) to the top of the list, because it is the issue that most undermines
trust in the whole control room.
