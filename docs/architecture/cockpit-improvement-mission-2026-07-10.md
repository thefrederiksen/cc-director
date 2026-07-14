# Mission Brief: Cockpit Improvement

Status: active mission. Written 2026-07-10 by the Architect session ("Architect: Cockpit Improvement",
session 0c9d28b7, machine SOREN_NORTH). This document is the Architect's handover to the Manager
session. The Manager owns execution from here; the Architect does not gate the Manager.

## The mission

Upgrade the React Cockpit (apps/cockpit, served by the Gateway) plus the small set of desktop and
Gateway fixes around it, per the reviewed and verified improvement work filed as GitHub issues
number 1238 through 1261. The mission ends with every in-scope issue implemented, verified, and a
nicely formatted HTML quality-assurance report that shows and explains every improvement.

Source documents (read them before starting):

- docs/reviews/cockpit-improvement-report-2026-07-10.html - the improvement report (what and why).
- docs/architecture/cockpit-review-2026-07-10.md - the deep review with file-and-line evidence.
- docs/architecture/cockpit-improvement-plan.md - the approved plan behind issues 1210 to 1215.
- docs/architecture/gateway-owns-session-presentation.md - the presentation-ownership plan (issue 1241).

Every claim in the issues was re-verified against the working tree on 2026-07-10 by the Architect,
including corrections (the orange-rail bug is desktop-only; the Gateway already produces the
machine-errors envelope; effective color and triage bucket are already stamped on the wire).

## Roles and rules of the mission

- The Architect (this document's author) settled the design and filed the issues. Do not re-open
  design questions that an issue already answers; the issue text is the specification.
- The Manager (you) owns execution: sequencing, spawning workers, reviewing their work, merging,
  and the final quality-assurance report. You are allowed to spawn agent sessions yourself
  (cc-devthrottle session spawn <repo> --controlled-by self ...) - that is the DevThrottle rule.
- Escalate to Soren only for product decisions an issue does not answer. Do not stop the whole
  mission to wait; keep working everything that is not blocked.
- Work fully autonomously otherwise.

## Decisions already made - do not re-litigate

1. The issue bodies are the specification. Each has evidence, steps, and acceptance criteria.
2. The Fleet page dies into the Fleet Map (issue 1212); the terminal stays a plain terminal;
   Chat and Voice arrive as literal ports (issue 1213). These belong to the existing stack, not you.
3. The Gateway owns session presentation; clients render server fields (issue 1241 direction).
4. Manual rail order is sacred; only the opt-in attention grouping changes (issue 1249).
5. The browser talks ONLY to the Gateway with relative addresses.
6. No fallback programming; fail loudly with a clear message.
7. Plain English everywhere - no abbreviations, no jargon. ASCII only in code and output.
8. Fleet search filters by session TITLE only. Searching what sessions are actually doing
   (terminal output or transcript content) is a noted follow-on, deliberately not in scope.
9. Remote screen capture is OUT OF SCOPE for the Cockpit. Agents capture their own machine's
   screen with their own tools (AgentEyes). The Cockpit only ever uploads an image from the
   device the browser runs on.
10. "Open in Explorer" and "Open in VS Code" are desktop-only actions and are NOT added to the
    Cockpit session menu - the session may run on a remote machine.

## The one open dependency: the pull-request stack 1217 to 1226

Issues 1210 to 1215 are already implemented in an open, stacked set of pull requests (1217, 1219,
1220, 1223, 1225, 1226), deployed for testing and awaiting merge. That stack is NOT yours to merge -
it belongs to its author and to Soren's click-through. Rules:

- Do NOT merge or rebase that stack yourself.
- Work the Wave 1 issues below first; none of them depend on the stack.
- Surface the stack plainly in your status reporting ("Wave 2 is waiting on the 1217-1226 stack").
  When the stack merges, start Wave 2.
- If the stack is still unmerged when Wave 1 is done, say so loudly (go red / needs-you) rather
  than starting Wave 2 into guaranteed conflicts.

## The work, in waves

Wave 1 - independent of the stack (start immediately, safe to parallelize across workers):

- 1238 [Desktop, bug] Parked dictation clips keep the rail orange and mask red. Highest value
  per line in the mission. Touches MainWindow.axaml.cs counting + a regression test.
- 1243 [Cockpit] New-session dialog: model and permission mode (client-only; Args field exists).
- 1240 [Gateway] Per-session actions use the session-owner cache, not a full fleet scan.
- 1244 [Cockpit] The shared user-interface kit + one confirmation dialog + palette document.
  This is a foundation: 1245, 1248, 1254, 1255 consume it. Do it early, with one strong worker.
- 1250 [Cockpit, bug] Learning page silent failure + full-reload link.
- 1252 [Cockpit, bug] Prompt queue loses text on failed pop/edit.
- 1253 [Cockpit, bug] Transcription Health failure count single-sourced.
- 1254 [Cockpit, bug] Screenshots dock: confirm + re-sync + image error fallback (dialog part
  after 1244).
- 1255 [Cockpit, bug] Dirty tracking: Director settings editor + Dictionary duplicates
  (dialog part after 1244).
- 1261 [Cockpit, maintenance] De-duplicate formatting helpers; names instead of identifier prefixes.
- 1245 [Cockpit] Schedule page rebuild on the reusable sortable/searchable table (after 1244).
- 1246 [Cockpit] Directors page adopts the shared table (after 1245).

Wave 2 - after the 1217-1226 stack merges (files conflict before then):

- 1239 [Cockpit] One shared roster store, visibility-aware polling.
- 1242 [Cockpit] Terminal room: collapsible rails, full-screen, session name in the header.
- 1247 [Cockpit] Navigation cleanup (sections, expose pages, narration remnants, one home name).
- 1248 [Cockpit] Loading/empty/error states everywhere + keyboard + announcements + auth screens
  (after 1244).
- 1249 [Cockpit] Attention view needs-you group in wait-time order.
- 1251 [Cockpit, bug] Rename box must not freeze polling (fix on the post-stack surface).
- 1241 [Gateway plus clients] Finish Gateway-owns-presentation (phased; each phase shippable).
- 1256 [Cockpit] Command-center home (after 1239 and 1244).
- 1257 [Cockpit] Browser notifications via the existing web-push stack.
- 1259 [Cockpit] Jump to a session by number.
- 1266 [Cockpit] Read-only Source Control tab on the session page (added by Soren 2026-07-10).
  The Director and Gateway halves (extend GET /sessions/{sid}/git with per-file lists; read-only
  Gateway proxy with device-key authentication) are stack-independent and MAY be built during
  Wave 1; only the Cockpit tab itself waits for the stack (pull request 1223 rebuilds the tab strip).

Parked - NOT in this mission's implementation scope (good-idea label):

- 1258 (triage from the rail) and 1260 (fleet content search). Leave them parked.

## Working rules for you and every worker

- This is a SHARED working tree with other live sessions. Stage only your own files by name,
  never git add -A. Build every pull request from an isolated worktree off origin/main
  (git worktree add), cherry-pick or re-apply, push HEAD to a branch - never switch the shared
  checkout's branch.
- One issue = one branch = one pull request, referencing the issue. Squash-merge after review and
  verification, then close the issue. Small pull requests.
- Tests are required: every bug fix gets a regression test; every public method change follows
  docs/CodingStyle.md. Run the affected test projects before any merge.
- NEVER kill running cc-director processes. If you need a live Director for testing, build to
  slot 5 or higher (scripts/local-build-avalonia.ps1 -Slot 5) and launch via the
  cc-director-launch scheduled task; shut it down via POST /shutdown on its Control API.
- Gateway testing: build with -p:IncludeNativeLibrariesForSelfExtract=true, swap only the
  executable, relaunch via the devthrottle-gateway-launch scheduled task. Never redeploy-gateway.ps1
  on uncommitted work (it is HEAD-only and reverts the tree).
- Cockpit verification is in a real browser against a running Gateway. Every completed issue needs
  proof: a screenshot or a short observable check matching the issue's acceptance criteria.
- Fleet messages are one line only (newlines truncate). Message only sessions on this mission.
- Do not broadcast to the fleet.

## How to run the workers

- Spawn one worker per issue (or per small coherent group), controlled by you:
  cc-devthrottle session spawn D:\ReposFred\devthrottle --controlled-by self
  --name "Worker: issue NNNN" --purpose "implement issue NNNN"
  --args "--dangerously-skip-permissions --model opus"
  --prompt "Implement GitHub issue NNNN in this repo. The issue body is the full specification.
  Follow docs/architecture/cockpit-improvement-mission-2026-07-10.md working rules. Report back
  to your manager session when the pull request is open with proof."
  (Write the prompt as ONE line when passing it on the command line.)
- Keep a small number of workers running in parallel (three or four), grouped so they do not touch
  the same files. The kit (1244) blocks 1245/1246/1248 and parts of 1254/1255 - sequence around it.
- Review every worker's pull request yourself before merging: does it match the issue's acceptance
  criteria, does it carry the test, is the diff free of unrelated files.
- When a worker is done and its work is merged, have it flag itself finished
  (cc-devthrottle session done).

## The final deliverable: the quality-assurance report

When the in-scope issues are done (or when everything not blocked by the stack is done and the
stack is still unmerged - report what is true), produce:

- docs/reviews/cockpit-improvement-qa-report-<date>.html
- A nicely formatted, self-contained HTML report in the same visual style as
  docs/reviews/cockpit-improvement-report-2026-07-10.html (dark, clean, ASCII text).
- For EVERY improvement: the issue number and title, what was wrong (one plain-English paragraph),
  what changed (files touched, the approach), how it was verified (the acceptance check that was
  actually performed, with the observed result), and its pull request number.
- A verification section: quality assurance means independently CHECKING, not trusting the
  worker's word. Spawn a fresh QA worker session that re-verifies each acceptance criterion in the
  running application and records pass or fail with evidence. Failed items go back to a worker
  before the report calls them done.
- An honest status table at the top: done and verified / done awaiting verification / blocked and
  why / parked.

When the report exists, message the Architect session (0c9d28b7) with its path, one line.
