# Control API - removed

The Director no longer has a Control API. The remove-the-network-port mission deleted the
Director's HTTP listener entirely: there is no loopback port, no port range 7879-7898, no
`/healthz`, no fleet verbs, no local settings routes, and no credential scheme for any of them,
because there is nothing to present a credential to.

**Everything a caller used to do here goes through the Gateway.** The supported way to drive the
fleet is the `cc-devthrottle` command line, which presents the calling session's own key to the
Gateway; the Gateway rules on it and reaches the owning Director over that Director's outbound
tunnel. No Gateway connection means no agent tooling - that is the designed trade.

What replaced each piece:

| The Control API used to provide | Where it lives now |
|---|---|
| Session listing, messaging, spawning, renaming, closing | The Gateway's agent surface, via `cc-devthrottle` with the session's own key |
| The SessionStart hook's preamble and the transcript pointer report | Files the Director maintains per session (`CC_SESSION_PREAMBLE_FILE`, `CC_SESSION_POINTER_FILE`) |
| Health and liveness | The instance registration file the running process writes (its `Pid` names the process), and the Gateway's fleet view |
| Shutdown and update checks | Named signals (`Local\cc-director-shutdown-<directorId>`, `Local\cc-director-check-updates-<directorId>`) |
| Local settings, agents, tools, workspaces routes | The desktop application in process; remote configuration rides the Gateway's tunnel verbs |

A session's environment carries `CC_SESSION_ID` (its own identifier), `CC_DIRECTOR_ID` (which
Director it belongs to - identity, not an address), and the Gateway pair `CC_GATEWAY_URL` +
`CC_GATEWAY_SESSION_KEY`. The old `CC_DIRECTOR_API` and `CC_DIRECTOR_TOKEN` variables no longer
exist.

For the Gateway's own API, see the Gateway documentation in this directory.
