# Manager brief - post-v1.3.0 follow-ups

Architect: session `2eef41a3` ("Stable Release - Architect").

**v1.3.0 is CUT AND PUBLISHED.** So is v1.2.0. Do not touch the tags, do not re-cut a release.
Main is at 1.3.0 and open for the next cycle.

## Standing authority

- **The Architect holds the owner's authority to COMMIT and to MERGE TO MAIN.** He granted it in his own
  words: *"I have given you full authority to go to main, origin main, commit"* and *"don't wait for me,
  don't ask for approval."* Drive each item all the way to a merged pull request. Merged is the only done.
- Do not ask for approval. Do not stop. Report to the Architect as each item lands.
- One pull request per item. Never batch. Squash merge, delete the branch.

## The rules that cost us today - read these, they are not decoration

1. **A call site is not a caller, and a retired purpose is not a dead file.** Ask "who STARTS this?", not
   "does this exist?".
2. **Verify before you claim** - `git show origin/main:<path>` / `git grep <pattern> origin/main`. Never a
   commit message, never a memory, never this brief.
3. **This brief is not evidence.** Six briefs I wrote today were corrected by Managers who checked the code
   instead of trusting me, and every one of them was right. **Items 2 and 3 below are UNVERIFIED CLAIMS made
   by a previous Manager. Verify each one before you fix it. If a bug is not real, say so and close it - do
   not manufacture a fix for it.**
4. **Proof is a running build, not a green test.** Sixteen green tests agreed with each other and were all
   wrong today because they tested a fake politer than the real transport.
5. **No silent degradation, ever.** A change that looks successful and quietly breaks something is the worst
   thing we can ship. Deleting `streamMode` would have silently killed the Launcher; a stray
   `CC_DIRECTOR_ROOT` at user scope would have silently emptied the owner's fleet. Both were near-misses.
6. **Seven** test projects, not two. Core plus Gateway alone is a false green. Last full run: 5524 passed.
7. **Plain English, ASCII only.** No abbreviations, no jargon, no emoji.
8. **Work in a worktree cut from origin/main.** Never `git checkout -b` in the shared checkout - it is on
   another session's live `feat/prompt-log`. Do not touch it.

## The items, in priority order

### 1. Issue #1548 - spawning into a Gateway mission fails with "unknown mission" (REAL - verified by the Architect)

Missions live in TWO stores that do not sync. The **Gateway** store is the source of truth
(`cc-devthrottle mission list` reads it). The **Director-local** `MissionStore`
(`%LOCALAPPDATA%\cc-director\config\director\missions.json`) is a documented TEMPORARY bridge.

`SessionCommandExecutor` resolves a mission three ways:
- `MissionId` AND `MissionName` both set -> Gateway path, stamps directly, no local lookup. **The documented
  end state, and the only one that works.**
- `MissionId` only -> transitional bridge -> looks up the Director-local store -> fails
  `unknown mission '<id>'. Create it first with POST /missions.`
- No `MissionId` -> no attach.

So `cc-devthrottle session spawn <repo> --mission <id>` can **never** work for a Gateway mission: the CLI
sends the id with no name, hits the bridge, and is rejected against the wrong store. **The error is a lie -
it tells you to create a mission that already exists.** I hit this myself and had to POST `fleet/spawn`
directly with both fields to get any Manager started today.

Fix at the root. The Gateway knows the name; make the spawn path carry it, or have the Director resolve
against the Gateway. Do not paper over it in the CLI.

### 2. UNVERIFIED CLAIM - a crashed session is indistinguishable from an exited one

Claimed as a regression from #1537 by the Working-is-Blue Manager. What I checked myself, and no further:
`ActivityState` has **no** `Crashed` member (Starting, Idle, Working, WaitingForInput, WaitingForPerm,
Exited), while `SessionViewModel` still has an `ErrorStatusBrush` and Crashed handling. Issue #959
introduced a deep-red "Crashed" that is deliberately darker than the "needs you" red.

Verify whether crashed is genuinely unreachable now, and where it went. If real, fix it under the standing
law (below). If not real, close it and tell me.

### 3. UNVERIFIED CLAIM - the prompt queue can never auto-drain

Claimed to be gated on `ActivityState.Idle`, which nothing ever writes. Partial corroboration: the only
reference to `ActivityState.Idle` I found in Core is a **comparison** at `Session.cs:1954`, not an
assignment - so nothing appears to assign it. `PromptQueue.cs` contains no `Idle` reference at all, so the
gate is elsewhere if it exists. Find the real gate before touching anything.

### 4. `TunnelFailure`'s default branch drops Director messages into a bare 502

Item 1's endpoint gap, generalised. The router now produces a plain-English error naming what happened, and
roughly 20 endpoint legs still collapse any non-Ok result into a bodyless 502, so the human never sees it.
The Tier 1 Manager scoped its status-and-words mapping to its own two routes only, deliberately, leaving the
general case for this item.

Do it the way item 1 did: **keep every existing status byte-identical** and carry the body only for the
Gateway-synthesized outcomes. Do NOT route them through `MapDirectorFailure` - that would turn today's 502
into a 400/404 and change a shipped contract. **Check who READS the message before assuming the fix lands.**

### 5. Cleanup the Architect deferred - now unblocked

- **`GatewayClient.cs` dead code.** Tier 2 deleted `SelectActiveUrlThenRegisterAsync`, the `HeartbeatTick`,
  `MaybeReRegisterOnIdentityChange`, plus the never-assigned `_heartbeat` and `_reRegistering` fields - but
  **I dropped that file's changes when resolving a rebase conflict** against the verify-handshake deletion,
  to avoid delicate surgery at speed during the release. Everything else in that cleanup shipped. Re-apply
  it against current main. My deferral, my fault, and it changes nothing for any user.
- **`DirectorVerification.cs`** (`DirectorVerifyRequest` + `DirectorVerifyResultDto`) and
  **`DirectorDto.TwoWayVerifiedAt`**. These were HELD by Tier 2 because the verify handshake had not merged
  yet. **It has now merged (#1555).** Re-check callers on current main - they should be orphaned now.
  `TwoWayVerifiedAt` is declared but written nowhere and serialises null forever to every caller, which is
  its own small lie on the wire.
- **Watch for same-name traps:** there are two different `HeartbeatTick`s and two different
  `_serveProvisioner`s. One of each is LIVE. Prove by callers.

### 6. About a dozen comment blocks that contradict themselves two lines apart

`GatewayEndpoints.cs:1043` says "falls back to the HTTP dial below" while `:1045` says "Post-cut:
tunnel-only... collapses to 502" - and `DirectorEndpointClient` is deleted, so there IS no dial below.
Bounded and mechanical: delete the false pre-cut sentence, keep the post-cut truth.

**Do NOT do a blanket sweep.** The real count of comments mentioning an HTTP fallback is **44**, not 7, and
they need per-line judgement. **`MachineSessionSpawner` is NOT stale** - it says "tunnel-first,
byte-identical HTTP fallback", which is TRUE, because the launcher leg genuinely still has that fallback.
Deleting a correct comment and replacing it with a false one would make the documentation worse. Fix only
the self-contradicting blocks.

## Explicitly NOT in scope

- **`streamMode`** - the Launcher still gates on it (`LauncherStreamClient.IsEnabled`) and enrollment writes
  it. Removing it silently kills the Launcher's persistent join. It is a design decision, not a cleanup.
- The LAN addressing option being a user-visible control that now does nothing - the owner's call.
- Anything in Tier 3: mobile resilience (#1181, #1187, #1139, #1326, #1325), the Cockpit regression (#1296),
  flaky tests (#1541, #1221), the Gateway dialling the launcher over HTTP.
- `docs/cencon/proof/issue-509/ask-sequence.log` - a tracked file a test rewrites with fresh GUIDs each run.
  Revert it if it dirties your tree; do not fix it here.

## The fleet

| Director | Port | Use |
|---|---|---|
| slot 1 | 7879 | The owner's build of latest main. **Do not touch.** |
| slot 2 | 7884 | Permanent. The Architect runs here. Do not disturb. |
| slot 6 | 7883 | **NOT throwaway** - hosts REAL user sessions. Restarting it KILLS them. |
| your own | pick 8+ | Your own proof Director, its OWN scheduled task, own root via `CC_DIRECTOR_ROOT`. |

**Set `CC_DIRECTOR_ROOT` for your PROCESS only - never at user or machine scope.** Setting it at user scope
would silently redirect the owner's next Director into your scratchpad and he would find an empty fleet with
no error. This nearly happened today. Verify it is unset when you finish.

Never kill a `cc-director*.exe` you did not launch. Shut your own down with
`POST http://127.0.0.1:<port>/shutdown` - a force-kill leaves a phantom session. Never drive the GUI: a
screenshot attempt today grabbed the owner's live desktop mid-dictation.

## Definition of done

Per item: root cause fixed or the claim disproved and closed, seven suites green, proven on a running build
where it has a runtime surface, merged to main, branch deleted. Report each as it lands.
