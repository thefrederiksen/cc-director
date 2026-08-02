# Dev Throttle

DevThrottle is a desktop application positioned as "Mission Control for Claude Code" - one place to run, observe, and orchestrate multiple Claude Code sessions side by side. It also installs a suite of `cc-*` command-line tools onto your PATH.

This skill orients a Claude Code session to what is available after DevThrottle is installed. It is written for the installed product, not for building from source.

Naming note: DevThrottle is the product/brand. The application binary installed and launched on a machine is `cc-director.exe`, and the bundled command-line tools keep their `cc-*` names. So you will see the product called DevThrottle while the concrete on-disk app, process, and tools still carry `cc-` names.

## What DevThrottle is, in three parts

1. **Desktop app (`cc-director.exe`)** - Windows (primary), with experimental Mac/Linux support. Runs and supervises multiple Claude Code sessions, one per repo, with real-time activity tracking, terminal buffers, and voice input.
2. **The Gateway** - one machine on the fleet runs a Gateway process that every machine's Director connects OUT to over a persistent two-way stream (internally, "the tunnel"). The Gateway is the single front door for the web Cockpit and the phone app, and the coordination point the whole fleet talks through. Individual Directors are never dialled over the network - they dial out to the Gateway, and everything travels down that stream.
3. **`cc-*` tool suite** - CLI tools installed on PATH when DevThrottle is set up. Each tool supports `--help` for its full command syntax. The fleet command is `cc-devthrottle`.

## The cc-* tools (installed on PATH)

All tools are on PATH after install. For exact flags and examples, run any tool with `--help`.

### Documents
`cc-pdf`, `cc-html`, `cc-word`, `cc-excel`, `cc-powerpoint` - convert markdown to PDF / HTML / Word / Excel / PowerPoint with themed templates (boardroom, paper, terminal, blueprint, thesis, spark, obsidian).

### Email
`cc-gmail`, `cc-outlook` - read, send, and search Gmail and Outlook from the CLI.

### Web
`cc-crawl4ai` (clean markdown extraction for RAG), `cc-websiteaudit`, `cc-brandingrecommendations`.

### Desktop automation
`cc-click` (Windows UI: click, type, screenshot, OCR), `cc-trisight` (3-tier UI element detection: UIA + OCR + pixel), `cc-computer` (AI desktop agent with screenshot-in-the-loop).

### Media
`cc-image`, `cc-voice` (text-to-speech), `cc-whisper` (audio transcription / translation), `cc-video`, `cc-transcribe`, `cc-photos`, `cc-youtube-info`.

### Data and utilities
`cc-vault` (contacts, tasks, goals, docs, RAG), `cc-hardware`, `cc-docgen` (C4 architecture diagrams from YAML).

### DevThrottle fleet, schedules, and setup
`cc-devthrottle` is the unified command for fleet/session operations, inter-session messages, settings, Gateway schedules, and setup:

```
cc-devthrottle actions --json
cc-devthrottle session list
cc-devthrottle message send <target|all> "message"
cc-devthrottle settings get screenshots.source_directory
cc-devthrottle schedule list
cc-devthrottle setup status
```

A handful of tools are registered but not yet built (`cc-twitter`, `cc-facebook`, `cc-youtube`, `cc-posthog`). If a tool isn't on PATH, it likely isn't built yet.

## How an agent talks to the fleet: use `cc-devthrottle`, not raw HTTP

The way to list, message, create, rename, and close sessions from inside a session is the
`cc-devthrottle` command. It already knows how to reach the fleet: every session is launched with
`CC_DIRECTOR_API` (its own Director's loopback address) and `CC_SESSION_ID` (its own id) in the
environment, and `cc-devthrottle` reads them automatically. You never need a Gateway URL or token.

Do NOT try to drive sessions by curling the Director's HTTP port. The Director exposes only a small
LOOPBACK-ONLY floor (see below) - the old "Control API" that let a caller list/prompt/create/delete
sessions over HTTP was removed when the fleet moved onto the tunnel. Session control now travels the
tunnel to the owning Director; the agent-facing surface for it is `cc-devthrottle`.

For the full fleet-messaging and session-spawning command reference, see the **fleet-comms** skill.

### The Director's loopback floor

The Director's own HTTP surface is bound to `127.0.0.1` only (never the LAN or Tailscale) and is now
a minimal floor - not a general control API. It exists for local health checks, the launcher, and the
agent-lifecycle hooks, not for driving sessions:

| Method | Endpoint | Purpose |
|---|---|---|
| GET | `/healthz` | Liveness. The only route that answers WITHOUT a credential, and its unauthenticated answer is `{"status":"ok"}` and nothing else. Present a credential and it also carries the version, director id, and session/director counts |
| GET | `/fleet/sessions` | Roster the local Director sees (what `cc-devthrottle session list` reads) |
| POST | `/fleet/send`, `/fleet/ask` | Fleet messaging relay (used by `cc-devthrottle message`) |
| POST | `/fleet/spawn` | Spawn on another machine, relayed via the Gateway |
| POST | `/reconnect` | Bounce this Director's outbound tunnel |
| POST | `/shutdown` | Ask this Director to stop |
| GET/PUT | `/settings` (+ agents/tools/workspaces) | Local config surface (desktop app + cc-settings-api) |
| GET/POST | `/sessions/{sid}/fleet-preamble`, `/claude-hook` | Agent-lifecycle IPC (the SessionStart hook calls these) |

"Is the app running?" is the one raw call worth knowing:

```
curl http://127.0.0.1:7879/healthz
```

**Every other route requires a credential.** The Director's loopback floor is authenticated: loopback
proves the caller is on this machine, which does not distinguish the desktop from the command line
from an agent's child process from a browser that followed a rebound name to 127.0.0.1. A call with
no credential is answered 401, and one with a `Host` header that is not this Director's own loopback
address is answered 403 before it reaches a handler.

You do not normally have to think about this: `cc-devthrottle` derives its own credential from the
machine secret, and an agent inside a session is given one at launch as `CC_DIRECTOR_TOKEN`, bound to
that session. That injected credential reads its OWN session and the roster and nothing else - it
cannot spawn, shut down, prompt another session, read another session's terminal, or change settings.
Use `cc-devthrottle` for anything beyond that; it is what everything else an agent needs goes
through.

## Creating a session correctly (always name it)

Create sessions with `cc-devthrottle session spawn`, not a raw HTTP call. Whichever way you create a
session, get these right every time - an underspecified session is unnamed, blocked on permission
prompts, or on the wrong model.

1. **Always give it a meaningful name.** A session is how a human finds work in Mission Control. On a
   fleet where many sessions run in the SAME repo, an unnamed session falls back to the bare repo
   folder name (e.g. "devthrottle") and is indistinguishable from every other session in that repo.
   Pass `--name` (an explicit display name) or `--purpose` (what the session is FOR, e.g.
   `implement #806`); the name must describe the work and must not be blank or equal to the bare repo
   folder name. `spawn` warns when you give neither.
2. **Carry the normal permission preset and model in `--args`.** The desktop New Session dialog uses
   the "Automatic (skip permissions)" preset. When you pass no `--args`, `spawn` applies the same
   default. When you DO pass `--args`, you override it entirely, so include the whole line, e.g.
   `--args "--dangerously-skip-permissions --model opus[1m]"` - otherwise the session can stall at a
   "Do you want to proceed?" prompt or run on the wrong model/window.
3. **Use `--prompt`** for the session's first task (dispatched once the agent is ready). For a long
   instruction, write it to a file and make `--prompt` a short "read and follow <path>" pointer.

```
# Create a properly-named, autonomous-ready session
cc-devthrottle session spawn D:\Repos\myrepo \
  --name "myrepo - fix auth bug #123" \
  --args "--dangerously-skip-permissions --model opus[1m]" \
  --prompt "Fix the bug in auth.js"
```

The Director names the session at birth and returns the final id and name. See the **fleet-comms**
skill for the full flag set (`--agent`, `--role`, `--mission`, `--machine`, `--controlled-by`, and the
display-name convention).

### Opening a session on ONE particular Director

`--machine` names a computer, and one computer runs several named Director instances - so it lands on
whichever the Gateway lists first. When you were told to use a specific Director, name it:

```
cc-devthrottle director list          # names, machines, and the Director id to use
cc-devthrottle session spawn D:\Repos\myrepo \
  --director 6f0a2b41-1c33-4f9e-9a10-2b7d5e8c1234 \
  --name "myrepo - fix auth bug #123"
```

`--director` takes the Director id or its display name and needs no `--machine` - a Director
identifies the computer it runs on. Prefer the id: it survives a rename and cannot collide with a
second Director sharing a name. A person handing you a Director will usually paste its toolbar Copy
output, which is three lines - `Director:`, `Director ID:`, `Machine:`. Take the id from there and
use it verbatim.

An unregistered name fails loudly, and a name matching two Directors fails listing both. Neither
falls back to another Director - if you get one of those errors, run `director list` and pick, rather
than dropping the flag and spawning wherever.

## Closing a session (including closing yourself)

A session can close itself. When an agent has finished its work and nothing is waiting on the
user, it should reap its own session rather than leaving an idle entry in Mission Control.

**Use `cc-devthrottle session done`.** It flags THIS session (via `CC_SESSION_ID`) for graceful
asynchronous removal: the owning Director's deletion reaper removes it on its next sweep, once a short
grace has passed and the session is no longer working. It does NOT kill the caller's process
mid-request, so you can flag yourself and then finish your turn normally.

```
# Close THIS session gracefully (run at the very end of your turn, on an unattended run)
cc-devthrottle session done
```

To reap a DIFFERENT session, pass its id or name: `cc-devthrottle session done <target>`.

Do not self-close while something still needs the user (a pending decision, an approval, an
unanswered question). Reap only when the queue is truly empty.

## Who the user is

Every session is started for one signed-in DevThrottle user. The session-start preamble names that
user - both their email and, when they set one, their chosen nickname. Treat that named person as the
user of this session: unless they explicitly say otherwise, "me", "my account", and "email me" mean
that user. Do NOT guess who the user is from usage patterns or by searching the database for a name -
use the identity the preamble gave you. If no user is named (nobody signed in), do not invent one.

## When this skill is the right thing to consult

- "What cc-* tools do I have for X?" - look in the tool list above; run the tool with `--help` for syntax.
- "How do I list / message / create / close sessions?" - use `cc-devthrottle` (details in the fleet-comms skill).
- "Is the app running?" - `curl http://127.0.0.1:7879/healthz`. For the VERSION, present a
  credential: the unauthenticated answer is liveness only.

## What this skill does NOT do

- It does not replace `<tool> --help`, which has the authoritative flags and examples for each tool.
- It does not replace the **fleet-comms** skill, which is the full reference for `cc-devthrottle`.


**Skill Version:** 5.2 (tunnel-only fleet)
**Last Updated:** 2026-07-14
**Changes in 5.0:** Rewritten for the tunnel-only fleet (release v1.1.0, the Gateway Cleanup cut). The Director no longer exposes a general HTTP Control API - it binds a small loopback-only floor, and the Gateway is the single front door the fleet connects out to over the tunnel. Removed the deleted session-driving REST endpoints (list/details/buffer/prompt/interrupt/turns/handover/chat/voice/create/request-deletion/delete) and the curl-the-Director examples. Session create / close / rename / message now documented through `cc-devthrottle` (spawn / done / rename / message), which is the agent-facing surface. Dropped retired tools `cc-browser`, `cc-reddit`, and `cc-comm-queue` from the tool list.

**Changes in 5.2:** Corrected 5.1's diagnostic, which was wrong. Rename, done, and "message send all" were restored to the Director's loopback floor (`POST /fleet/rename`, `/fleet/done`, `/fleet/broadcast`) after the v1.1.0 release (issue #1490, closed). A 404 from them means the Director predates that restoration. But do NOT use the `/healthz` version to decide: `Directory.Build.props` still reads `1.1.0` on main, so the shipped v1.1.0 Director WITHOUT the routes and a fresh main build WITH them both report `"version":"1.1.0"` - verified by running both side by side on 2026-07-14. Probe the route and compare it against a deliberately fake route instead; see the fleet-comms skill for the exact commands (issue #1514).
**Changes in 4.3:** Added "Who the user is" - the session-start preamble names the signed-in DevThrottle user (email + nickname); "me / my account / email me" means that user unless they say otherwise, and identity must not be guessed from usage or the database (issue #1357).
