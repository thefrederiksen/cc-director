---
name: dev-throttle
description: DevThrottle - "Mission Control for Claude Code". A desktop app (binary cc-director.exe) that runs and supervises multiple Claude Code sessions, ships cc-* CLI tools on PATH, and exposes a REST Control API (default port 7879). Triggers on "/dev-throttle", "/devthrottle", "/cc-director", "what cc tools", "list tools", "available tools", "control api", "devthrottle api", "session manager", "mission control".
---

# Dev Throttle

DevThrottle is a desktop application positioned as "Mission Control for Claude Code" - one place to run, observe, and orchestrate multiple Claude Code sessions side by side. It also installs a suite of `cc-*` command-line tools onto your PATH.

This skill orients a Claude Code session to what is available after DevThrottle is installed. It is written for the installed product, not for building from source.

Naming note: DevThrottle is the product/brand. The application binary installed and launched on a machine is `cc-director.exe`, and the bundled command-line tools keep their `cc-*` names. So you will see the product called DevThrottle while the concrete on-disk app, process, and tools still carry `cc-` names.

## What DevThrottle is, in three parts

1. **Desktop app** - Windows (primary), with experimental Mac/Linux support. Runs and supervises multiple Claude Code sessions, one per repo, with real-time activity tracking, terminal buffers, voice input, and a web Manager UI.
2. **Control API** - A REST/JSON API embedded in the desktop app, on port range 7879-7898 (default 7879). Lets external callers list sessions, send prompts, interrupt, fetch terminal buffers, perform programmatic handovers, and post voice commands.
3. **`cc-*` tool suite** - CLI tools installed on PATH when DevThrottle is set up. Each tool supports `--help` for its full command syntax.

## The cc-* tools (installed on PATH)

All tools are on PATH after install. For exact flags and examples, run any tool with `--help`.

### Documents
`cc-pdf`, `cc-html`, `cc-word`, `cc-excel`, `cc-powerpoint` - convert markdown to PDF / HTML / Word / Excel / PowerPoint with themed templates (boardroom, paper, terminal, blueprint, thesis, spark, obsidian).

### Email
`cc-gmail`, `cc-outlook` - read, send, and search Gmail and Outlook from the CLI.

### Web and social
`cc-browser` (cross-session browser automation with persistent connections), `cc-playwright` (Chromium browser + remote-debug + CDP; the default for form fills, sign-in, OTP, and React forms), `cc-reddit` (human-paced Reddit), `cc-crawl4ai` (clean markdown extraction for RAG), `cc-websiteaudit`, `cc-brandingrecommendations`.

### Desktop automation
`cc-click` (Windows UI: click, type, screenshot, OCR), `cc-trisight` (3-tier UI element detection: UIA + OCR + pixel), `cc-computer` (AI desktop agent with screenshot-in-the-loop).

### Media
`cc-image`, `cc-voice` (text-to-speech), `cc-whisper` (audio transcription / translation), `cc-video`, `cc-transcribe`, `cc-photos`, `cc-youtube-info`.

### Data and utilities
`cc-vault` (contacts, tasks, goals, docs, RAG), `cc-hardware`, `cc-comm-queue` (queue for outbound email / social with approval), `cc-docgen` (C4 architecture diagrams from YAML).

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

## Control API at a glance

REST/JSON on Kestrel. Default base URL: `http://localhost:7879` (the app picks a stable port in 7879-7898 if 7879 is taken). When accessed remotely, the API is fronted on the user's Tailscale tailnet rather than localhost.

Key endpoints:

| Method | Endpoint | Purpose |
|---|---|---|
| GET | `/healthz` | Health, version, director id, session count |
| GET | `/` | Session list (JSON), or HTML Manager UI for browsers |
| GET | `/sessions` | List sessions |
| GET | `/sessions/{sid}` | Session details |
| GET | `/sessions/{sid}/buffer` | Terminal output (by line range or since-timestamp) |
| POST | `/sessions/{sid}/prompt` | Send a prompt to a session |
| POST | `/sessions/{sid}/interrupt` | Send Ctrl+C |
| GET | `/sessions/{sid}/turns` | Turn history |
| POST | `/handover` | Programmatic session handover |
| POST | `/chat` | Manager chat (LLM-powered analysis of sessions) |
| POST | `/voice/command` | Voice command input |
| GET | `/repos` | Registered repositories |
| POST | `/sessions` | Create a new session |
| POST | `/sessions/{sid}/request-deletion` | Flag a session for graceful async removal (the safe self-close) |
| DELETE | `/sessions/{sid}/request-deletion` | Cancel a pending deletion during the grace window |
| DELETE | `/sessions/{sid}` | Kill and remove a session immediately (do NOT use on yourself) |

## Sample API calls

```
# Health check
curl http://localhost:7879/healthz

# List sessions as JSON
curl -H "Accept: application/json" http://localhost:7879/sessions

# Send a prompt to a known session
curl -X POST http://localhost:7879/sessions/<sid>/prompt \
  -H "Content-Type: application/json" \
  -d "{\"text\": \"Fix the bug in auth.js\"}"
```

## Creating a session correctly (always name it)

When you create a session through `POST /sessions`, do these four things every time. The Control API
applies NO defaults of its own - it uses exactly what you send - so an underspecified create produces
a session that is unnamed, blocked on permission prompts, or on the wrong model.

1. **Always give it a meaningful Name.** A session is how a human finds work in Mission Control. On a
   fleet where many sessions run in the SAME repo, an unnamed session falls back to the bare repo
   folder name (e.g. "devthrottle") and is indistinguishable from every other session in that repo.
   The `Name` MUST describe what the session is FOR (e.g. "mobile PWA - impl loop #806"), and it must
   not be blank or equal to the bare repo folder name. Never create a session without a name.
2. **Carry the normal permission schema and model in `Args`.** The app's normal launch uses the
   "Automatic (skip permissions)" preset; the Control API does not add it for you. Pass
   `Args: "--dangerously-skip-permissions --model opus[1m]"` so the session can act without stopping
   to ask permission for every read/edit/command, and runs on the model + 1M window you expect.
   Omitting the preset leaves the session stuck at a "Do you want to proceed?" prompt.
3. **Use `PrePrompt`** for the session's first task (dispatched once the agent is ready). For a long
   instruction, write it to a file and make `PrePrompt` a short "read and follow <path>" pointer.
4. **Verify the name persisted; PATCH if empty.** After creating, read the session back and confirm
   `name` is set. Some running builds do not honor the create-time `Name` - if it came back empty,
   set it explicitly: `PATCH /sessions/{sid}` with `{ "Name": "<meaningful name>" }`.

```
# Create a properly-named, autonomous-ready session (auth header only when the API requires a token)
curl -X POST http://localhost:7879/sessions \
  -H "Content-Type: application/json" \
  -d "{\"RepoPath\":\"D:/Repos/myrepo\",\"Agent\":\"ClaudeCode\",\"Type\":\"Developer\",\"Name\":\"myrepo - fix auth bug #123\",\"Args\":\"--dangerously-skip-permissions --model opus[1m]\",\"PrePrompt\":\"Fix the bug in auth.js\"}"

# Then verify the name, and PATCH it if the running build returned an empty name:
curl -X PATCH http://localhost:7879/sessions/<sid> \
  -H "Content-Type: application/json" -d "{\"Name\":\"myrepo - fix auth bug #123\"}"
```

## Closing a session (including closing yourself)

A session can close itself. When an agent has finished its work and nothing is waiting on the
user, it should reap its own session rather than leaving an idle entry in Mission Control.

**Use the graceful self-delete: `POST /sessions/{sid}/request-deletion`.** This flags the session
for asynchronous removal. The owning Director's deletion reaper removes it on its next sweep
(about every 30 seconds) once a short grace has passed and the session is no longer working. It
does NOT kill the caller's process mid-request, so the agent can flag itself and then finish its
turn normally. The request body is optional: `{ "reason": "..." }`.

Every session already knows how to reach its own Director without any Gateway address or token:
the environment variable `CC_DIRECTOR_API` holds the local Director's base URL (for example
`http://127.0.0.1:7879`), and `CC_SESSION_ID` holds the session's own id. So to close yourself:

```
# Close THIS session gracefully (run at the very end of your turn)
curl -X POST "$CC_DIRECTOR_API/sessions/$CC_SESSION_ID/request-deletion" \
  -H "Content-Type: application/json" \
  -d "{\"reason\": \"work complete, nothing waiting on the user\"}"
```

The same call works through the Gateway (`POST {gateway}/sessions/{sid}/request-deletion`), which
forwards it to the owning Director. That matters when the session you want to reap lives on another
machine; to close your OWN session, going straight to `CC_DIRECTOR_API` is simplest and needs no
Gateway token.

Two related endpoints:
- `DELETE /sessions/{sid}/request-deletion` cancels a pending deletion during the grace window (you
  changed your mind before the reaper ran).
- `DELETE /sessions/{sid}` kills and removes a session immediately. Do NOT call this on your own
  session - it tears down your process mid-request. It is for force-removing OTHER sessions.

Do not self-close while something still needs the user (a pending decision, an approval, an
unanswered question). Reap only when the queue is truly empty.

## When this skill is the right thing to consult

- "What cc-* tools do I have for X?" - look in the tool list above; run the tool with `--help` for syntax.
- "How do I call DevThrottle from a script?" - point at the Control API section.
- "Is the app running / what version?" - `curl http://localhost:7879/healthz`.

## What this skill does NOT do

- It does not replace `<tool> --help`, which has the authoritative flags and examples for each tool.
- It does not document every API endpoint; the table above covers the common ones.

---

**Skill Version:** 4.2 (end-user, DevThrottle rebrand)
**Last Updated:** 2026-07-11
**Changes in 4.2:** Added "Closing a session (including closing yourself)" - a session can reap itself with `POST /sessions/{sid}/request-deletion` against its own Director (`CC_DIRECTOR_API` + `CC_SESSION_ID`), the graceful self-delete that does not kill the process mid-turn. Documented the Gateway forward of the same call, the cancel-during-grace endpoint, and that immediate `DELETE /sessions/{sid}` must not be used on yourself. Added these three endpoints to the Control API table.
**Changes in 4.1:** Added "Creating a session correctly (always name it)" - a created session MUST carry a meaningful Name, the normal permission preset + model in Args (--dangerously-skip-permissions --model opus[1m]), a PrePrompt for its first task, and a verify/PATCH of the name afterwards (the Control API applies no defaults; some running builds do not honor create-time Name).
