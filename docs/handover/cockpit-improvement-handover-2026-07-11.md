# Cockpit Improvement Mission - Handover

Date: 2026-07-11
From: Manager session "Manager: Cockpit Improvement" (2e9e00d2) on SOREN_NORTH, repo D:\ReposFred\devthrottle
Reason: the owner (Soren) halted the mission for handover to a new agent working on a new cc-director.
Status at halt: Wave 1 complete and deployed; the 1217-1226 stack landed and deployed; Wave 2 partially done, several pull requests open on origin, urgent regression fix pushed but not merged.

Everything a worker produced is on origin (pushed). Nothing is stranded on a local branch. All five of this mission's worker sessions have been reaped.

---

## 1. Deploy state - what is LIVE right now

- Gateway executable: commit 3b2e0a15 (the full 1217-1226 stack). Installed at C:\Users\soren\AppData\Local\cc-director\gateway\devthrottle-gateway.exe, launched by the Windows scheduled task devthrottle-gateway-launch.
- Cockpit assets served by that Gateway (wwwroot\c): commit 7f180cbe (the stack PLUS Wave 2 issues 1288 and 1289). A backup of the previous cockpit is at wwwroot\c-backup-predeploy.
- Front door: https://soren-north.taildb08ed.ts.net/ (also http://localhost:7878/ on this machine - localhost is a secure context so it works fully).
- Directors: STILL THE OLD BUILD. Consequence: the new session-menu "Handover info" panel and the per-file git status work only at the Gateway boundary (the Gateway route is live and forwards, returning 502 because the old Directors lack the Director-side route). Full end-to-end for both needs the Directors updated to the new build. Mark these "verified at the Gateway boundary, Director end-to-end pending the next Director update".

Redeploy recipe (the ONLY correct one): run scripts\redeploy-gateway.ps1 from a checkout whose HEAD is the commit you want to ship. It publishes the Gateway AND the cockpit (wwwroot\m + wwwroot\c) from an isolated worktree pinned to HEAD, gracefully shuts the running Gateway, swaps, relaunches, and asserts the running /healthz SHA matches. IMPORTANT: the shared working tree is on branch feat/desktop-dictation-orange-rail (stale) - do NOT run the script from the shared checkout; run it from a worktree checked out at origin/main, or its HEAD ships the wrong code. For a cockpit-only change you may instead `npm run build` apps/cockpit from an origin/main worktree and robocopy dist -> wwwroot\c (no Gateway restart needed; robocopy exit codes 0-7 are success).

---

## 2. Open pull requests on origin (Wave 2, NOT merged) - land these

Recommended merge order (they share files - rebase each onto the prior before merging):
  #1296-fix  ->  #1295  ->  #1299  ->  #1301  ->  #1302

| Item | Issue | Branch on origin | State | Notes |
|------|-------|------------------|-------|-------|
| (no PR yet) | 1296 | feat/recover-cockpit-qa-fixes-1296 (8fecaf43) | code complete, NOT browser-verified | URGENT live regression - the menu bug the owner is seeing now. Open a PR, browser-verify, MERGE FIRST, then redeploy. |
| #1295 | 1247 | feat/cockpit-nav-cleanup-1247 | CI was green | Nav sections + expose pages + one home name "Sessions" + remove Fleet Map narration remnants. Held for two owner calls (see section 4) and merge order. |
| #1299 | 1266 (tab) | feat/1266-cockpit-source-control-tab | CI was green | Read-only Source Control tab on the session page. Backend (steps 1-3) already merged. Worker: styles.css change is one self-contained block, reconciles cleanly. |
| #1301 | 1257 | feat/cockpit-web-push-1257 | CI was green | Cockpit browser notifications reusing the existing web-push stack. Shares main.tsx + SessionsView with #1295. |
| #1302 | 1239 | feat/cockpit-shared-roster-1239 | complete | One shared visibility-aware roster store (foundation for 1256). |

Contested files across the above (expect small rebase conflicts, resolvable by keeping both intents):
- apps/cockpit/src/styles.css  (1296, 1295, 1299)
- apps/cockpit/src/main.tsx  (1295, 1301)
- apps/cockpit/src/sessions/SessionActionBar.tsx  (1296, 1295)
- apps/cockpit/src/fleet/FleetMapView.tsx + fleetmap.css  (1296, 1295)
- apps/cockpit/src/sessions/SessionsView.tsx  (1295, 1301)

After landing them, do ONE clean gateway+cockpit redeploy from origin/main and verify (front door + healthz SHA + the four #1296 acceptance checks in a browser).

---

## 3. What is already merged and deployed

- Wave 1 (12 issues): 1240, 1243, 1244, 1250, 1252, 1253, 1254, 1255, 1245, 1246, 1261, and the 1266 backend. Issue 1238 was closed as obsolete (superseded by #1208); its real successor bug is filed as #1268 (desktop rail can stay orange via an orphaned dictation-lock marker - NOT fixed, filed for later).
- The 1217-1226 stack: PRs 1217/1219/1220/1223/1225/1226, issues 1210-1215 closed. Two real integration fixes were made while landing it: (a) the prompt-injection chokepoint test was updated to follow the mobile voice-submit into its hoisted client-core hook; (b) a GatewayEndpoints/DirectorEndpointClient conflict was resolved keeping BOTH the #1266 git proxy and the 1214 handover endpoint, with handover on the #1240 owner cache.
- Wave 2 so far: #1288 (PR #1290, dictation dialog styles hoisted to client-core - the Speak dialog is now styled) and #1289 (PR #1291, large two-tab Schedule editor). Both merged and in the live cockpit.

Wave 1 QA report deliverable: PR #1282 (docs/reviews/cockpit-improvement-qa-report-2026-07-10.html) plus the restored mission source docs. NOT merged - awaiting the owner's look.

---

## 4. Owner decisions still pending

1. Issue 1247 (in PR #1295): (a) put the Feedback page (/feedback, the internal Wingman feedback corpus) in the nav menu? Recommended NO - keep it reachable by address only. (b) Delete the Lists page and route? Recommended YES - it was already hidden/commented out and slated for removal. The worker built #1295 WITH these defaults; confirm or change before merging.
2. Issue 1296 item 4: recover the "My order" grouping feature (group by computer, then cc-director port, then that director's own order)? It may have been an experiment. Currently SKIPPED. Owner decision needed before recovering.

---

## 5. Wave 2 work NOT started (GitHub issues, ready for development)

- 1242 - Terminal room: collapsible rails, full-screen, session name in the header.
- 1248 - Loading/empty/error states app-wide + keyboard + announcements + auth screens (consumes the 1244 kit).
- 1249 - Attention view "needs you" group in wait-time order (mirror the mobile oldest-first; manual rail order stays sacred).
- 1251 - Rename box must not freeze polling (fix on the post-stack surface).
- 1241 - Finish Gateway-owns-presentation (phased; each phase shippable).
- 1256 - Command-center home (after 1239/#1302 and 1244).
- 1259 - Jump to a session by number.
Parked (good-idea, NOT in scope): 1258 (triage from the rail), 1260 (fleet content search).

---

## 6. cc-director problems found ("the cc-director is wrong" cleanup) - file/fix these

1. Idle supporting sub-agents go RED. A controlled/supporting worker that finishes its turn and sits in WaitingForInput flips to red "needs you" and breaks through - three workers went red just waiting for the Manager. A supporting worker awaiting its controller must stay recessive; red should mean genuine human-attention-needed only. This is the "two workers red, which should never happen" the owner flagged. FILE AN ISSUE.
2. Self-teardown is unshipped. `cc-devthrottle session done` does not exist in the installed CLI (it is the still-open PR #1262 / issue #1285). Workers cannot self-reap; the Manager must delete each via the Director API: DELETE http://127.0.0.1:7879/sessions/{full-session-id} (returns {"killed":true,"removed":true}). Ship #1262 so the reap-on-merge rule is not manual.
3. Local-commits-lost (root cause of the #1296 regression). QA fixes were committed to a local branch and never pushed; the pull requests merged from the pushed branches, so main rebuilt the features WITHOUT the fixes and a clean redeploy resurrected the bugs. Prevention is issue #1297's rule, now adopted: a worker's branch must be ON ORIGIN before its work is accepted, push every commit immediately, and deploy only from pushed refs.
4. Shared-tree git-clean race (#1271). A concurrent session ran git clean in the shared working tree mid-mission and deleted the untracked mission documents; they were recovered from the insurance snapshot origin/wip/shared-tree-snapshot-2026-07-10 (commit 27f42b0a). Never git clean / reset --hard / add -A / checkout -b in the shared tree; each session works in its own worktree+branch.
5. Flaky tests. CcDirector.Gateway.Tests StreamModeOff_DoesNotMapTheHub and LauncherStreamIntegrationTests.StreamModeOff_DoesNotMapTheLauncherHub fail intermittently on main (roughly half the runs), unrelated to the change under test; every mission pull request needed CI reruns. File an issue to stabilize them.

## 7. Worker skill gap ("this should have been in our skill")

The worker/manager skill must enforce: (a) a worker's branch is on origin before its work is accepted; (b) each worker is reaped the instant its pull request merges; (c) finished/idle workers are never left hanging (they go red and clutter the owner's attention). Update the skill so idle supporting workers are reaped and never surface as red.

---

## 8. Sessions

- This mission's five worker sessions: ALL REAPED (issues 1239, 1247, 1257, 1266-tab, 1296).
- Manager session 2e9e00d2 ("Manager: Cockpit Improvement") and Architect session 0c9d28b7 ("Architect: Cockpit Improvement"): ready to be closed/moved once this handover is accepted.
- NOT part of this mission - do NOT touch (they belong to other work): bf08c750 (installer pre-release #1294), 18c54949 (cockpit fix), 60566ccf (Terminal table wrap bug), 702d6b8a (numbers), e9f5f107 (sound).

## 9. Key locations

- Repo (shared working tree): D:\ReposFred\devthrottle. HEAD is on the stale branch feat/desktop-dictation-orange-rail - always work from origin/main.
- Latest origin/main at halt: run `git fetch origin && git rev-parse origin/main`.
- Gateway install: C:\Users\soren\AppData\Local\cc-director\gateway (exe + wwwroot\c cockpit + wwwroot\m mobile + ffmpeg.exe).
- Director Control API: http://127.0.0.1:7879 (also the CC_DIRECTOR_API env var).
- Insurance snapshot of pre-clean mission docs: origin/wip/shared-tree-snapshot-2026-07-10 (commit 27f42b0a).
- Lost-QA-fix backup branch: origin/backup/issue-1215-cockpit-local-2026-07-11 (the source for #1296; item 4 "My order" = commit 1ecdcdc1, still unrecovered).
