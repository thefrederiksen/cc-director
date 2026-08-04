# Phase 5 report: delete the Director's listener

Manager: session 967c051d. Branch `mission/remove-network-port`, worktree `D:\ReposFred\devthrottle-noport`.
Phase commits: `e5132c9af` (the deletion), `4a2e6665e` (everything outside the product repointed),
`742a49ee3` (the live-proof rig). Baseline for the comparative gate: `f09d55ff4` - see the gate
section for why it is not the branch tip the phase started from.

DRAFT - the gate and live-proof sections are being filled as the runs complete.

## What was deleted

The Director binds nothing. Removed outright:

- The Kestrel web host and every route it served: the fleet relay surface (`/fleet/*`), the
  automation-browser routes, `/healthz`, `/reconnect`, the settings/agents/tools/workspaces local
  config surface, the prompt-delivery-failures read, and the served HTML views (`Web\` embedded
  assets, including the session view and the dictate page).
- The port allocator: the 7879-7898 range, the per-Director reservation files, the ghost-reservation
  pruning, and the Windows excluded-range reader (`netsh` parsing). Named instances no longer carry
  an assigned port at all (`NamedInstance.Port` is gone; old registry JSON deserializes cleanly).
- The startup self-probe. The Architect's ruling held: the probe existed to prove nothing was
  shadowing the bound port, and with no bound port its question no longer exists. It went with the
  route it called.
- The Director-side authentication: the route guard (`ControlApiGuard`), the middleware
  (`DirectorAuth`), the derived scoped tokens (`DirectorScopedToken`, `ScopeNames`), and the
  one-rotation grace window for session-child tokens. All of it authenticated callers to a surface
  that no longer exists.
- The control endpoint in the instance registration. The file itself stays - phase 4's lifecycle
  machinery certifies a registration's author by its process id and start time, and that is what
  makes the file usable without a socket - but `ControlEndpoint` is now written empty (empty, not
  absent, so old readers deserialize).
- The advertised inbound endpoint in the Gateway registration. `GatewayClient` no longer resolves a
  Tailscale or LAN identity to advertise; the registration is identity only (id, pid, machine,
  user, version, start time). The Gateway's register route no longer demands an endpoint or an
  unreachable-reason - that guard protected against undialable entries in the era when the Gateway
  dialled Directors back, and nothing dials.
- The fleet-relay methods on `GatewayClient` whose only callers were the deleted routes (prompt,
  ask, fanout, rename, interrupt, hold, compact, buffer, role, mission attach, request-deletion,
  spawn-on-machine, machine query/launch/list, directors list, mission and workflow-run lookups,
  the run-participant record, and the optional-reachability fleet read). What survives is what the
  desktop and the host actually call: the fleet map reads, the push channels, snooze, session
  numbering, registration and heartbeat.

Kept deliberately:

- Every session-state service the host owns (badge tracking, preamble maintainer, pointer watcher,
  recorders, loggers, git status, activity ledger) - none of them ever touched the port, and the
  listener-era lesson that they start FIRST, before anything that can fail, is kept.
- The tunnel (`GatewayStreamClient`) and the outbound HTTP `GatewayClient` - the one door.
- `TailscaleServeSelfProvisioner`, for one job: on startup each Director reads its own leftover
  port reservation file if one exists, tears down any Tailscale Serve mapping still published for
  that port, then deletes the file - the file was the last record of which port it ever bound, so
  the read has to happen before the delete. This is the upgrade path self-healing; on a machine
  that never ran the listener era it does nothing.

## The two blockers the Architect named

**Blocker 1 - `CC_DIRECTOR_API` (and with it `CC_DIRECTOR_TOKEN`) is no longer stamped.**
`SessionManager` stamps `CC_SESSION_ID`, `CC_DIRECTOR_ID` (identity, not an address), and the
Gateway pair `CC_GATEWAY_URL` + `CC_GATEWAY_SESSION_KEY`. The token went with the address because
its verifying door died with the listener; the minting seam (`SessionCredentialSource`) went with
it. Removing the variable was also the probe for hidden dependents, and it FOUND them - none of
them a caller of the API, all of them prose or guards keyed to the variable:

1. **The fleet preamble told every agent a lie-to-be.** The shipped injected text said "You reach
   the fleet through your own Director (CC_DIRECTOR_API); no Gateway address or token is needed."
   Every agent in every session reads that. It now says the truth: the fleet is reached through
   the Gateway with the session's own key, already in the environment. (`FleetPreambleTemplate`,
   its approved-text test, and both copies of the dev-throttle and fleet-comms skill bodies.)
2. **The schedule scope guard (issue #2201) was keyed to the variable and to the reservation
   files.** Its trigger (`CC_DIRECTOR_API`), its evidence (the `.port` files) and its hazard (the
   schedule half reading the account token from a different root than the session half) each died
   separately - the hazard ended when phase 2b moved the schedule client onto the session's own
   environment pair, so both halves of the tool now read one environment and the mismatch the
   guard refused can no longer be expressed. Guard and tests deleted with the reasoning recorded
   in place.
3. **The hook-contract guards already forbade the tokens** (`HookScriptContractTests` fails if any
   hook script mentions `CC_DIRECTOR_API` or `CC_DIRECTOR_TOKEN`) - phase 3's deletion proof now
   doubles as this phase's regression guard on the hook side.

**Blocker 2 - the desktop fleet-tool health check is REBUILT against the Gateway, not removed.**
The question it answers is unchanged - "can a session I spawn actually reach the fleet?" - and the
answer is now measured on exactly the path a session uses. The host mints a probe session key
through the same mint-and-register path a session launch uses (`MintFleetToolProbeCredentialAsync`),
AWAITS the registration (a session launch deliberately does not; the probe must, because it
presents the key immediately), runs the cc-devthrottle that PATH resolves with precisely the
environment a session gets, reads only the exit code, and revokes the key afterwards. The verdict
vocabulary tells the two states apart that the brief demanded: `CannotReachGateway` (the fault -
usually a stale install's tool on PATH; the repair banner and PATH repoint flow survive unchanged)
against `NoGateway` (no Gateway connection right now - the mission's accepted no-Gateway trade,
never painted as a broken install, never offered a repair). The green-to-red flip the brief warned
about cannot happen: nothing in the check dials the Director at all.

Two neighbouring surfaces were the same defect in other clothes and were rebuilt with it:

- **The connectivity troubleshooter** diagnosed the inbound model - is Tailscale up, is the Serve
  mapping present, does the local listener answer, does the advertised URL dial back. Those
  questions died at the tunnel-only cut and this phase removed their last object; the ladder now
  asks what can actually be wrong (Gateway configured -> Gateway answers from here -> tunnel
  connected) and states that a firewall can never again be the cause.
- **The sidebar's "CONTROL API DOWN" indicator** reported a bind failure. Nothing binds; the
  indicator, its notification, and the `IsListening`/`StartupError` machinery behind it are gone.
  Fleet reachability is the Gateway status box, which was already the truth.

## Everything else that still spoke to the port (the consumer map, and what happened to each)

Found by mapping before deleting; none of these were in the brief:

| Consumer | Disposition |
|---|---|
| Instance picker (`SelectDirectorDialog`) probed each instance's port for liveness | Resolves liveness from the registration the running process wrote, via `DirectorInstanceLocator` (moved from the Launcher to Core so both supervisors share the one certified answer: pid plus start-time window, ambiguity refused) |
| Handover copy block advertised "Control API: <endpoint>" | Identity only (session id, Director id, machine); the fleet reaches a session through the Gateway by ids |
| Help dialog resolved and displayed the advertised control endpoint | Row removed; the dialog names identity and the Gateway |
| "Open in browser" session-view handler in the desktop | Deleted - it was already dead (its route was removed at the Gateway Cleanup cut and no XAML referenced the handler); recorded as a pre-existing dead limb, not a phase regression |
| `agent-session-isolation.ps1` discovered the port from the "Kestrel listening" log line and passed it to teardown | Launch readiness is now the instance registration naming the PID (which also carries the identifier the named-signal teardown needs); the manifest records `directorId` instead of `port`; the launch mutex stays with an honest comment |
| `cc-settings-api` skill drove `GET/PUT /settings` | Retired in place, stating where settings editing lives now - including that a `config.json` file edit does not re-apply a running Director's Gateway connection, which the deleted route did |
| Fleet-served skills taught the loopback floor, `CC_DIRECTOR_TOKEN`, and a route-probing diagnostic | Rewritten for the one door, in both copies each |
| `docs/public/api/01-control-api.md` and the CLI reference's Director REST section | The API document is now a removal record mapping each old capability to its replacement; the stale REST section (which still documented a voice-turn route deleted missions ago) says the surface is gone |
| CLAUDE.md rule 0b told test sessions to find the port in the log and "drive it via REST" | Now: readiness is the registration file; drive a test Director through its Gateway; stop it with the named signal |
| The no-cross-machine-loopback guard's allowlist | SEVEN entries left it at once (host, guard, registration, self-test, session manager, picker, app bootstrap) - the guard's own stale-entry check forced the shrink, which is that guard doing exactly what it promises |

## Tests followed their subjects

The parked `Gateway.Tests` suite was the listener's home suite, so this phase is where it got
re-pointed. The rule applied throughout: a test whose SUBJECT died is deleted with the reason
recorded in place; behavior that lives on is re-pinned at the surviving seam - the shared verb
executors the tunnel dispatches into (`SessionCommandExecutor.Create`, `SessionWriteExecutor`,
`CatalogReadExecutor`), the pushed-roster mapper, and the outbound `GatewayClient` methods that
remain.

Deleted with their subjects (each file's reason is in the commit; the notable ones):
hostile-access and auth-reapply suites (attack and rotate a surface that no longer exists), the
per-endpoint suites (settings, agents, tools, tool-run), `SessionHookRoutesAreGoneTests` (phase 3's
three-routes-absent proof is subsumed by there being no router; phase 7 adds the no-listener
guard), `FleetSpawnNamedDirectorTests` (the Director-floor routing decision is a dead concept - the
Gateway routes by machine), the port allocator suites, and `DirectorScopedToken`'s suite.

Re-pinned at the surviving seam (behavior preserved, no socket anywhere):

- Spawn origin and lineage, mission attach, and workflow seats now drive the real `create` verb
  core. Two of these had route-only halves that are GATEWAY responsibilities on the live path and
  already covered there: the mission-name resolution (`MachineEndpoints` stamps the name from its
  own store; the Director's create-time contract - Gateway-resolved ids stamp directly, the id-only
  bridge resolves locally and refuses loudly - is pinned here) and the auto-seat resolution plus
  participant record (`MachineSpawnWorkflowScopeTests` covers the Gateway half; the Director's
  stamp-and-brief half, including the maintained preamble file carrying the seat paragraph, is
  pinned here).
- The ghost-session reap (#1019) proves naming through the PUSHED roster (the one the CLI resolves
  against) and reaps through the real `request-deletion` verb and the real reaper.
- The defect-10 idle-wait promptness lives where the live path lives:
  `TunnelIdleWaitFanoutRestoreProofTests` (Gateway waitForIdle over the tunnel) already carried it;
  the deleted Director-route version tested a relay nothing calls.
- The error-words guarantee (a human reads WHY, not "HTTP 502") is re-pinned through the surviving
  `RelayFailureAsync` caller (`RecordHoldAsync`); the Gateway leg is untouched.
- The registration-request tests now pin the new shape: identity only, no endpoint, no reason -
  so a reintroduced advertisement reddens a test.
- The reachability suite tests the Gateway-pair probe, including that the probe THROWS rather than
  ever running credential-less, and that the no-Gateway verdict never routes to a tool repair.
- The host suite is now about what the host does: registration written with `Pid`/`StartedAt` and
  an EMPTY `ControlEndpoint`, deleted on stop; identity handoff; the state services running with
  nothing else started, and their idempotence.

One coverage loss stated plainly rather than papered over: the `GET /workspaces` and
`GET /history` list routes died with their tests and have no tunnel verb. The mission's phase 2
already recorded `cc-history` as dead in production (a pre-existing defect filed separately and
excluded from that phase's pass mark); this phase did not widen or narrow that hole, and it remains
open.

## An Architect error in the shared worktree, and what it did to the baseline

Stated in the Architect's own words and at the Architect's request: this was an ARCHITECT error,
not a collision or an accident of scheduling. While this phase was mid-flight with a rename staged,
the Architect edited MISSION.md in this worktree and committed with `git add <his file>` plus a
bare `git commit` - believing narrow staging scoped the commit. It does not: the index is shared
state in a shared worktree, and commit takes the WHOLE index. His mission-state commit `a641109fe`
therefore swept in this phase's staged-but-half-done rename of `DirectorInstanceLocator.cs` (R100,
Launcher to Core, content unedited - so the moved file still declared `namespace CcDirector.Launcher`
and imported `CcDirector.Setup.Engine`, which Core cannot reference). **Every commit from
`a641109fe` through `9b68b91be` - four commits - does not compile on Windows.** The failure was
silent on both sides: his commit reported success, the rename vanished from this phase's working
state into his commit, and it surfaced only when a fresh worktree at the old tip failed to build.
The first phase-5 commit happens to complete the move, so the branch tip builds green and nothing
needed rewriting; the fix that actually scopes a commit in a shared tree is `git commit -- <paths>`
(or not writing in another agent's tree at all), and the Architect has put that in the mission
record and fleet memory.

**Consequence for the gate: the comparative baseline is `f09d55ff4`**, the last commit that
builds - NOT `9b68b91be`, the commit this phase's work sits on. A comparative run against a
non-building parent would be meaningless, and worse, a later reader could conclude the mission
broke the build. It did not: the break entered with `a641109fe` and left with `e5132c9af`.

## The gate, comparatively

Every `obj` and `bin` under `src\` and `tools\` was deleted before the phase arm's first run, per
the mission's standing stale-assembly rule.

**Phase arm** (commit `e5132c9af` onward, in this worktree):

- Default gate, run 1: FAILED in 2 projects.
  - `GatewayInputStatsAggregatorTests.TokenSpend_RepeatedIdenticalSnapshot_DoesNotDoubleCount` -
    `InvalidOperationException: The collection has been marked as complete with regards to
    additions`. That is verbatim the mission's named fleet-wide defect: the `FileLog` teardown
    race, whose victim is arbitrary and whose cause is not. Phase 4 recorded eight of nine
    failures sharing this exception.
  - `ReleaseSourceTests.FetchLatestAsync_403ThenSuccess_RecoversAndDelaysBetweenAttempts` -
    "expected a non-zero backoff before the retry", a wall-clock timing assertion in the setup
    engine. Run in isolation THREE times on each arm afterwards: 9/9 passed every time, on both.
    A timing assertion that only fails under a loaded parallel run is the same class of noise.
- Parked `CcDirector.Core.Tests`: 4200 passed, 1 failed, 8 skipped, in 32m53s. The one failure was
  OURS and is fixed: see the print-ban audit above. Re-run of that test: green.
- Parked `CcDirector.Gateway.Tests`: (result pending; filled from the run.)

**Parent arm** (commit `f09d55ff4`, a separate detached worktree, clean by construction), run
more than once as the mission's rule requires - a single-run control on this repository's gate is
itself a coin toss:

- Run 1: FAILED in 1 project -
  `WorkListStoreCaseParityTests.StoreNameCollision_ExactlyMatchesOrdinalIgnoreCase_ForEveryPair`,
  with the SAME exception as the phase arm's first failure ("The collection has been marked as
  complete with regards to additions") in a DIFFERENT test. Different victim, one cause - which is
  exactly the shape the mission documented.
- Run 2: fully green, every project, zero failures.

**Judgement.** Both default-gate failures on the phase arm are absent from the parent's green run
and one of them appears on the parent arm as a different victim of the same named exception, so by
the mission's comparative criterion neither is this phase's. The failure that WAS this phase's came
from the parked suite the default run never touches - which is the coverage gap the gate itself
warns about, and the reason the parked suites were run rather than waved through.

## The live proof

(PENDING - produced by `scripts/phase5-noport-proof.ps1`; evidence files quoted here when run.)

## Not proven, stated plainly

- The first-launch wizard on a CLEAN Windows machine with no security popup - the proof the
  mission plan assigns to this phase - needs a clean virtual machine and is not produced by this
  worktree's rig. The mechanism that raised the popup (the allocator probing candidate ports on
  every interface) is deleted, but deletion-in-source is exactly the kind of proof the
  requirements document refuses, so this stays OPEN for the mission's QA report and should be run
  on the clean-machine rig.
- macOS and Linux remain unproven for this phase's changes, as for the mission generally (recorded
  at phase 3; unchanged).
- A single-target `message send` into a real agent terminal remains outside the rig (phase 2's
  fixture limitation, unchanged): the checklist session is a batch script with no composer to echo.
