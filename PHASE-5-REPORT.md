# Phase 5 report: delete the Director's listener

Manager: session 967c051d. Branch `mission/remove-network-port`, worktree `D:\ReposFred\devthrottle-noport`.
Phase commits: `e5132c9af` (the deletion), `4a2e6665e` (everything outside the product repointed),
`742a49ee3` (the live-proof rig), `59d66db01` (the print-ban audit), `9bdb31fe9` (the refused-probe
fix), `fe14cc136` (the live proof and its evidence). Baseline for the comparative gate:
`f09d55ff4` - see the gate section for why it is not the branch tip the phase started from.

## The claim, and what backs it

**The Director listens on nothing.** Two Directors, proven alive and registered at the moment of a
connection scan that resolves owning processes, owned ZERO listening sockets while holding 14
established outbound connections to their Gateway - with the owner's own pre-mission Directors
found listening on 7879 and 7881 by the identical query, in the same instant, as the positive
control. Seventeen of seventeen `cc-*` commands passed from inside a real session holding a real
session key, and that session's own environment dump shows `CC_DIRECTOR_API` and
`CC_DIRECTOR_TOKEN` absent. Evidence is committed under `docs/qa/phase5-noport/`; the source
absence of a bind is deliberately NOT offered as proof, per the mission's requirements.

What is NOT proven is stated in the last section, and the first-launch popup on a clean machine is
the one the mission asked for that this worktree cannot produce.

## What was deleted

The Director binds nothing. Removed outright:

- The Kestrel web host and every route it served: the fleet relay surface (`/fleet/*`), the
  automation-browser routes, `/healthz`, `/reconnect`, the settings/agents/tools/workspaces local
  config surface, the prompt-delivery-failures read, and the served HTML views (`Web\` embedded
  assets, including the session view and the dictate page).
- The port allocator: the 7879-7898 range, the per-Director reservation files, the ghost-reservation
  pruning, and the Windows excluded-range reader (`netsh` parsing). Named instances no longer carry
  an assigned port at all (`NamedInstance.Port` is gone).

  **The upgrade path was RUN, not reasoned about.** A throwaway program compiled against this
  branch's Core deserialized (a) a pre-mission `named-instances.json` in which every instance
  carries a `port`, and (b) a pre-mission Director registration whose `ControlEndpoint` is a real
  loopback address. Both parsed cleanly - two instances with names, display names and gateway URLs
  intact; the registration with its id, pid and old endpoint readable - so a machine upgrading onto
  this build does not lose its instance registry or trip over its own leftover files. The dropped
  `port` is simply not carried forward the next time the registry is written.
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

**One defect found while building the probe, and fixed before it could ship.** The first version
returned "no verdict available" for three different situations - no Gateway configured, no tunnel,
and *the Gateway refusing to register the probe key* - and the caller rendered all three as the
benign `NoGateway` state. The third is not benign: it is a connected Gateway rejecting us, dressed
up as an expected trade the owner has already accepted, which is exactly the plausible-but-wrong
shape this mission keeps catching. It is not hypothetical either - registration is keyed by session
id and the hub does NOT check that the id belongs to a live session of the calling Director (the
mission's own inspection filed that as finding 2), so this probe's synthetic id is precisely what
stops being accepted if that hole is closed. A refusal now THROWS, and the desktop turns it into no
verdict plus a loud log, so the day the hub is hardened this check says so instead of going quiet.

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

## FINDING: a test that had been passing without running for 22 days

`WingmanAskForwardingTests.Ask_no_claude_returns_no_claude_status_with_context_digest` was green
every day and asserted nothing. Its body needed a real session, obtained through the Director's
`POST /sessions` route; when creation failed it took an early `return` and the test passed having
verified nothing at all. That route was deleted by the tunnel-only cut, commit `398c4e4ae` on
**2026-07-13** (confirmed by reading the file on both sides of that commit: one `MapPost("/sessions"`
before, zero after). From that day until this phase re-pointed session creation at the real create
verb - **2026-08-04, 22 days** - creation could only ever fail, so the early return was the ONLY
path the test ever took.

It took a port deletion to expose it, and only because re-pointing creation made it SUCCEED for the
first time; the test then failed loudly, because the Gateway's wingman-ask route is tunnel-only and
that fixture has no tunnel. **The suite cannot presently distinguish a test that passes from a test
that never runs** - a green tick meant the same thing in both cases for three weeks.

Two things follow, and only one of them is a repair:

1. The test is re-pointed at the `wingman-ask` verb core - the seam the tunnel actually dispatches
   into - so it exercises the claim rather than the fixture's luck.
2. A conditional early `return` inside a test body is the mechanism, and it is not unique to this
   file. Anything of the shape "if the arrangement failed, return" converts a broken arrangement
   into a pass. `Assert.Fail`, `Assert.Skip`, or a fixture that throws would all have surfaced this
   on day one. Worth a sweep beyond this mission.

**A second, smaller finding fell out of fixing it, and is REPORTED rather than fixed here.** With
the test finally running, the free-text ask path (`WingmanService.AnswerViaSessionAsync`) is seen to
omit `ContextDigest` on its `no_claude` branch while setting it on its success branch - and its
sibling `AskAboutSessionAsync` sets one on BOTH. The two wingman paths disagree about whether a
no-claude answer carries its context digest. That predates this mission and has nothing to do with a
network port, so changing it inside a deletion diff would be smuggling; the test now pins the
contract that actually ships and says why.

## FINDING: on this machine, a rig gets the INSTALLED tools unless it fights for its own

Building the live proof produced two separate near-misses, both of which would have yielded a fully
green run proving the wrong thing, and both of which look correct from the outside. This is a
standing hazard for anyone building a rig in this repository, not a slip - the previous Manager hit
the same trap and warned about it, which is what makes it a property of the setup.

1. **The obvious shim runs the installed package, not the branch.** `tools/cc-devthrottle/main.py`
   enters through `from cc_devthrottle.cli import app`, and that name resolves to the pre-mission
   copy in the pyenv's `site-packages`. Running `python main.py` would have exercised the OLD
   command line - the one that still requires `CC_DIRECTOR_API` - against the new Director. The
   branch package is `src`, so the shim must set `PYTHONPATH` and enter through it.
2. **Even a correct shim loses to the Director's own bin.** `SessionManager` deliberately puts its
   instance's `bin` FIRST on every session's PATH so a stale copy elsewhere can never win - and the
   Director had populated that directory with the installed toolset. The first honest checklist run
   resolved `<root>\instances\default\bin\cc-devthrottle.cmd` and every fleet command failed with
   `CC_DIRECTOR_API is not set`. The rig now installs the branch shim where the Director actually
   looks, which is also what an upgraded machine looks like.

That second run is worth keeping as a result rather than only as an obstacle: **an old cc-devthrottle
against a new Director fails loudly, naming the missing variable**, instead of hanging, silently
degrading, or appearing to work. It also states an upgrade-ordering fact plainly - the command line
must move with the Director, and a machine that updates one without the other gets a clear error
rather than a mystery.

The checklist itself contributed a third: the original batch version wrote its environment dump and
then silently executed none of its commands, reporting nothing while looking complete. It was
replaced with PowerShell that records each command's exit code. A proof harness that can fail
silently is not a proof harness.

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
- Parked `CcDirector.Gateway.Tests`: 2204 passed, 2 failed, 47 skipped, in 38m18s. BOTH failures
  were OURS, and the second is a finding in its own right:
  - `GatewayDirectoryRegistrationTests.Register_rejects_missing_tailnet_endpoint` pinned the exact
    guard this phase deleted. Rewritten to assert the new contract - an endpointless registration
    is ACCEPTED, because it is now what every Director sends - and it checks the registration does
    not acquire an endpoint on the way in.
  - `WingmanAskForwardingTests.Ask_no_claude_returns_no_claude_status_with_context_digest`: see the
    vacuous-test finding below.
  Both re-run green (14/14 across the two classes).

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

Produced by `scripts/phase5-noport-proof.ps1` on SOREN_NORTH, 2026-08-04. Evidence committed at
`docs/qa/phase5-noport/connection-scan.txt` and `docs/qa/phase5-noport/checklist-results.txt`.
Nothing of the owner's was read, reconfigured or stopped: the rig ran its own self-hosted Gateway on
port 7997 under its own storage root, two Directors in slots 6 and 7 (the owner's slots 1-5 and the
installed application untouched), each launched from its own scheduled task through a wrapper that
sets the environment process-locally, and `--no-autostart` so the throwaway Gateway never wrote the
user's Run key.

### 1. The connection scan, with owning processes, on a machine running more than one Director

Both rig Directors were PROVEN RUNNING at the instant of the scan - process ids taken from the
registrations they had written, each verified alive, each registered with the Gateway:

```
slot 6: pid=20508 exe=...\local_builds\cc-director6.exe directorId=97f74e40... registeredControlEndpoint=''
slot 7: pid=69368 exe=...\local_builds\cc-director7.exe directorId=deb95cba... registeredControlEndpoint=''
```

The result:

```
TCP LISTEN sockets owned by the rig Directors: 0
UDP endpoints owned by the rig Directors: 0
VERDICT: PASS: the rig Directors, alive and registered, own ZERO listening sockets
```

The same file lists what those two processes DID own: **14 ESTABLISHED** connections, every one of
them outbound to `127.0.0.1:7997` - the Gateway - plus 14 rows in Windows' `Bound` state.

**The `Bound` rows deserve a sentence, because they are the one thing in this file a reader could
misread.** They appear as `local=0.0.0.0:63783 remote=0.0.0.0:0`, and `0.0.0.0` looks like
listening-on-every-interface. It is not: these are the local halves of the same outbound sockets,
which Windows reports as `Bound` rather than `Listen`. The scan asserts on `State -eq 'Listen'`
precisely so the distinction is made by the operating system rather than by reading an address, and
the Listen count is zero. (An earlier draft of this report said 24 established, having counted from
truncated console output rather than the file; corrected here from the artifact.)

That is the design in one line: the Director talks, and is never talked to.

**The positive control is in the same scan, in the same instant**, which is what makes the zero a
measurement rather than a blind spot:

```
pid=34032 exe=...\cc-director\app\cc-director.exe    listeners=1   LISTEN 127.0.0.1:7879
pid=15700 exe=D:\ReposFred\dt-slot2\...\cc-director2.exe listeners=1   LISTEN 127.0.0.1:7881
```

Those are the owner's own pre-mission Directors, found listening by the identical query that found
zero for ours. The launcher is reported too - `pid=81280 ... LISTEN 127.0.0.1:7900` - and labelled
in the file as PHASE 6's listener, deliberately still present and not evidence about phase 5 either
way. The requirements name both programs, so omitting the launcher would have invited a reader to
take this file as proof of something it never measured.

### 2. Every cc-* command, from inside a real session holding a real session key

The session was created THROUGH the Gateway (`POST /directors/{id}/sessions`) and ran the checklist
with this branch's command line resolved first on its PATH. Its own environment, dumped by the
session itself:

```
CC_GATEWAY_URL             present=True  correct
CC_GATEWAY_SESSION_KEY     present=True  correct
CC_DIRECTOR_ID             present=True  correct
CC_SESSION_ID              present=True  correct
CC_DIRECTOR_API            present=False correct
CC_DIRECTOR_TOKEN          present=False correct
```

**That is blocker 1 proven live**, not argued from source: no session is handed an address for a
door that does not exist.

**17 of 17 commands PASS**: `session list`, `session whoami`, `actions --json`, `repo list`,
`worktree list`, `machine list`, `director list`, `skill list`, `workflow list`, `schedule list`,
`mission list`, `browser list`, `session rename`, `session hold`, `session hold --release`,
`session role`, `message send all`.

One earlier FAIL was the RIG's, not the product's, and is recorded rather than quietly corrected:
the checklist invented `session release`, which is not a command (`session hold --release` is), and
the tool answered `Usage:` with exit 2 - the command line being right and the harness being wrong.

### 3. The teardown is itself a result

Both Directors took the named shutdown signal and **deleted their registrations**, which is what a
clean stop looks like as distinct from a kill (a force-killed Director leaves its registration
behind - that is how the earlier reaped run was identified). The Gateway stopped, the scheduled
tasks were unregistered, and the owner's two Directors were still running afterwards, untouched.

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
  fixture limitation, unchanged): the checklist session is a script with no composer to echo.
  `message send all` exercises the same framing and fanout and passed.
- **The desktop's rebuilt fleet-tool check was not exercised against a live Gateway.** Its verdict
  logic, its probe-credential path and its refusal-vs-no-Gateway distinction are covered by unit
  tests and by construction, but nobody watched the indicator paint on a running desktop in this
  phase. The check runs in the Avalonia application, which the rig does not drive.
- **The launcher still listens** (`127.0.0.1:7900`, recorded in the scan). That is phase 6's work
  and is called out here so this phase's evidence is never read as covering it.
- The parked `Gateway.Tests` suite's full green re-run after the two fixes is recorded in the gate
  section; the two fixed classes were re-run directly (14/14).
