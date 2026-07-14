# Gateway Cleanup - Phase 0 to Phase 1 Deletion Checkpoint

Status: assembled 2026-07-12 by the Manager (session 432d5006). This is the go/no-go package for the
Architect (session 51f1898e) to review and carry to Soren. It is presented BEFORE any deletion, build, or
launch. Nothing in the deletion program has been started; this document is the gate.

Phase 0 is complete and merged to origin/main: every unary-shaped Director read and write now rides the
tunnel dispatch (the spine plus waves 1, 2, and 3), the up-stream primitive and the four connection-bound
stream verbs are in, and the eight Director-local verbs stay in-process by design. All additive, no
deletions. This checkpoint covers the deletion program (Phase 1 Director floor, then the Gateway-side
removals in Phases 2 and 3) that the mission now unlocks.

The package has five parts, in the order the Architect requested.

## 0-RESOLVED. Wave 4 closed both findings; Phase 1 is now clear to cut (2026-07-12)

UPDATE after the Architect's ruling and Wave 4: both findings below are RESOLVED and merged to main, so the
deletion is unblocked (still behind Soren's gate). Kept below for the record.

- FINDING A is adopted: the floor is SIX (fleet-preamble-hook-output kept, ratified by the Architect).
- FINDING B is closed: the ten orphans are handled - Wave 4a (#1437, merged 65a87d12) added tunnel verbs for
  the seven clear-cut ones (handovers-list, handovers-content, repo-delete, interrupted-dismiss,
  interrupted-remove, backfill-numbers, screenshot-delete); Wave 4b (#1435, merged 867d71c1) made missions
  GATEWAY-NATIVE (Gateway mission store + endpoints + the create MissionName plumbing + the cc-devthrottle
  re-point), so the Director /missions routes are now a clean DELETE (their function lives at the Gateway).
  Nothing a real client or the Gateway needs is orphaned anymore.

So the deletion inventory below now has ZERO risk items: every DELETE route maps to a tunnel verb, an
up-stream primitive, the pushed store, a Gateway-native surface, a documented drop, or the Phase-4 config
deferral. Phase 1 (the Director floor deletion) is clear to proceed on slot 5 per Section 4, pending Soren's
go/no-go which the Architect carries.

## 0. TWO FINDINGS THAT NEEDED A DECISION BEFORE PHASE 1 (surfaced by the coverage proof) - now RESOLVED above

The exact inventory (re-verified against current code; the mission-brief appendix had drifted and
undercounted) turned up two things that changed the plan, and both were the Architect's/Soren's call:

- FINDING A - the floor is SIX items, not five. The inventory found a 6th agent-lifecycle route the
  appendix missed: `GET /sessions/{sid}/fleet-preamble-hook-output` (ControlEndpoints.cs:299), which the
  installed Claude hook (the bash/curl variant) calls at `ClaudeHookInstaller.cs:86` - it returns the
  preamble wrapped in Claude's `hookSpecificOutput` envelope. It is Director-local agent IPC exactly like
  `fleet-preamble` and belongs on the floor. RECOMMENDATION: keep it (floor becomes six).

- FINDING B - TEN routes are BLOCKERS. Ten routes have a real remote consumer (a Gateway
  `DirectorEndpointClient` method, a web client, or a command-line tool) but NO tunnel verb yet and no
  documented drop. Phase 1 cannot delete them until each gets a verb or a conscious drop decision:
  1. `DELETE /screenshots/file` (:2387) - web client deletes a screenshot; no verb.
  2. `DELETE /repos` (:2499) - Gateway `DeleteRepoAsync`; no verb.
  3. `GET /handovers` (:2677) - Gateway `ListHandoversAsync` (the saved-handover-docs list, distinct from
     the per-session `handover` info verb); no verb.
  4. `GET /handovers/content` (:2744) - Gateway `GetHandoverContentAsync`; no verb.
  5. `DELETE /interrupted/{deadDirectorId}/{deadPid}` (:2935) - Gateway `DismissInterruptedAsync`; no verb
     (the interrupted READ is covered by `interrupted-list`; only its two DELETE mutations are orphaned).
  6. `DELETE /interrupted/{deadDirectorId}/{deadPid}/sessions/{sessionId}` (:2945) - Gateway
     `RemoveInterruptedSessionAsync`; no verb.
  7. `POST /missions` (:1184), `GET /missions` (:1198), `GET /missions/{mid}` (:1206) - the `cc-devthrottle`
     tool uses these; Director-local MissionStore, no verb, and the Gateway-native mission store is not yet
     defined.
  8. `POST /admin/backfill-numbers` (:2964) - the Gateway proxies to it via `/directors/{id}/backfill-numbers`;
     no verb.
  RECOMMENDATION: a small WAVE 4 that adds tunnel verbs for the clear-cut ones (screenshot-delete, repo-delete,
  handovers-list, handovers-content, interrupted-dismiss, interrupted-remove, backfill-numbers) and a design
  decision on the missions trio (Gateway-native mission store vs a Director verb) - all still additive, no
  deletions - THEN Phase 1. This is exactly the orphan the coverage proof exists to catch before we cut.

## 1. Exact deletion inventory (file:line)

The full re-verified inventory (146 routes total; Director routes classified KEEP-FLOOR / DELETE-with-verb /
DIRECTOR-LOCAL / DROP / RISK; DirectorEndpointClient and every call site; the catch-all and the nine HTTP
fallbacks; the dialing machinery with front-door 443 explicitly kept) is in the Appendix at the end of this
document. Summary of the Director floor that REMAINS (everything else on the Director HTTP surface is deleted):

1. `GET /healthz` (ControlEndpoints.cs:87) - liveness for the launcher and local operator.
2. `POST /shutdown` (:2974) - graceful self-shutdown (the launcher's DirectorSupervisor and the test teardown).
3. `POST /reconnect` - NEW in Phase 1 (does not exist yet): force the Director to bounce the tunnel.
4. `POST /sessions/{sid}/claude-hook` (:2228) - the installed agent hook posts lifecycle events here.
5. `GET /sessions/{sid}/fleet-preamble` (:268) - the installed agent hook reads its preamble here.
6. `GET /sessions/{sid}/fleet-preamble-hook-output` (:299) - FINDING A: the Claude bash-hook variant reads
   the preamble here; Director-local agent IPC, kept on the floor.

## 2. Re-confirm grep result: wingman-act and recovery-prompt

The Architect flagged these two (their names suggested a possible driver consumer we could have missed). I
re-ran the consumer grep across client-core, the Gateway (DirectorEndpointClient and any dial), the
command-line tools, and Director-internal/driver source.

RESULT - NO CONSUMER FOUND for either, in any source:

- `POST /wingman/act` (`/wingman/act`): zero matches in packages/client-core/src, src/CcDirector.Gateway,
  tools, or src/CcDirector.Core / src/CcDirector.ControlApi source. No client fetch, no DirectorEndpointClient
  method, no command-line call, no internal caller of the route.
- `POST /recovery-prompt` (`/recovery-prompt`): zero SOURCE matches in the same set. The only matches are
  inside compiled `.dll` build artifacts under bin/ and obj/ (the embedded string constant), which are not
  consumers.

Conclusion: both are confirmed Director-local. Their ROUTES are safe to delete in Phase 1; their handler
methods stay in-process for the desktop app / any internal direct call (deleting a route registration does
not remove the handler or the feature). No remote consumer is orphaned.

## 3. Coverage proof (nothing a real client or the Gateway needs is orphaned)

The coverage cross-check (full table in the Appendix) maps every DELETE route to its replacement:

- COVERED: the 14 session reads, 7 catalog reads, 23 session/director writes, 12 queue/git verbs, 2 unary
  byte verbs, 3 streams (up-stream primitive), the roster (up-tunnel pushed store), the 5 fleet-messaging
  routes (Gateway-native), and the 8 Director-local verbs (route dropped, handler kept). Every one of these
  was exercised with per-verb parity tests in the Phase 0 waves, so the replacements are verified, not assumed.
- DROP (documented, no consumer): the local desktop UI (`/`, `/login`, `/logout`, `/view`, `/fanout-local`),
  the xterm/dictate static assets, the local voice-engine routes, the Gateway-native `/tts` + `/voice-turn`
  duplicates, and the verify/verify-ws handshake (deleted in Phase 3, coordinated with registration).
- DROP (added 2026-07-13, Phase 2 PR E-B): the Director SSE endpoint `POST /sessions/{sid}/voice-turn`
  (`CcDirector.ControlApi/VoiceTurnEndpoint.cs`, issue #351). Its ONLY driver was the Gateway async voice-turn
  surface (`GatewayVoiceTurnEndpoint`), which is client-dead (only the retired native MAUI client called it;
  cockpit + mobile use `/wingman/voice-turn`) and was RETIRED + deleted in PR E-B. The Director route now has
  zero callers, so it is a clean DROP at the cut - not on the floor, not given a tunnel verb.
- DEFERRED to Phase 4 (not a Phase 1 delete): the settings / agents / tools / workspaces / scheduler config
  surface (28 routes) - reached today via the `/directors/{id}/settings` proxy and the `cc-settings-api` skill.
- NOT COVERED = the 10 RISK routes in Section 0, Finding B. These are the only orphans, and they are the
  gate: Phase 1 must not delete them until each has a verb or a conscious drop. Everything else is clean.

So the honest coverage answer is: the main read/write surface is fully covered and verified; ten routes with
real consumers are NOT yet covered and are blockers; the rest are documented drops or Phase-4 deferrals.

## 4. Slot-5 verification plan (Phase 1 Director floor)

The big-bang cut is proven on ONE test Director on slot 5, never on the user's Directors. Per CLAUDE.md
rule 0b, the test Director is launched ONLY via the Windows Task Scheduler, never from this agent's process
tree (a nested pseudo-console kills grandchild agents).

Build and launch:

1. Build the stripped Director to slot 5: `scripts/local-build-avalonia.ps1 -Slot 5` (produces
   `local_builds/cc-director5.exe`). Slot 5+ is reserved for agent test Directors; the user's main build and
   slots 1-4 are never touched or killed.
2. Point the `cc-director-launch` scheduled task at `cc-director5.exe` with its WorkingDirectory set (the
   idempotent one-time registration in CLAUDE.md 0b), then `Start-ScheduledTask -TaskName "cc-director-launch"`.
   It boots under svchost (outside the agent ConPty), so any agent sessions it spawns have clean stdio.
3. Read the allocated Control API port from the slot-5 Director log line
   `[ControlApiHost] Kestrel listening on http://0.0.0.0:<port>`.

End-to-end proof - ALL of this driven THROUGH THE TUNNEL (the browser/Gateway path), with the slot-5
Director exposing ONLY the five-item floor:

- Roster: the Gateway `/sessions` aggregation shows the slot-5 Director's sessions from the pushed store
  (no HTTP pull to the Director).
- Terminal stream: open the terminal WebSocket to the Gateway; frames arrive via the Director up-stream.
- A prompt: send a prompt through the Gateway; it rides the `prompt` tunnel verb.
- A read: fetch `turns` through the Gateway; it rides the `turns` tunnel verb.
- A file view: open a session file through the Gateway; it rides the `read-file` up-stream.
- Floor-only proof: curl each DELETED route on the slot-5 Director loopback and confirm 404 (the route is
  gone), while `healthz` / `shutdown` / `reconnect` / `claude-hook` / `fleet-preamble` still answer.

Teardown: `POST /shutdown` to the slot-5 Director (graceful, so it leaves no phantom crash-journal entry);
never force-kill. Only ever kill a process whose path is the slot-5 exe, and only as a last resort.

## 5. Blast radius and rollback

Blast radius:

- The deletions live in the shared codebase, but only the slot-5 test Director is BUILT and RUN from the
  post-deletion code for this proof. The user's daily-driver Director (main build) and slots 1-4 keep
  running the pre-deletion build and are unaffected until the explicit fleet rollout (a separately gated
  step, Phase 6 / "each rollout to real machines").
- The browser-facing and phone-facing contracts do not change; only the hidden Gateway-to-Director leg
  changes. The Gateway front-door 443 mapping is explicitly kept.
- The Gateway-side removals (DirectorEndpointClient, the catch-all, the dialing machinery) are Phases 2 and
  3; each has its own proof (cockpit and mobile drive slot 5 with no Director HTTP dial anywhere, verified
  by log and by grep for the removed client), and none of it rolls to production without the rollout gate.

Rollback:

- Pre-merge: the deletions are built on a branch; if the slot-5 proof fails, the branch is abandoned with
  zero production impact.
- Post-merge, pre-rollout: production Directors were never rebuilt, so they are unaffected; a revert of the
  deletion pull requests restores the routes on main. Because the cut is big-bang, a revert is all-or-nothing
  per phase, which is why each phase is proven on slot 5 before the next begins.
- The rollout to the user's real machines is its own hard gate (Architect to Soren) and is not part of this
  checkpoint.

## Recommendation

Phase 0 (the additive verb migration) is complete and verified. But the coverage proof did its job and
surfaced TEN routes with real consumers that are not yet covered (Section 0, Finding B), so Phase 1 is NOT
clear to start as-is. Recommended sequence, all still additive before any deletion:

1. A small WAVE 4 (additive, no deletions): add tunnel verbs for the seven clear-cut orphans (screenshot-delete,
   repo-delete, handovers-list, handovers-content, interrupted-dismiss, interrupted-remove, backfill-numbers),
   and settle the missions trio with the Architect (Gateway-native mission store vs a Director verb).
2. Adopt the six-item floor (Finding A: keep `fleet-preamble-hook-output`).
3. THEN Phase 1 (the Director floor deletion) on slot 5 with the end-to-end proof in Section 4, returning for
   the Architect's review of the slot-5 result before the Gateway-side removals (Phases 2 and 3).

No deletion, build, or launch happens until the Architect reviews this package and carries Soren's go/no-go
back. This is Soren's gate.

## Architect review - SETTLED 2026-07-12 (session 51f1898e)

Reviewed. The checkpoint is thorough and the coverage proof did exactly its job: it caught real orphans BEFORE
any deletion. Rulings below. Net: Phase 1 is NOT clear to cut yet; one small additive WAVE 4 closes the
orphans first, then the Phase 1 deletion itself returns as Soren's gate.

FINDING A - RATIFIED. `fleet-preamble-hook-output` stays on the floor; the floor is SIX, not five. It is the
same Director-local agent-lifecycle IPC as `fleet-preamble` (the installed Claude bash-hook reads its preamble
here in Claude's hookSpecificOutput envelope, ClaudeHookInstaller.cs:86), so it falls squarely under the
already-approved Open Decision 1 (the hook endpoints are Director-local by nature and stay on the loopback
floor). Deleting it would break Claude session startup via the bash hook. No Soren decision needed - it is
inside the approved floor rationale, just a sixth endpoint the appendix missed.

FINDING B - the ten orphans:
- The SEVEN clear-cut orphans (screenshot-delete, repo-delete, handovers-list, handovers-content,
  interrupted-dismiss, interrupted-remove, backfill-numbers) each have a real remote consumer and fit the
  unary tunnel shape. APPROVED: give each a tunnel verb in an additive WAVE 4, exactly like the Phase 0 lifts
  (lift-not-rewrite, the REST route and the verb share one core, per-verb parity tests). Additive, so under the
  Option A commit policy the Manager builds and merges it without a per-commit relay.
- The MISSIONS trio (POST /missions, GET /missions, GET /missions/{mid}) - DESIGN RULING: missions are a
  FLEET-level concept (a mission spans sessions across Directors and machines - one mission's Architect,
  Manager, and workers can be on different machines; missions nest), so they belong GATEWAY-NATIVE, the same
  category as fleet messaging and scheduling. They must NOT become a Director tunnel verb - that would entrench
  a fleet concept as Director-local, the exact "easy path that never heals" anti-pattern the brief warns
  against, and it contradicts the mission's own Definition of Done ("the command-line tools go through the
  Gateway"). So: build a Gateway-native mission store plus its endpoints and re-point the `cc-devthrottle`
  mission verbs at the Gateway (additive, part of Wave 4 / the Phase 4 command-line re-point); the Director's
  /missions routes are then DROPPED in Phase 1 (not floor, not a Director verb), once the Gateway store exists
  so nothing is orphaned. This is REQUIRED by the Definition of Done, not scope creep - the brief already
  commits command-line tools to the Gateway; missions were simply under-specified in the appendix. (Soren
  informed; he may veto in favour of a temporary /missions floor exception if he wants to defer the Gateway
  store, but the recommended and DoD-required end state is Gateway-native.)
  - MISSIONS attach mechanism (Wave 4b design nuance, SETTLED 2026-07-12): making missions Gateway-native
    breaks spawn-into-mission, because the Director create verb today resolves the mission NAME from the
    Director-local MissionStore and rejects an unknown mission. Ruling: Option A. The GATEWAY (the source of
    truth) resolves AND validates the mission before spawn and passes BOTH MissionId and MissionName on the
    create command - add an optional MissionName to NewSessionRequest / the create verb. The Director stamps
    session.AttachToMission(id, name) DIRECTLY from the passed name with NO local-store lookup on the Gateway
    path; mission-existence validation (reject unknown -> BadRequest) MOVES to the Gateway where the store
    lives. Option B (overlay the name only during roster aggregation, no Director change) is rejected: it would
    leave the Director's own session record and desktop UI without the mission name. A local-store lookup when
    MissionName is absent is allowed ONLY as an explicitly transitional bridge for old Director-store missions
    during Wave 4 -> Phase 1, and is REMOVED when Phase 1 drops /missions and the MissionStore. End state
    (post-Phase-1): the Director never resolves a mission name locally - it only stamps what the create command
    carries. No permanent fallback.

SEQUENCE - APPROVED: WAVE 4 (additive - the seven orphan tunnel verbs, plus the Gateway-native mission store
and the cc-devthrottle mission re-point) and adopt the six-item floor, THEN Phase 1 on slot 5. No deletions in
Wave 4. The Phase 1 DELETION itself (and the slot-5 build and launch) remains SOREN'S GATE under Option A: the
Manager brings the completed Wave 4 plus the now-unblocked Phase 1 plan back to the Architect, who carries the
deletion go/no-go to Soren before any route is deleted or any slot-5 build or launch happens.

The rest of the package is sound and approved as the Phase 1 approach: the deletion inventory, the
wingman-act / recovery-prompt CLEAN re-confirm, the slot-5 plan (build to slot 5, launch ONLY via the
cc-director-launch scheduled task, the end-to-end tunnel proof plus the floor-only 404 check), and the
blast-radius / rollback. Proceed with Wave 4 now.

---

## Appendix: full inventory pass output (file:line, re-verified against current origin/main)

The following is the exhaustive inventory. Line numbers were re-verified against the current code (the
mission-brief appendix had drifted +20 to +60 lines and undercounted the route total as 118; the true count
is 146).

### A1 - Director routes (src/CcDirector.ControlApi/), 146 total Map* registrations

Composition root wires them at ControlApiHost.cs:419-455. No MapFallback / MapMethods / static-file
middleware, so this is exhaustive. Per-file counts: ControlEndpoints.cs 104, AgentsEndpoint 11,
SettingsEndpoint 6, ToolsEndpoint 5, DictationEndpoint 4, TerminalStreamEndpoint 4, WorkspacesEndpoint 3,
SchedulerEndpoint 2, and 1 each in FactsEndpoint / DispatchEndpoint / ClaudeTranscriptsEndpoint /
SessionHistoryEndpoint / SessionContextEndpoint / SessionUsageEndpoint / VoiceTurnEndpoint.

FLOOR (keep): GET /healthz (ControlEndpoints.cs:87); POST /shutdown (:2974); POST /sessions/{sid}/claude-hook
(:2228); GET /sessions/{sid}/fleet-preamble (:268); GET /sessions/{sid}/fleet-preamble-hook-output (:299,
emitted by the installed Claude hook at ClaudeHookInstaller.cs:86); plus NEW POST /reconnect (not present today).

DELETE -> SessionReadExecutor verbs: GET /sessions/{sid} (:245) snapshot; /buffer (:1277); /buffer/html (:1304);
/turns (:1325); /summary (:1341); /recap (:1544); /turn-summaries (:1892); /usage (SessionUsageEndpoint.cs:22);
/context (SessionContextEndpoint.cs:25); /history (SessionHistoryEndpoint.cs:29); /github-urls (:1039);
/wingman (:1105) wingman-view; /wingman/explain (:877) wingman-explain; /handover (:1365) handover.

DELETE -> CatalogReadExecutor verbs: GET /sessions/{sid}/git (:1753) git-status; /coaching/categories (:2644);
/claude-sessions (:2660); /interrupted (:2922) interrupted-list; /fs/list (:2769) fs-list; /facts
(FactsEndpoint.cs:32); /repos (:2485) repos-list.

DELETE -> SessionWriteExecutor verbs: POST /sessions/{sid}/prompt (:2011); /interrupt (:2139); /escape (:2161);
/hold (:964); DELETE /sessions/{sid} (:2834) kill; POST /sessions (:2787) create; /wingman/goal (:1123);
/role (:1159) set-role; /mission (:1222) attach-mission; /resize (:2261); /clear-context (:2204);
/history-picker (:2182); /mobile-mode (:897); /voice-mode (:932); /wingman-enabled (:1004); /relink (:1806);
/request-deletion (:2873) + DELETE /request-deletion (:2898) cancel-deletion; /execute-action (:3005);
POST /handover (:1975) handover-generate; /wingman/ask (:786) wingman-ask; /recap (:1564) recap-generate.
(PATCH /sessions/{sid} patch has NO Director route today - cockpit reaches it via the catch-all;
terminal-input has no REST route - it is the Phase-2 keystroke pump.)

DELETE -> QueueGitExecutor verbs: GET /sessions/{sid}/queue (:2072) queue-read; POST /queue (:2075) queue-add;
DELETE /queue/{itemId} (:2081) queue-remove; POST /queue/{itemId}/move-up (:2094); move-down (:2100);
DELETE /queue (:2106) queue-clear; POST /queue/{itemId}/send (:2116); POST /git/stage (:1794) /unstage (:1796)
/discard (:1798) /commit (:1800); POST /sessions/github (:2807) create-from-github. (queue-update PATCH has
no Director route - reached via the catch-all.)

DELETE -> SessionByteExecutor verbs: GET /screenshots (:2349) screenshots-list; POST /sessions/{sid}/upload-image
(:2292) upload-image.

DELETE -> up-stream primitive: GET /sessions/{sid}/stream WS (TerminalStreamEndpoint.cs:56) open-terminal-stream;
GET /sessions/{sid}/file (:1086) read-file; GET /screenshots/file (:2369) screenshot-file.

DELETE -> up-tunnel pushed store: GET /sessions roster (:230) - served by PushSnapshot/PushDelta, no unary verb.

DELETE -> Gateway-native fleet messaging: GET /fleet/sessions (:351); POST /fleet/send (:379); /fleet/broadcast
(:455); /fleet/ask (:635); /fleet/spawn (:739).

DIRECTOR-LOCAL (route deleted, handler kept) - the 8 confirmed-local: POST /sessions/{sid}/wingman/act (:822);
GET /brief (:1386); POST /chat (:1704); GET /handover-context (:1496); POST /turn-summaries (:1911);
/rule-violations (:1728); /recovery-prompt (:1825); /state-vote (:1929).

DROP (local UI / static / handshake / dead legacy - no client, no Gateway caller): GET / (:157); /login (:168);
POST /login (:178); GET /logout (:208); /sessions/{sid}/view (:214); POST /fanout-local (:2398, only
Web/manager.html:792); GET /verify/{nonce} (:106) + /verify-ws/{nonce} (:122, Phase 3, breaks registration -
coordinate); xterm.js/css/canvas (TerminalStreamEndpoint.cs:49/51/53); dictate.html/worklet/overlay
(DictationEndpoint.cs:70/76/82); GET /dictate WS (DictationEndpoint.cs:90); POST /voice/command (:1605) +
/voice/status (:1626) + /voice/utterance (:1636) + PUT chunk (:1647) + complete (:1667); POST /tts (:1839) +
/tts/status (:1873); POST /sessions/{sid}/voice-turn (VoiceTurnEndpoint.cs:50); POST /repos (:2512);
GET /repos/overview (:2563); POST /handovers (:2699); DELETE /handovers (:2724); GET /claude-transcripts
(ClaudeTranscriptsEndpoint.cs:21); POST /dispatch (DispatchEndpoint.cs:26).

PHASE-4 config surface (NOT a Phase 1 delete - listed so it is not mistaken for one): SettingsEndpoint.cs
GET/PUT /settings (37/39) + detect/test (56/70/84/97); AgentsEndpoint.cs 11 routes (110/117/126/185/198/214/
242/275/299/335); ToolsEndpoint.cs 5 (35/46/61/77/114); WorkspacesEndpoint.cs 3 (25/31/39); SchedulerEndpoint.cs
2 (24/38).

RISK - real caller, NO tunnel verb, NO documented drop (the Phase 1 blockers, Section 0 Finding B):
DELETE /screenshots/file (:2387, web client); DELETE /repos (:2499, Gateway DeleteRepoAsync);
GET /handovers (:2677, Gateway ListHandoversAsync); GET /handovers/content (:2744, Gateway GetHandoverContentAsync);
DELETE /interrupted/{d}/{p} (:2935, Gateway DismissInterruptedAsync); DELETE /interrupted/{d}/{p}/sessions/{s}
(:2945, Gateway RemoveInterruptedSessionAsync); POST /missions (:1184) + GET /missions (:1198) + GET /missions/{mid}
(:1206, cc-devthrottle); POST /admin/backfill-numbers (:2964, Gateway proxy).

### A2 - Gateway dialing machinery (src/CcDirector.Gateway/)

DirectorEndpointClient (Discovery/DirectorEndpointClient.cs, whole file, class :14, ctor :21) - DELETE. Methods:
VerifyCallbackAsync(65), VerifyStreamCallbackAsync(110), GetHealthAsync(165), GetHealthDetailedAsync(183),
ListSessionsAsync(206), ListSessionsWithStatusAsync(227), GetSessionAsync(248), GetWingmanAsync(263),
AskWingmanAsync(283), SetWingmanGoalAsync(310), SetRoleAsync(334), SetHoldAsync(358), KillSessionAsync(385),
RequestSessionDeletionAsync(408), CancelSessionDeletionAsync(435), PatchSessionAsync(457), GetBufferAsync(481),
GetTurnsAsync(505), GetHistoryTurnsAsync(~528, NOT in the appendix), PostPromptAsync(602), PostInterruptAsync(629),
PostEscapeAsync(643), UploadImageAsync(662), ListReposAsync(698), DeleteRepoAsync(711), GetFactsAsync(728),
ListCoachingCategoriesAsync(741), ListClaudeSessionsAsync(754), ListHandoversAsync(767), GetHandoverContentAsync(780),
ListDirectoryAsync(793), CreateGitHubSessionAsync(809), CreateSessionAsync(829), GetSummaryAsync(849),
GetGitAsync(865), GetHandoverAsync(883), PostHandoverAsync(896), GetRecapAsync(916), PostRecapAsync(929),
GetInterruptedAsync(962), DismissInterruptedAsync(976), RemoveInterruptedSessionAsync(995), PostShutdownAsync(1011).
Call sites (re-point at DirectorCommandRouter.TrySendAsync / up-stream): GatewayHost.cs:276/427; WingmanVoiceService.cs:39/68;
WingmanTrainingStore.cs:54; Briefing/TurnEndWatcher.cs:48/59; Running/MachineSessionSpawner.cs:34;
Running/DirectorImplSessionDriver.cs:16/23; Discovery/AdvertisedEndpointMonitor.cs:37; Api/ExesEndpoints.cs:34;
Api/GatewayDictationEndpoint.cs:63/313/430/445; Api/GatewayEndpoints.cs:44/2050; Api/GatewayVoiceTurnEndpoint.cs:59/380/579;
Api/GatewayWingmanVoiceEndpoint.cs:61/506/520/623/667/676/684; Api/SessionWsProxyEndpoints.cs:50/198/288;
Api/WorkListRunnerEndpoints.cs:35.

Catch-all + SessionWsForwarder (Api/SessionWsProxyEndpoints.cs, Map :50): browser-facing STAY (GET /sessions/{sid}/stream :55;
/dictate :61; /screenshots/file :77; /screenshots :88; /directors/{id}/settings :131 config; POST /directors/{id}/backfill-numbers
:164 - see RISK). DELETE the catch-all /sessions/{sid}/{**rest} :101 (ProxyAsync dispatch :123). The Director-dialing leg
to delete: ProxyAsync :197, LocateOwningDirectorAsync :288, ForwardDestination :321, class SessionWsForwarder (ForwardAsync
:407, ForwardWebSocketAsync :416, PumpAsync :457, ForwardHttpAsync :487).

9 tunnel-verb HTTP fallbacks become stream-only (Api/GatewayEndpoints.cs, TrySendAsync anchor per verb; the HTTP else-branch
after each dies): kill :963, wingman-goal :1042, set-role :1060, hold :1086, patch :1148, prompt :1205, interrupt :1265,
escape :1281, create :1357. Router DirectorCommandRouter (Api/DirectorCommandRouter.cs, TrySendAsync/ReadBody/DescribeFailure)
is the KEEP path; wired when _streamMode on (GatewayHost.cs field :98, set :419, passed :1172/1505/1432). SendCommandAsync
(GatewayHost.cs:1646) is the tunnel dispatch (keep). Phase 6 removes the _streamMode flag.

Reachability circuit breaker (Discovery/DirectorRegistry.cs) - DELETE: consts MaxConsecutiveFailures :47,
UnreachableCooldown :50, UnreachableEvictAfter :53; class Reachability :60-66, _reach :69; ShouldProbe :227,
RecordReachable :238, WasEverReachable :251, RecordUnreachable :337; verify-state MarkTwoWayVerified :259,
MarkStreamVerified :275, RecordEndpointProbeResult :301; sweeper evict :458-473. Consumers: TurnEndWatcher.cs:136/143/146;
GatewayEndpoints.cs:419/498/500/509/511/771. DO NOT TOUCH the DIFFERENT class FleetRosterCache.cs:64/91
(RecordReachable/Unreachable) - its call sites GatewayEndpoints.cs:534/562 stay (only ~25 lines from the registry ones -
distinguish registry.* vs rosterCache.*).

AdvertisedEndpointMonitor (Discovery/AdvertisedEndpointMonitor.cs, whole file, ctor :37) - DELETE. Refs: GatewayHost.cs
field :343, construction :889 (only two now, not the appendix's five).

verify / verify-ws trio - DELETE: POST /directors/{id}/verify (GatewayEndpoints.cs:378, calls VerifyCallbackAsync :392 /
VerifyStreamCallbackAsync :404); client methods DirectorEndpointClient.cs:65/110; registry MarkTwoWayVerified (:259) /
MarkStreamVerified (:275).

Director control-endpoint advertisement (TailnetEndpoint/ControlEndpoint selection) - DELETE: GatewayHost.cs:535
(d.TailnetEndpoint ?? d.ControlEndpoint), :793 ((d.ControlEndpoint ?? d.TailnetEndpoint ?? "")), :1109
(Registry.Get(directorId)?.ControlEndpoint); SessionWsProxyEndpoints.cs:321 ForwardDestination; GatewayEndpoints.cs
DeriveDirectorBaseUrl (~:2050-2061). DirectorDto.ControlEndpoint/TailnetEndpoint/AdvertisedEndpoint* fields die with it.

TailscaleServeProvisioner (Tailscale/TailscaleServeProvisioner.cs): KEEP front-door 443 - FrontDoorHttpsPort const :53,
FrontDoorWatchInterval :73, _frontDoorTimer :82, front-door serve :122, timer init :142, WatchFrontDoorCore :285,
reconcile branches :189-196/:307-310/:256-261, ReconcileActions :276, Dispose :426. DELETE per-Director-port mappings -
DirectorPortMin/Max :65-66, _portsById :80, OnDirectorAdded/Removed subscriptions :117-118 (+ unsubscribe :427-428),
HandleAdded loop :130 + per-director ShouldMap :170, HandleAdded :323, HandleRemoved :337, ShouldMap :351.
(LegacyCockpitPort :60 is a separate legacy concern - leave for its own decision.)

### A3 - Coverage summary
COVERED (verified by Phase 0 parity tests): 14 session reads, 7 catalog reads, 23 writes, 12 queue/git, 2 unary bytes,
3 streams (up-stream), roster (pushed store), 5 fleet messaging (Gateway-native), 8 Director-local (handler kept).
DROP: the local UI/static/voice/handshake set above. DEFERRED: the Phase-4 config surface. NOT COVERED = the 10 RISK
routes (Section 0 Finding B) - the only orphans, and the Phase 1 blockers.


