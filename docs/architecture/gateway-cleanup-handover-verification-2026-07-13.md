# Gateway Cleanup - Verification Handover (2026-07-13)

You are a FRESH verification session, started on the NEW tunnel-only cc-director (v1.1.0). Your job: independently VERIFY that the Gateway Cleanup mission was completed correctly and end-to-end, surface anything missing or broken, and drive the one known live issue (the mobile app "blinking" / sessions appearing and disappearing) to a root cause. Do NOT trust prior reports - confirm each item with your own eyes (the prior Architect caught several report-vs-reality discrepancies during the ship). Never say something works without a proof you ran yourself.

## The mission in one line
The Gateway must NEVER dial a cc-director over the network. ALL Gateway<->Director traffic rides "the tunnel" (the two-way SignalR stream; DirectorHub `/director-stream` + GatewayStreamClient). WHY: (1) kills the phone "reconnecting to this session's computer" bug (the old Gateway dialed the Director's advertised Tailscale endpoint even on the same LAN, hit a 2s timeout + circuit breaker); (2) closes the inbound network port on every client machine (the Director dials OUT, nothing dials IN).

## What was shipped (confirm each)
- **PR #1486** (squash 398c4e4a) - the destructive cut: the Gateway no longer dials Directors (DirectorEndpointClient + the whole HTTP reverse-proxy DELETED), the tunnel is MANDATORY (streamMode gate removed), Director REST cut to a small loopback floor, plus the two restorations that were folded in (repos/handover management verbs; the fleet-messaging CLI floor on the Director loopback) and the floor-only real-exe proof (all 5 points passed live, inbound port confirmed closed).
- **PR #1488** (f7032070) - Director made 100% tunnel-only: removed the streamMode gate on the Director side, RETIRED the /verify handshake (this killed a FALSE "Cannot reach the Gateway" banner), the tunnel Hello now REGISTERS the Director (RegisterFromStream), inbound Tailscale port for the Director closed.
- **PR #1489** (880255b5) - version bump to 1.1.0.
- **Release v1.1.0** - published as a STABLE release (Soren directly ordered a real release over a prerelease recommendation). /releases/latest should be v1.1.0, isPrerelease=false, ~16 assets, signed.
- Test state at ship: Gateway.Tests ~2123-2133 green, Core.Tests 3010 pass / 8 env-skips / 0 fail. Re-run to confirm on the current main.

## Current fleet state (measured at ship - RE-VERIFY, it may have moved)
- **Gateway 7878 = v1.1.0 tunnel-only** (it auto-updated itself when the stable release published; was PID 26616, version 1.1.0+880255b5). This is the single fleet Gateway, hosted on SOREN_NORTH.
- **A cut Director (id 37cea6e2, SOREN_NORTH, v1.1.0, source=stream)** is tunnel-connected to the Gateway and served a live tunnel command (repos-list 200). This is the working tunnel-only Director - likely the machine you are running on.
- **Old Director 7881 (id 5edf0787) = cc-director2.exe, v1.0.7, a LOCAL DEV BUILD with the auto-updater OFF** - it did NOT and will NOT auto-update (that is why it stayed v1.0.7 for ~38h while v1.0.9 was latest). It is roster-only behind the cut Gateway (pre-cut, so session verbs fail for it) but ALIVE, still hosting the prior mission drivers (Architect b8231814 + Manager b549044e) + ~9 other-mission sessions. Soren's ONE hard rule this whole ship was: do NOT kill 7881. Its sessions still need to be migrated/drained onto the tunnel-only stack, then it can be retired.
- SORENLAPTOP + the Mac: installed-from-release Directors auto-update to v1.1.0 on their poll cadence; Soren deprioritized them during the ship.

## PRIORITY ISSUE TO INVESTIGATE - mobile app "blinking" (sessions appear then disappear)
Soren reports the mobile app roster is flickering - sessions keep coming on and disappearing. This is the top thing to root-cause. Strong hypotheses, in order:
1. **Mixed fleet roster churn.** The cut Gateway builds its roster from pushed snapshots (PushedSessions / stream source). The pre-cut 7881 (source=file, roster-only) and the cut Directors (source=stream) may be racing - e.g. 7881's sessions flap in/out as different push/reconcile paths disagree, or a session shows under two Directors. Check whether the blink correlates with the pre-cut 7881 being present; test whether draining+retiring 7881 stops it.
2. **Roster push/reconnect timing.** #1488 changed registration to tunnel-Hello (RegisterFromStream). If Directors reconnect/re-Hello periodically and the Gateway rebuilds/expires roster entries, sessions blink. Look at the PushedSessions staleness/expiry window vs the push cadence, and any roster-rebuild-on-reconnect.
3. **Duplicate Director identity on SOREN_NORTH.** Two Directors on one machine (7881 pre-cut + the cut 37cea6e2) may collide on machine identity / session numbering, causing entries to alternate.
Reproduce on the phone, watch the Gateway roster (GET /healthz counts + the directors/sessions listing) live while it blinks, correlate with Gateway logs (DirectorHub Hello/disconnect, GatewayStreamRegistry, PushedSessions). This is the #1480 "always-current Gateway picture" territory - related design notes in memory [[gateway-live-picture-1480]].

## Verification checklist (confirm each yourself)
1. **Gateway is tunnel-only v1.1.0**: GET http://127.0.0.1:7878/healthz -> version 1.1.0. /m/ -> 200.
2. **THE SECURITY WIN - inbound port closed on a cut Director**: `netstat -ano | findstr :<cutDirPort>` shows LISTENING only on 127.0.0.1 (NOT 0.0.0.0 / [::]); `tailscale serve status` shows NO mapping for the Director port (only the 7878 front door). Contrast a pre-cut Director (7881) which still listens on its control port.
3. **THE PHONE WIN - reconnecting bug gone**: from the phone, on Tailscale (away) AND home LAN, open a session that lives on a CUT Director. Confirm NO "reconnecting to this session's computer" banner, and turns/prompt/voice/file all work over the tunnel.
4. **Whole surface over the tunnel**: turns, buffer, prompt (round-trip), resize, terminal stream, file view, screenshots, upload-image, voice, the restored repos/overview + repos-management verbs. All should work with zero HTTP-dial fallback (there is none - the client is deleted).
5. **Director loopback floor intact**: healthz, shutdown, reconnect, claude-hook, fleet-preamble(+hook-output), the Phase-4-deferred loopback config surface + the restored /fleet/send|ask|sessions|spawn (so `cc-devthrottle` still works locally).
6. **Release integrity**: gh api repos/thefrederiksen/devthrottle/releases/latest -> v1.1.0, isPrerelease=false, assets present + signed.
7. **Tests green on current origin/main**: Gateway.Tests + Core.Tests.

## Known caveats / follow-ups (not blockers, but log/verify)
- **Stale wwwroot assets**: the live Gateway swap used a Copy-Item MERGE (a Remove-Item hook blocked a clean replace), so old hashed assets may linger beside the cut ones. Harmless (hashed = content-addressed; the cut index.html governs what loads) but a clean wwwroot is owed.
- **File byte-Range reads** are whole-file-only over the up-stream (minor video-seek/large-PDF degradation) - tracked follow-up **#1481**.
- **Disk**: D: hit 100% full during the ship (caused a first gateway-swap attempt to fail + likely the earlier Gateway roster degradation); ~13GB was freed. Confirm D: has healthy headroom and stays clear.
- **Migrate + retire 7881**: move the prior mission sessions off the pre-cut dev-build 7881 onto the tunnel-only stack, then retire it (this also likely resolves the mobile blink if hypothesis 1 holds). Do NOT force-kill it while it hosts sessions.
- **Old mission worktrees to clean up**: `D:\ReposFred\devthrottle-cutprep` and `D:\ReposFred\devthrottle-gwcleanup-phase2`; plus `D:\cc-proof-root` + `local_builds\_gwbuild-cut` / `_dir-v1.1.0-cut` / `_rollback-v1.0.9` residue.

## Where the full state lives
- Mission memory: `C:\Users\soren\.claude\projects\D--ReposFred-devthrottle\memory\gateway-cleanup-mission.md` (seat-by-seat history, every ruling, every PR).
- Design docs: `docs/architecture/gateway-cleanup-phase2-repoint-design.md`, `gateway-cleanup-phase1-deletion-checkpoint.md`, `gateway-cleanup-phase1-manager-plan.md`, `gateway-cleanup-mission-2026-07-11.md`.
- The cutover runbook + evidence tables were in the prior Manager session's scratchpad (session df57f396: `cutover-plan.md`, `cutover-runbook-FINAL.md`, `sb4-evidence/EVIDENCE-TABLE.md`).
- The prior mission drivers still reachable on the old 7881 loopback (Architect b8231814, Manager b549044e) via `POST http://127.0.0.1:7881/fleet/send` if you need their context before they are retired.

## Rollback (if something is badly wrong)
- Gateway: a reserved v1.0.9 exe is staged (`...\cc-director\gateway\devthrottle-gateway-v1.0.9.exe`) - swap back + relaunch via the gateway-launch scheduled task.
- Directors: `.old` backups + a SHA-verified v1.0.9 asset in `local_builds\_rollback-v1.0.9`. To stop the fleet auto-updating, re-mark v1.1.0 as a prerelease (but note a v1.0.7 machine would then pull v1.0.9 = still a restart).
- Rollback is only coherent if the Gateway AND the Directors revert together (the incompatibility is bidirectional).
