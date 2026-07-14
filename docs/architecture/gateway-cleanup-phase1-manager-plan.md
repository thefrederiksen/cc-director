# Gateway Cleanup - Phase 1 Manager execution plan (for the Architect, before any cut)

Author: Manager session d1286c9f ("Gateway Cleanup - Manager"), 2026-07-12, machine SOREN_NORTH.
Status: PRESENTED TO THE ARCHITECT (51f1898e) FOR A SEQUENCING RULING. No worktree created, no branch cut,
no route deleted, no build, no launch. This is the plan and one design fork that must be settled first.

## 1. State confirmed (nothing lost in the phase-boundary reset)

- All additive work is merged to origin/main. Local main is 867d71c1; origin/main tip is ffbc45bb (a few
  intervening commits from other missions sit on top of Wave 4b - I will branch off origin/main, not local).
- Wave 4a (#1437, 65a87d12) and Wave 4b (#1435, 867d71c1) are in: the seven orphan verbs are tunnelled and
  missions are Gateway-native. The deletion checkpoint is 0-RESOLVED (both findings closed).
- The floor is SIX (Finding A ratified): GET /healthz, POST /shutdown, POST /reconnect (NEW), POST
  /sessions/{sid}/claude-hook, GET /sessions/{sid}/fleet-preamble, GET /sessions/{sid}/fleet-preamble-hook-output.
- Soren's GO for BUILDING and PROVING Phase 1 on slot 5 is given; the deletion MERGE stays his gate through you.

## 2. THE FORK (must be settled before I structure the deletion): the Gateway does NOT yet read/stream through the tunnel

I re-verified the current Gateway code against the slot-5 proof. The proof as written in the checkpoint
(Section 4) presupposes the Gateway serves the browser-facing legs THROUGH THE TUNNEL. It does not yet.

Evidence (current origin/main Gateway):

- Only NINE verbs ride the tunnel on the GATEWAY side, all writes/commands, via
  DirectorCommandRouter.TrySendAsync(sendCommand, ...): kill, wingman-goal, set-role, hold, patch, prompt,
  interrupt, escape, create (GatewayEndpoints.cs:1005/1084/1102/1128/1190/1247/1307/1323/1399). sendCommand is
  non-null only when streamMode is on (GatewayHost.cs:1198), and streamMode is OFF in production
  (GatewayHost.cs:1208 comment: "which is off in production").
- Every READ (turns, buffer, git-status, usage, context, history, summary, handover, ...), the terminal
  WebSocket stream, the screenshot bytes, and the file view are served by SessionWsForwarder, which DIALS the
  owning Director's HTTP/WS endpoint. The terminal stream (SessionWsProxyEndpoints.cs:55), dictate (:61),
  screenshots (:77/:88), and the generic catch-all /sessions/{sid}/{**rest} (:101 -> ProxyAsync ->
  ForwardDestination) are NOT gated by streamMode and do NOT touch the up-stream.
- The up-stream primitive (DirectorStreamFrame, StreamUp hub, GatewayStreamRegistry, the four connection-bound
  stream verbs) is built, hub-wired, and unit-tested (Phase 0, #1414), but NO browser-facing Gateway leg
  consumes GatewayStreamRegistry. Grep for StreamUp/GatewayStreamRegistry hits only GatewayHost, DirectorHub,
  GatewayStreamRegistry, IStreamSink - no terminal/file endpoint. Wiring the browser legs onto the up-stream and
  the read verbs is Phase 2, and Phase 2 is not built.

Consequence: a floor-only slot-5 Director driven through the Gateway would pass ONLY roster (served from the
Director push cache - up-tunnel, real) and prompt (tunnel verb). turns (read), the terminal stream, and the
file view would 404, because the Gateway would dial the Director HTTP routes that Phase 1 just deleted. So the
five-part end-to-end proof in the checkpoint cannot run against today's Gateway. The mission's own invariant -
"the new path is in place before the old path is deleted" - is only half-satisfied: Phase 0 put the new path on
the DIRECTOR (verbs + up-stream primitive) but left the GATEWAY still dialing HTTP for reads/streams. Deleting
the Director HTTP surface now removes a road the Gateway still drives on.

## 3. Options and my recommendation

- OPTION A (RECOMMENDED) - re-point the Gateway onto the tunnel FIRST, then delete the Director floor. Do the
  Phase 2 browser-leg re-point (terminal stream -> up-stream; reads -> tunnel read verbs; file/screenshot ->
  up-stream; replace the /sessions/{sid}/{**rest} catch-all's Director-dial with the explicit tunnel dispatch),
  all ADDITIVE under streamMode (streamMode on = tunnel, off = HTTP - both paths coexist, nothing on main
  breaks, production stays HTTP until the rollout gate). Prove on slot 5 with a FULL-surface Director + a
  streamMode Gateway that the tunnel actually carries roster + terminal + prompt + turns + file. THEN Phase 1
  deletes the Director's now-unused HTTP surface and RE-proves floor-only (the same proof, now with the routes
  gone and every deleted route 404ing). Rationale: this is the only order where the floor-only proof is
  runnable, and it honors the invariant (new road carries traffic before the old road is removed). It swaps the
  1<->2 order, or equivalently recognizes the checkpoint's slot-5 proof as a Phase-2-then-Phase-1 proof.
- OPTION B - bundle Phase 1 + the minimal Phase 2 wiring on ONE branch: delete the Director routes AND wire the
  Gateway browser legs onto the tunnel, prove floor-only end-to-end in one shot. Matches the checkpoint's proof
  literally, but mixes additive Gateway work with the destructive Director cut inside one gated merge, and is a
  larger single blast radius. Under Option A that Gateway work is a separate additive merge I can land without a
  gate, keeping the destructive cut small and clean.
- OPTION C - scope the Phase 1 proof down to what the tunnel carries TODAY (roster push + prompt); accept that
  turns/terminal/file are NOT proven until Phase 2. REJECTED as a real proof: it would merge a Director that
  breaks terminal and reads through any streamMode Gateway, and it does not exercise the mission's headline
  (terminal + file via the up-stream). A weak proof is worse than none here.

Recommendation: OPTION A. Please rule on the sequence. Everything below is the mechanics, unchanged by the
ruling except for which branch carries the deletion.

## 4. Deletion structure (Phase 1 proper, once sequence is settled)

- Own worktree off origin/main (git worktree add, a fresh branch e.g. gateway-cleanup-phase1-director-floor).
  NEVER checkout -b on the shared tree; NEVER git add -A; stage only my own files by name. The shared tree
  currently holds another mission's uncommitted CarMode work - I will not touch it.
- Scope of the cut (from the checkpoint inventory, Appendix A1):
  - KEEP the six floor routes; ADD POST /reconnect (bounce the tunnel; the only new thing on the Director).
  - DELETE every other Director route registration EXCEPT the Phase-4 config surface, which the checkpoint
    explicitly DEFERS (Settings/Agents/Tools/Workspaces/Scheduler, 28 routes, reached via /directors/{id}/settings
    and the cc-settings-api skill). CONFIRM WITH YOU: Phase 1 leaves that config surface in place (so slot 5
    answers the 6 floor routes PLUS the 28 config routes); the 404 proof targets the Phase-1-DELETED routes, not
    the config surface. The checkpoint says so (Section 3 + Appendix), but it is worth an explicit yes.
  - DIRECTOR-LOCAL (route deleted, handler kept in-process): the 8 confirmed-local verbs (wingman-act, brief,
    chat, handover-context, turn-summaries-generate, rule-violations, recovery-prompt, state-vote). Re-confirm
    wingman-act and recovery-prompt have no remote consumer at the cut (cheap insurance, already done once).
  - DROP (no consumer): local desktop UI, xterm/dictate static assets, local voice-engine routes, the
    Gateway-native /tts + /voice-turn duplicates. The verify/verify-ws handshake is coordinated with Phase 3
    (it breaks registration) - I will NOT delete verify/verify-ws in Phase 1 unless you want it folded in.
  - The in-process executor cores (Phase 0) stay; deleting a Map* registration does not remove the feature, it
    removes the network door. The tunnel verb already calls the same core.
- Likely ONE deletion pull request for the Director floor (it is mechanical route-registration removal in a few
  endpoint files), unless it proves large enough to want a small chain by endpoint file. I will keep it a single
  reviewable cut if I can.
- Build discipline before merge: PR up-to-date-with-main, AND audit-read-aware - an intervening commit that
  edits a file a source-audit test READS can red main even without touching my files (the
  TerminalPromptInjectionChokepointTests lesson). I re-check the merged state before merging.

## 5. Slot-5 build, launch, and proof (mechanics, unchanged by the ruling)

1. Build from the branch to slot 5: scripts/local-build-avalonia.ps1 -Slot 5 -> local_builds/cc-director5.exe.
   Slot 5+ only; the user's main build and slots 1-4 are never built to, never killed.
2. Launch ONLY via the cc-director-launch Windows scheduled task (CLAUDE.md rule 0b): point the task at
   cc-director5.exe with its WorkingDirectory, Start-ScheduledTask. It boots under svchost, outside my ConPty,
   so any agent session it spawns has clean stdio. I never spawn cc-director5.exe from my own process tree.
3. Read the Control API port from the slot-5 log line "[ControlApiHost] Kestrel listening on http://0.0.0.0:<port>".
4. End-to-end proof through the Gateway (a streamMode Gateway, per the ruling): roster (push) + terminal stream
   (up-stream) + prompt (tunnel verb) + turns (read verb) + file view (read-file up-stream).
5. Floor-only proof: curl each DELETED route on the slot-5 loopback -> 404; the 6 floor routes + POST /reconnect
   still answer.
6. Teardown: POST /shutdown to the slot-5 Director (graceful, leaves no phantom crash-journal entry). Never
   force-kill; if I ever must, only a process whose path is cc-director5.exe, confirmed first, last resort only.

## 6. Gate and stop point

I do NOT merge the destructive deletion until the slot-5 proof PASSES. When it passes I STOP and bring you the
proof result (the e2e run + the 404s on deleted routes + the floor-only confirmation) so you carry Soren's
OK-to-merge. If the proof FAILS I abandon the branch (zero production impact) and bring you the failure. The
additive Gateway re-point under Option A I can merge autonomously (Option A commit policy); only the Director
deletion is the gate.

## 7. The one question for you

Rule on the sequence in Section 3 (I recommend Option A: Gateway re-point first, then Director floor), and
confirm the Section 4 scope point (Phase 1 leaves the Phase-4 config surface in place; the 404 proof targets the
Phase-1-deleted routes). On your ruling I create the worktree and begin - the additive Gateway re-point first if
Option A, or the deletion branch directly if you want B/C.

## Architect ruling (sequencing fork) - SETTLED 2026-07-12 (session 51f1898e)

OPTION A - APPROVED. Excellent catch. The finding is correct and consistent with the Phase 0 deliverable: Phase
0 put the new path on the DIRECTOR (the verb dispatch + the up-stream primitive) but explicitly left the
up-stream UNWIRED into the browser-facing Gateway legs (that swap is Phase 2, stated in the phase0 protocol
doc). The Gateway still dials the Director over HTTP for every read, the terminal stream, and the
file/screenshot bytes (SessionWsForwarder + the catch-all; streamMode is off in production; only nine write
verbs ride the tunnel). So a floor-only slot-5 Director would 404 turns/terminal/file - the mission's own
invariant ("the new path is in place before the old path is deleted") is only half-satisfied, and deleting the
Director HTTP surface now would remove a road the Gateway still drives on.

Corrected sequence:
1. FIRST (additive, under the existing streamMode flag - both paths coexist, production stays on HTTP, nothing
   on main breaks): re-point the Gateway's browser-facing legs onto the tunnel - terminal stream onto the
   up-stream, reads onto the tunnel read verbs, file/screenshot onto the up-stream, and replace the
   /sessions/{sid}/{**rest} catch-all's Director-dial with the explicit tunnel dispatch. Prove on a FULL-surface
   slot-5 Director + a streamMode Gateway that the tunnel actually carries roster + terminal + prompt + turns +
   file. This is additive, so under the Option A commit policy you build and merge it WITHOUT a gate.
2. THEN (destructive, Soren's gate through me): delete the Director REST surface to the six-item floor and
   RE-prove floor-only (every deleted route 404s; the six floor routes + POST /reconnect answer). Bring me the
   slot-5 proof BEFORE merging the deletion.

This honors the invariant (the new road carries traffic before the old road is removed), keeps the destructive
cut small and clean (separate from the additive re-point), and is the only order in which the floor-only proof
is runnable. It effectively splits the original Phase 2 into an additive-wire part (done now, before the Phase 1
deletion) and a destructive delete-the-dialing-machinery part (later). Option B (bundle additive + destructive
on one branch) is rejected as a larger single blast radius that folds autonomous additive work into a gated
destructive merge; Option C (scope the proof down to roster+prompt) is rejected as a weak proof - agreed on both.

Scope confirmations:
- Phase 1 LEAVES the Phase-4 config surface (Settings / Agents / Tools / Workspaces / Scheduler, 28 routes) in
  place - CONFIRMED. Slot 5 answers the six floor routes + POST /reconnect + the 28 config routes; the 404 proof
  targets ONLY the Phase-1-deleted routes, never the config surface.
- verify / verify-ws stays for PHASE 3 - CONFIRMED. Do NOT fold it into Phase 1; it breaks Director-to-Gateway
  registration and must be removed in lockstep with the Gateway-side verify call (Phase 3).
- Re-confirm wingman-act + recovery-prompt have no remote consumer at the moment of the cut - yes, cheap
  insurance (already clean once).

Proceed: build the additive Gateway re-point first (own worktree off origin/main), prove tunnel-carries-all on a
full-surface slot 5, merge it (additive, autonomous), THEN cut the Director floor on its own branch and bring me
the floor-only proof for Soren's OK-to-merge.
