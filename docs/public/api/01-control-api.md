# Control API

Every running Director hosts a small REST Control API on loopback (`127.0.0.1`, port range 7879-7898, one stable port per Director).

This surface is a **floor**, not a full remote-control API. It is deliberately narrow: health, the fleet verbs, local settings, the tool catalog, and the workspace list. It is reachable only from the machine the Director runs on.

The Director does **not** expose routes for creating, reading, renaming or killing sessions directly, nor for terminal buffers, git, handovers, repositories, screenshots, dictation, text-to-speech or a terminal WebSocket stream. Those either live on the Gateway or no longer exist. If you are looking for the session surface, you want the Gateway, and the supported way to drive it is the `cc-devthrottle` command line tool.

## Authorization

**Every route except `/healthz` requires a credential**, presented as `Authorization: Bearer <token>`. Being on loopback is not the boundary: it proves the caller is on this machine, and says nothing about whether it is the desktop, the command line, an agent's child process, or a browser that followed a rebound name to `127.0.0.1`.

Three things are checked, in this order, before any handler runs:

1. **The `Host` header** must be exactly this Director's own loopback address and bound port (`127.0.0.1:<port>` or `localhost:<port>`). Anything else is `403`. This is what refuses a rebound DNS name whose traffic genuinely arrives on loopback.
2. **A mutating request** (`POST`, `PUT`, `PATCH`, `DELETE`) that a browser initiated is accepted only when it reports `Sec-Fetch-Site: same-origin` and an `Origin` of this Director's own loopback origin; otherwise `403`. Clients that are not browsers send neither header. No `Access-Control-*` response header is involved - the decision is made here, not delegated to the caller's browser.
3. **The credential**, which is either this machine's secret or a token derived from it.

`401` means no valid credential was presented and a retry with one may work. `403` means the request was understood and refused, and no retry will help.

There is one machine secret, at `config/director/gateway-token.txt`, or the shared fleet token from `config.json` when this machine is attached to a Gateway. Scoped tokens are derived from it, so nothing extra is stored:

| Scope | Held by | May do |
|-------|---------|--------|
| the machine secret itself | the launcher, `cc-settings-api` | everything |
| `admin` / `cli` | the desktop's own callers, the `cc-devthrottle` command line | everything |
| `session-child` | an agent running inside a session, bound to that session's identifier | read its own session and the safe discovery set only |

A `session-child` credential is refused - regardless of `Host` and `Origin` - on shutting the Director down, spawning, prompting or messaging another session, reading another session's terminal, changing settings, running tools, and driving browsers. Presented for a session identifier other than the one it is bound to, it is refused there too.

`/healthz` is the only route reachable without a credential, and its unauthenticated answer is liveness alone (`{"status":"ok"}`). Present a credential and it also carries the Director identifier, version, machine name, live session count and server time - the launcher's update check and the Director's own startup self-probe read those.

## A session can find itself

The Director injects these environment variables into every session it spawns:

| Variable | Meaning |
|----------|---------|
| `CC_SESSION_ID` | The Director's session identifier for this session |
| `CC_DIRECTOR_API` | Base URL of the owning Director's Control API, e.g. `http://127.0.0.1:7880` |
| `CC_DIRECTOR_ID` | The owning Director's stable identifier |
| `CC_DIRECTOR_TOKEN` | This session's own credential (the `session-child` scope above), bound to `CC_SESSION_ID` |

A session reaches the rest of the fleet through its own Director, which forwards to the Gateway when one is configured. A session is never handed the Gateway address or the fleet token; `CC_DIRECTOR_TOKEN` is derived from that secret and reveals nothing about it.

The supported way for an agent to identify itself and reach the fleet is the command line tool, not a raw call:

```
cc-devthrottle session whoami
cc-devthrottle session list
```

## The routes

All bodies are JSON unless noted.

### Health and lifecycle

| Method | Route | Purpose |
|--------|-------|---------|
| GET | /healthz | Liveness. The only route that needs no credential; unauthenticated it answers `{"status":"ok"}` and nothing more. With a credential it also carries the Director identifier, version, machine name, live session count and server time |
| POST | /shutdown | Ask this Director to shut down gracefully |
| POST | /reconnect | Force this Director to re-establish its outbound tunnel without a full restart |

### Session launch hooks

These serve the agent hook scripts the Director installs. They are not a session management surface.

| Method | Route | Purpose |
|--------|-------|---------|
| GET | /sessions/{sid}/fleet-preamble | The launch-time fleet awareness text for a session: its own identity plus the commands that reach the fleet. Plain text, so a hook can drop it straight into a context field. |
| GET | /sessions/{sid}/fleet-preamble-hook-output | The same preamble, already wrapped in the hook output envelope, for shell hooks that cannot safely build JSON. Empty body when there is no preamble. |
| POST | /sessions/{sid}/claude-hook | Receives a Claude Code hook event. Accepts both the mapped body and Claude's raw event. Records what it is given and returns 200. |

### The fleet verbs

Each of these acts on a session by identifier. When the target is local, the Director handles it; otherwise it forwards to the Gateway.

| Method | Route | Purpose |
|--------|-------|---------|
| GET | /fleet/sessions | List the sessions in the fleet |
| GET | /fleet/buffer?sessionId= | Read a session's terminal buffer |
| POST | /fleet/spawn | Open a new session |
| POST | /fleet/rename | Rename a session |
| POST | /fleet/prompt | Send a prompt into a session |
| POST | /fleet/send | Send a message to a session |
| POST | /fleet/ask | Ask a session a question and wait for its answer |
| POST | /fleet/interrupt | Interrupt a session |
| POST | /fleet/hold | Place a hold on a session |
| POST | /fleet/role | Set or clear a session's explicit role. A blank value clears; a non-blank value must be one of the four known roles. |
| POST | /fleet/done | Flag a session for reaping once it is finished |
| POST | /fleet/broadcast | Send a message to the sender's own team. Requires `fromSessionId` so the team can be resolved. Fleet-wide broadcast is refused by the Gateway without a human grant. |

### Settings

| Method | Route | Purpose |
|--------|-------|---------|
| GET | /settings | The raw configuration |
| PUT | /settings | Write the configuration |
| POST | /settings/detect/gateway | Detect the Gateway address (`?apply=true` to save it) |
| POST | /settings/detect/public-url | Detect the public URL (`?apply=true` to save it) |
| POST | /settings/detect/screenshots | Detect the screenshots location (`?apply=true` to save it) |
| POST | /settings/test/gateway | Test a Gateway address |

### Agents

The catalog of coding agent command lines this Director can launch.

| Method | Route | Purpose |
|--------|-------|---------|
| GET | /settings/agents | List the configured agents |
| POST | /settings/agents | Add an agent |
| GET | /settings/agents/{id} | One agent |
| PATCH | /settings/agents/{id} | Update an agent |
| DELETE | /settings/agents/{id} | Remove an agent |
| POST | /settings/agents/{id}/enabled | Enable or disable an agent |
| POST | /settings/agents/reorder | Reorder the agent list |
| POST | /settings/agents/detect | Detect installed agents |
| POST | /settings/agents/quick-check | Check whether an agent runs |
| POST | /settings/agents/command-line | Preview the command line an agent would launch with |
| GET | /settings/agents/catalog | The catalog of known agents |

### Tools, workspaces

| Method | Route | Purpose |
|--------|-------|---------|
| GET | /tools | The tool catalog |
| GET | /tools/{name} | One tool |
| POST | /tools/{name}/test | Test one tool |
| POST | /tools/test | Test every tool |
| POST | /tools/run | Run a catalog tool with arguments and stream its output |
| GET | /workspaces | The workspace list |
| GET | /workspaces/{slug} | One workspace |
| GET | /history | Session history |

## Finding a Director's port

From inside a session, use `CC_DIRECTOR_API` - no discovery needed. From outside, each Director writes `instances/{directorId}.json` under `%LOCALAPPDATA%\cc-director\config\director\` with its current port, and registers with the Gateway when one is configured.
