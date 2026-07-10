# Issue 1214 - what the desktop "Handover info" shows today

## Where it lives in the desktop app

The desktop Avalonia app exposes handover info as the session context-menu item
"Copy Handover Info" (see `src/CcDirector.Avalonia/MainWindow.axaml.cs`, the menu
item built near line 2199 and the text builder `CopySessionNameAndId` near line
2375). Its stated purpose (from the method's own summary comment) is:

> "everything another agent needs to locate the session and talk to it."

It copies a plain-text block to the clipboard. It is NOT the rich transcript
"summary" (files touched / recent commands / open todos) served by
`GET /sessions/{sid}/summary` - it is the small identity/locate block.

## The exact lines the desktop copies

The block is built from these fields (in order):

- `Name`         - the session display name (`SessionViewModel.DisplayName`)
- `Session ID`   - the stable session id (`Session.Id`)
- `Repo`         - the repository / working directory (`RepoPath`)
- `Director ID`  - the id of the Director hosting the session
- `Machine`      - `Environment.MachineName` of the host
- `Version`      - the Director assembly version
- `Control API`  - the Director's reachable Control API endpoint (a Tailscale
                   Serve front door when on a tailnet, otherwise the loopback
                   endpoint)

## What the browser (Cockpit) handover view gets

The Cockpit view mirrors the desktop block with ONE deliberate exclusion: the
`Control API` endpoint. Issue 1214 requires that "the browser talks only to the
Gateway and never learns a Director address." The Control API endpoint IS a
Director address, so the Gateway-exposed handover info omits it. Everything else
(name, session id, repo, director id, machine, version) is carried through
unchanged.

The data path added for issue 1214:

- Director Control API: `GET /sessions/{sid}/handover` -> `HandoverInfoDto`
  (name, session id, repo, director id, machine, version). No Director address.
- Gateway proxy: `GET /sessions/{sid}/handover` -> resolves the owning Director
  from the session id, forwards to that Director, stamps the Director id, returns
  the same `HandoverInfoDto`. Requires the same Bearer/device-key auth as every
  other session route (401 without a valid credential). 404 when the session is
  unknown to every Director; 502 when the owning Director is unreachable.
- client-core: `getHandover(sessionId, signal?)` -> `SessionHandover`.
