# Manager brief - Tier 1 items 2, 3, 4 (Stable Release, v1.3.0)

Mission id: `ac6883bb-09e2-4b5a-96bf-df3eae8d9f63`
Architect: session `2eef41a3` ("Stable Release - Architect").

Read the mission brief for the WHY: `docs/architecture/stable-release-mission-2026-07-14.html`

**Stay in scope. Do not invent work. Do not re-open a Tier 3 decision. Do not go near session state,
colours, or the rail - that is another lane and it is not yours.** Item 1 is already merged
(`bcb94dbb`); do not touch `DirectorCommandRouter.cs`.

## Your three items, in this order

### 2. Close the open inbound port

`src/CcDirector.ControlApi/ControlApiHost.cs:272` - `builder.WebHost.ConfigureKestrel(o =>
o.Listen(IPAddress.Any, Port));`. LAN addressing mode binds the Director's Control API to every interface,
directly contradicting the tunnel-only cut's stated invariant that the inbound port stays closed. Nothing
dials it any more, so it is pure attack surface with no user. Verified by the Architect on origin/main.

Care required: `AddressingMode` has other readers. Read them before you cut. The floor must keep working on
loopback - that is what every local caller and the CLI use.

### 3. Cockpit Director Settings is broken

`/directors/{id}/settings` returns the single-page-app HTML shell instead of data. Verified: the Cockpit
calls it (`apps/cockpit/src/fleet/DirectorDetailView.tsx`, which even documents `GET/PUT
/directors/{id}/settings`), but **no such route is mapped on the Gateway** - so it falls through to the
SPA fallback and answers with HTML. Same disease as rename: a caller left pointing at something the cut
moved.

### 4. "Verify now" can never succeed

`GatewayConnectionPanel.axaml.cs:284` -> `ControlApiHost.VerifyGatewayNowAsync` ->
`GatewayClient.VerifyAsync` (`src/CcDirector.ControlApi/GatewayClient.cs:931`), which posts to
`directors/{id}/verify`. That route was **deliberately deleted** - see the comment at
`src/CcDirector.Gateway/Api/GatewayEndpoints.cs:505`. Worse than dead: on the 404 the client tells the user
*"The Gateway ... does not support the verify handshake - update the Gateway"*, which is a lie that sends
the owner off to do a day of work that would fix nothing. A button that cannot work is worse than no button.

## Carried forward from item 1 - this WILL bite you

- **Check who READS the message before assuming a fix lands.** Item 1's router computed a perfect error and
  ~20 endpoints threw it away as a bare status with no body. A fix the human never sees is not a fix.
- **The transport's exception type is not a contract.** Anything touching the tunnel branches on the TOKEN,
  never on the exception type - SignalR does not throw `OperationCanceledException` on a cancelled client
  result. Sixteen green tests missed this because the hand-written fake was politer than the real transport.
- There are still 7 stale "fall back to the HTTP dial" comments in `GatewayEndpoints` that the cut made
  false. Nearly free. Fold them in if you are already in that file; do not make a project of it.

## The rules that are not negotiable

1. **Verify before you claim.** `git show origin/main:<path>` / `git grep <pattern> origin/main`. Never a
   commit message, never a memory, never a tree that may be behind.
2. **Work in a worktree cut from origin/main.** Never `git checkout -b` in the shared checkout - it is
   currently on another session's live `feat/prompt-log` work. Do not touch it.
3. **Probe the route, never the version.** `/healthz` cannot tell a fixed build from a broken one. Probe
   the real route against a deliberately fake one. **Never probe `POST /fleet/spawn`** - it returns 201 and
   creates a real session.
4. **Proof is a running build, not a green test.** Item 1 proved this the hard way. Demonstrate each item on
   a running slot Director.
5. **No fallbacks.** Fix the root cause or fail loudly with a clear message.
6. **Plain English, ASCII only.** No abbreviations, no jargon, no emoji.
7. **Do not commit without the OWNER asking.** Not the Architect - the owner. Permission never carries
   forward. Ask me and I will go get it. Do not merge unless the owner asks.
8. Report to the Architect. Ping at every milestone. Fleet messages are ONE line. Do not message the owner.

## Testing

**Seven** test projects, not two. Core plus Gateway alone is a false green - the other Manager's full run was
5510 passed. One suite green proves nothing.

## The fleet - read this carefully

| Director | Port | Use |
|---|---|---|
| slot 6 | 7883 | **NOT throwaway.** It hosts REAL user sessions (AgentEyes, mindzieWeb). Rebuilding or restarting it KILLS them. Leave it alone. |
| slot 2 | 7884 | Permanent. The Architect runs here. Do not disturb. |
| installed app | 7879 | v1.1.0, the broken build. Never test against it. |
| your own slot | pick 8+ | Stand up your own proof Director with its OWN scheduled task (`cc-director-launch-slot<n>`). |

A shared scheduled task kills whichever Director it launched last - this already killed a Director today.
Never kill a `cc-director*.exe` you did not launch. Shut your own down gracefully with
`POST http://127.0.0.1:<port>/shutdown` - a force-kill leaves a phantom session.

Build a slot: `powershell -ExecutionPolicy Bypass -File scripts\local-build-avalonia.ps1 -Slot <n>`

## Definition of done

Per item: the defect is fixed at the root, demonstrated on a running Director, all seven suites green, and
the work sits ready in your worktree. **You do not commit and you do not merge until the owner asks** - tell
me when an item is ready and I will get his word. Report each item as it lands; do not batch all three.
