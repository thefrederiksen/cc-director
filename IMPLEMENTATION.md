# Claude Code Director v1.3 - Implementation Document

## 1. Executive Summary

Claude Code Director is a distributed system that manages and orchestrates multiple `claude` CLI instances across local Windows machines via a centralized React web interface. A local .NET 10 service maintains active sessions in memory using ConPTY for high-fidelity terminal interaction, while Supabase handles both real-time relay and long-term storage.

**Key architectural principle**: The local agent pushes all data outbound. No inbound ports, no NAT traversal, no tunneling required.

---

## 2. claude.exe Reference

| Property | Value |
|----------|-------|
| Path | `C:\Users\soren\.local\bin\claude.exe` (in PATH as `claude`) |
| Type | Native Windows PE32+ binary (221 MB) |
| Version | 2.1.37 |
| Concurrency | 4+ instances run simultaneously (normal) |
| VSCode flags | `--output-format stream-json --input-format stream-json --permission-prompt-tool stdio` |

Not a Node.js wrapper. Works directly with ConPTY.

### Hooks System

Claude Code supports lifecycle hooks (configured in `~/.claude/settings.json` or `.claude/settings.json`):

| Hook | Fires When |
|------|------------|
| `Stop` | Claude finishes a full response |
| `Notification` | Claude needs input (permission prompt, confirmation, etc.) |

Hooks execute shell commands with access to event context.

### Named Pipe IPC

Windows named pipe `\\.\pipe\CC_ClaudeDirector` provides structured communication:
- Claude Code hooks write PID-tagged JSON events to the pipe
- Director reads events (response_end, input_needed, etc.)
- Fire-and-forget: silent failure if Director not running
- Bidirectional: Director can write input replies back through the pipe
- Zero-latency, no ports, pure Windows IPC

---

## 3. System Architecture

### 3.0 Dual-Channel Design (Control Plane + Data Plane)

The Director uses **two complementary channels** per claude session:

```
+------------------------------------------------------------------+
|                      CLAUDE SESSION                               |
|                                                                   |
|  DATA PLANE (ConPTY)              CONTROL PLANE (Named Pipe)     |
|  +---------------------------+    +---------------------------+   |
|  | Raw terminal bytes        |    | Structured JSON events    |   |
|  | ANSI colors, progress bars|    | { type, pid, message }    |   |
|  | Full visual fidelity      |    |                           |   |
|  |                           |    | Events:                   |   |
|  | Direction: bidirectional  |    |  - response_end           |   |
|  |   OUT: terminal output    |    |  - input_needed           |   |
|  |   IN:  raw keystrokes     |    |  - permission_prompt      |   |
|  +---------------------------+    |                           |   |
|                                   | Direction: bidirectional  |   |
|  Purpose:                         |   OUT: events from claude |   |
|  - xterm.js rendering             |   IN:  replies from Dir.  |   |
|  - Visual terminal experience     +---------------------------+   |
|                                                                   |
|                                   Purpose:                        |
|                                   - Session state awareness       |
|                                   - Auto-approve permissions      |
|                                   - Detect when claude is idle    |
|                                   - Queue next task automatically |
+------------------------------------------------------------------+
```

**Why two channels?**
- ConPTY gives raw bytes for visual rendering (xterm.js needs this)
- Named pipe gives structured events for orchestration logic (Director needs this)
- Neither alone is sufficient: ConPTY can't tell you "claude is waiting for input" without parsing ANSI; Named pipe can't render a terminal

### 3.1 Data Flow (Agent-Push Model)

```
+--------------------------------------+       +-------------------------------+
|  LOCAL MACHINE                       |       |  CLOUD (Vercel + Supabase)    |
|                                      |       |                               |
|  .NET 10 Agent (Director)            |       |  Vercel (React SPA + API)     |
|                                      |       |  +-----------------+          |
|  +----------+   +----------------+   | push  |  | /api/sessions   |          |
|  | ConPTY   |   | Named Pipe     |   +------>|  | /api/buffer     |          |
|  | (data)   |   | (control)      |   |       |  +---------+-------+          |
|  +----+-----+   +-------+--------+   |       |            |                  |
|       |                 |            |       |  +---------v--------+         |
|       v                 v            |       |  | Supabase         |         |
|  +----------+   +----------------+   | push  |  | - Realtime ch    |<--sub--+|
|  | Circular |   | Event Router   |   +------>|  | - Postgres       |        ||
|  | Buffer   |   | (state machine)|   |       |  | - Storage        |        ||
|  +----+-----+   +-------+--------+   |       |  +------------------+        ||
|       |                 |            |       |                              ||
|       +--------+--------+            | poll  |                              ||
|                v                     +-------+  React Dashboard ------------+|
|         +-----------+                |       |  (xterm.js + Supabase sub)    |
|         | Session   |                |       +-------------------------------+
|         | Manager   |                |
|         +-----------+                |
+--------------------------------------+

Named Pipe: \\.\pipe\CC_ClaudeDirector
  claude.exe hooks --> JSON events --> Director
  Director --> input replies --> claude.exe
```

### 3.2 Communication Flows

| Flow | Path | Transport |
|------|------|-----------|
| Terminal output (live) | ConPTY -> Buffer -> Supabase Realtime -> Dashboard xterm.js | Supabase Realtime (batched 50-100ms) |
| Structured events | claude hooks -> Named Pipe -> Director Event Router | Named pipe JSON (`\\.\pipe\CC_ClaudeDirector`) |
| Input replies | Director -> Named Pipe -> claude | Named pipe JSON (bidirectional) |
| Commands to agent | Dashboard -> Vercel API -> Supabase command channel -> Agent | Supabase Realtime subscription |
| Buffer replay | Dashboard -> Vercel API -> Supabase | REST |
| Metadata (nodes/repos) | Agent -> Supabase Postgres | REST |
| Session archival | Agent -> Supabase Storage | REST (on session end) |
| Local terminal I/O | Browser <-> Agent local WebSocket | WebSocket (binary, bidirectional) |

### 3.3 Component Summary

**The Node (.NET 10 Agent)**
- **Data plane**: ConPTY hosting of `claude.exe` with full ANSI/VT100 support
- **Control plane**: Named pipe server receiving structured JSON events from claude hooks
- Circular byte buffer (~2MB/session) in RAM for raw terminal bytes
- Event router: processes named pipe events to update session state, auto-respond to prompts
- All outbound: pushes to Supabase Realtime, subscribes to command channel
- Local REST API + WebSocket for same-machine access

**The Command Center (React Dashboard on Vercel)**
- Static SPA + API routes
- xterm.js terminal emulator rendering from Supabase Realtime
- Sends commands through Vercel API -> Supabase -> Agent

**Storage Layer (Supabase)**
- **Realtime**: Live bidirectional relay (terminal bytes + commands)
- **Postgres**: Node registry, repo metadata, sessions, command queue
- **Storage**: Archived session transcripts (cold)

---

## 4. UI Layout

```
+-----------------------------------------------------------------------------------+
|  CLAUDE CODE DIRECTOR       [ Node: Desktop-Main ]  [ Status: Connected ]         |
+----------+---------------------------------------------+-------------------------+
| NODES    | TERMINAL: repo-1 (Worktree: main)           | PIPE MESSAGES           |
|----------|  [*] Idle   [Kill] [Resize]                  |-------------------------|
| > Desk   |---------------------------------------------| 14:32:01 response_end   |
|   Lapt   |                                             |   PID:12345 session:ab1 |
|          |   $ claude                                  |   state: Working->Idle  |
| SESSIONS |   (xterm.js renders from live buffer)       |                         |
|----------|   > Building solution...                    | 14:31:45 input_needed   |
| [*] ab1  |   [################....] 80%                |   PID:12345 session:ab1 |
| [~] cd2  |   Done. Tests passed.                       |   "Allow file read?"    |
| [!] ef3  |   > _                                      |   state: Working->Wait  |
|          |                                             |                         |
| REPOS    |                                             | 14:31:30 response_end   |
|----------|                                             |   PID:67890 session:cd2 |
| repo-1 > |                                             |   state: Working->Idle  |
| repo-2   |                                             |                         |
|          |                                             | [Clear] [Pause] [Filter]|
+----------+---------------------------------------------+-------------------------+
| [ Prompt ] > Type instruction for Claude...                            [ SEND ]   |
+-----------------------------------------------------------------------------------+
```

### 4.1 Session Status Colors (Hook-Driven)

Session states are **driven by named pipe events** from claude hooks. Each state maps to a color:

| State | Color | Indicator | Triggered By |
|-------|-------|-----------|-------------|
| Starting | Gray | `[ ]` | Session created, claude.exe spawning |
| Idle | **Green** | `[*]` | `Stop` hook fires -> `response_end` pipe message |
| Working | **Blue** | `[~]` | Input sent to session (text or keystroke) |
| WaitingForInput | **Amber/Yellow** | `[!]` | `Notification` hook fires -> `input_needed` pipe message |
| WaitingForPermission | **Red** | `[!!]` | `Notification` hook fires -> `permission_prompt` pipe message |
| Exited | Dark Gray | `[x]` | Process exit detected |

**Color transitions happen in real-time** as pipe events arrive:
```
User sends prompt  -->  [~] Blue (Working)
  ...claude thinking...
Stop hook fires    -->  [*] Green (Idle)         -- response complete
  ...or...
Notification fires -->  [!] Amber (WaitingForInput) -- needs approval
User approves      -->  [~] Blue (Working)       -- back to work
Stop hook fires    -->  [*] Green (Idle)         -- done
```

In the **Sessions list** (left sidebar), each session shows its colored indicator. At a glance you can see:
- Which sessions are idle (green) - ready for next task
- Which are actively working (blue) - claude is generating
- Which need attention (amber/red) - waiting for user input or permission

### 4.2 Pipe Messages Panel (Right Sidebar)

The **Pipe Messages** panel is a live scrolling log of every JSON message that flows through `\\.\pipe\CC_ClaudeDirector`. It provides full visibility into the control plane.

**Each message row shows:**
```
[timestamp] [event_type]
  PID:[pid] session:[short_id]
  [state transition or message content]
```

**Color coding per event type:**
- `response_end` - Green text (claude finished)
- `input_needed` - Amber text (needs attention)
- `permission_prompt` - Red text (needs approval)
- `state_changed` - Blue text (any other transition)

**Panel controls:**
- **Clear** - Clears the log
- **Pause** - Freezes the scroll (new messages still buffer)
- **Filter** - Filter by session, event type, or PID

**Data source:**
- Local mode: SSE from `GET /api/sessions/{id}/events` or global `GET /api/pipe-events` SSE stream
- Cloud mode: Supabase Realtime subscription on `session_events` table

### 4.3 Input Handling

- **xterm.js** captures raw keystrokes and forwards via WebSocket/Supabase (for interactive prompts, tool approval, Ctrl+C)
- **PromptBar** is a convenience shortcut for sending text commands, not the only input method
- When a session is in **WaitingForInput** or **WaitingForPermission** state, the PromptBar highlights amber/red and shows the prompt message (e.g., "Allow file read? [yes/no]")

---

## 5. Phased Implementation

### Phase 1: Local Agent Core

**Goal**: .NET 10 agent that spawns, manages, and interacts with `claude.exe` via ConPTY on one machine.

#### 5.1.1 Project Structure

```
C:\ReposFred\cc_director\
+-- cc_director.sln
+-- src/
|   +-- CcDirector.Agent/
|   |   +-- CcDirector.Agent.csproj          (.NET 10, console + Windows Service)
|   |   +-- Program.cs                        (Host builder, REST + WebSocket + pipe setup)
|   |   +-- ConPty/
|   |   |   +-- NativeMethods.cs              (P/Invoke declarations)
|   |   |   +-- PseudoConsole.cs              (ConPTY handle lifecycle)
|   |   |   +-- ProcessHost.cs                (Spawn claude.exe, async I/O loops)
|   |   +-- Pipes/
|   |   |   +-- DirectorPipeServer.cs         (Named pipe server for claude hook events)
|   |   |   +-- PipeMessage.cs                (JSON message types: response_end, input_needed, etc.)
|   |   |   +-- EventRouter.cs               (Routes pipe events to session state machine)
|   |   +-- Hooks/
|   |   |   +-- HookInstaller.cs              (Writes/updates claude hook config in settings.json)
|   |   |   +-- hook-templates/
|   |   |       +-- stop-hook.cmd             (Writes JSON to named pipe on Stop)
|   |   |       +-- notification-hook.cmd     (Writes JSON to named pipe on Notification)
|   |   +-- Memory/
|   |   |   +-- CircularTerminalBuffer.cs     (Thread-safe circular byte buffer)
|   |   +-- Sessions/
|   |   |   +-- Session.cs                    (Single session: ConPTY + buffer + process + pipe events)
|   |   |   +-- SessionState.cs              (State machine: Idle, Working, WaitingForInput, etc.)
|   |   |   +-- SessionManager.cs             (Manages all sessions, lifecycle)
|   |   +-- Api/
|   |   |   +-- SessionEndpoints.cs           (Minimal API endpoint definitions)
|   |   |   +-- TerminalWebSocketHandler.cs   (Bidirectional binary WebSocket)
|   |   +-- appsettings.json
|   +-- CcDirector.Agent.Tests/
|       +-- CcDirector.Agent.Tests.csproj     (xUnit)
|       +-- CircularTerminalBufferTests.cs
|       +-- DirectorPipeServerTests.cs
|       +-- SessionManagerTests.cs
+-- test-client/
|   +-- index.html                            (Minimal xterm.js test page served by agent)
|   +-- terminal.js
+-- README.md
```

#### 5.1.2 ConPTY Wrapper

**NativeMethods.cs** - P/Invoke declarations:
```csharp
// Required Win32 APIs:
CreatePseudoConsole(COORD size, HANDLE hInput, HANDLE hOutput, DWORD dwFlags, out HPCON hPC)
ResizePseudoConsole(HPCON hPC, COORD size)
ClosePseudoConsole(HPCON hPC)
CreatePipe(out HANDLE hReadPipe, out HANDLE hWritePipe, SECURITY_ATTRIBUTES lpPipeAttributes, DWORD nSize)
InitializeProcThreadAttributeList(...)
UpdateProcThreadAttribute(... PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE ...)
CreateProcess(... EXTENDED_STARTUPINFO_PRESENT ...)
```

**PseudoConsole.cs** - Handle lifecycle:
- Constructor: `CreatePipe` for input + output pipes, then `CreatePseudoConsole`
- `Resize(int cols, int rows)`: calls `ResizePseudoConsole`
- `IDisposable`: calls `ClosePseudoConsole`, closes pipe handles
- Configurable initial size (default 120x30)

**ProcessHost.cs** - Process management:
- `Start(string exePath, string args, string workingDir)`: creates `STARTUPINFOEX` with pseudo console attribute, calls `CreateProcess`
- Async drain loop: `Task.Run` reading from output pipe into circular buffer (8KB read chunks)
- Write method: writes bytes to input pipe
- `WaitForExit(CancellationToken)`: monitors process handle
- Graceful shutdown: write `0x03` (Ctrl+C) to input, wait 5s, then `TerminateProcess`

#### 5.1.3 Named Pipe Server (Control Plane)

**DirectorPipeServer.cs** - Listens on `\\.\pipe\CC_ClaudeDirector`:
```
Design:
- Single named pipe server, multi-instance (NamedPipeServerStream with MaxInstances)
- Each connected client (claude hook process) sends one JSON message and disconnects
- Async accept loop: await connection, read JSON, route to EventRouter, accept next

Protocol:
- Hook processes write a single JSON line and exit (fire-and-forget)
- Director reads the JSON, parses PipeMessage, routes by PID to correct session

Message format (from hooks):
{
  "type": "response_end" | "input_needed" | "permission_prompt",
  "pid": 12345,                    // claude.exe PID that fired the hook
  "timestamp": "2026-02-08T...",
  "message": "optional context"    // e.g., permission description for input_needed
}

Reply format (Director -> claude, for bidirectional pipe):
{
  "type": "input_reply",
  "pid": 12345,
  "reply": "yes"                   // or raw input text
}
```

**Lifecycle**:
- Starts with the agent (registered as IHostedService)
- Runs continuously, accepting connections
- If Director is not running, hook scripts silently fail (pipe doesn't exist)
- Thread-safe: each connection handled on thread pool

**PipeMessage.cs** - Strongly-typed event models:
```
PipeMessageType enum:
  ResponseEnd       // Claude finished a full response (Stop hook)
  InputNeeded       // Claude waiting for user input (Notification hook)
  PermissionPrompt  // Claude asking for tool permission (Notification hook)

PipeMessage:
  Type: PipeMessageType
  Pid: int
  Timestamp: DateTimeOffset
  Message: string?
```

**EventRouter.cs** - Maps events to sessions and updates state:
```
- Maintains PID -> SessionId lookup (populated when session spawns)
- On ResponseEnd: set session state to Idle, emit event to subscribers
- On InputNeeded: set session state to WaitingForInput, emit event
- On PermissionPrompt: set session state to WaitingForPermission, emit event
  - Can auto-approve based on session config (e.g., always allow file reads)
  - Or forward to dashboard for manual approval
- Unknown PIDs: log warning (orphan claude process)
```

#### 5.1.4 Hook Installer

**HookInstaller.cs** - Configures claude to send events to the Director:

```
On agent startup:
1. Read ~/.claude/settings.json (or create if missing)
2. Add/update hooks configuration:

{
  "hooks": {
    "Stop": [{
      "type": "command",
      "command": "cmd /c \"echo {\\\"type\\\":\\\"response_end\\\",\\\"pid\\\":%PID%} > \\\\.\\pipe\\CC_ClaudeDirector\""
    }],
    "Notification": [{
      "type": "command",
      "command": "cmd /c \"echo {\\\"type\\\":\\\"input_needed\\\",\\\"pid\\\":%PID%,\\\"message\\\":\\\"%s\\\"} > \\\\.\\pipe\\CC_ClaudeDirector\""
    }]
  }
}

3. Backup original settings before modifying
4. On agent shutdown: optionally restore original hooks (configurable)
```

**Hook script templates** (alternative to inline commands):

**stop-hook.cmd**:
```batch
@echo off
echo {"type":"response_end","pid":%PID%,"timestamp":"%DATE%T%TIME%"} > \\.\pipe\CC_ClaudeDirector 2>nul
```

**notification-hook.cmd**:
```batch
@echo off
echo {"type":"input_needed","pid":%PID%,"message":"%~1","timestamp":"%DATE%T%TIME%"} > \\.\pipe\CC_ClaudeDirector 2>nul
```

The `2>nul` ensures silent failure if the Director pipe doesn't exist.

#### 5.1.5 Session State Machine

**SessionState.cs** - Tracks claude's current activity per session:

```
States:
  Starting        -> claude.exe spawning
  Idle            -> claude finished response, waiting for next prompt
  Working         -> claude is generating a response
  WaitingForInput -> claude needs user input (Notification hook fired)
  WaitingForPerm  -> claude needs permission approval
  Exiting         -> graceful shutdown initiated
  Exited          -> process terminated

Transitions (driven by events):
  Starting        + (process running)     -> Idle
  Idle            + (input sent)          -> Working
  Working         + (ResponseEnd pipe)    -> Idle
  Working         + (InputNeeded pipe)    -> WaitingForInput
  Working         + (PermPrompt pipe)     -> WaitingForPerm
  WaitingForInput + (input reply)         -> Working
  WaitingForPerm  + (permission reply)    -> Working
  Any             + (process exited)      -> Exited
  Any             + (kill command)        -> Exiting -> Exited

Events emitted to subscribers (dashboard, Supabase):
  - StateChanged { sessionId, oldState, newState, message? }
```

This state machine is what enables the dashboard to show meaningful status per session (not just "running" but "idle", "working", "waiting for approval", etc.).

#### 5.1.6 Circular Buffer (Data Plane)

**CircularTerminalBuffer.cs**:
```
Design:
- Fixed-size byte array (default 2MB = 2,097,152 bytes)
- Write head advances on each write, wraps around
- No line tracking - stores raw ANSI bytes
- Thread-safe via ReaderWriterLockSlim (writers rare-block, readers concurrent)

Methods:
- Write(ReadOnlySpan<byte> data)     // Append bytes, advance write head
- DumpAll() -> byte[]                // Return all valid bytes in order
- GetWrittenSince(long position) -> (byte[] data, long newPosition)  // For streaming
- Clear()                            // Reset buffer
- Properties: TotalBytesWritten (monotonic counter for stream position tracking)
```

The `GetWrittenSince` method is key for WebSocket streaming: the WebSocket handler tracks its last-read position and periodically asks the buffer "what's new since position X?"

#### 5.1.4 Session Manager

**Session.cs**:
```
Properties:
- Id: Guid
- ProcessId: int                       // claude.exe PID (for pipe event routing)
- RepoPath: string
- WorkingDirectory: string
- State: SessionState                  // State machine (Idle, Working, WaitingForInput, etc.)
- CreatedAt: DateTimeOffset
- PseudoConsole: PseudoConsole
- ProcessHost: ProcessHost
- Buffer: CircularTerminalBuffer

Events:
- OnStateChanged: event<StateChangedArgs>   // Fired on state transitions

Methods:
- SendInput(byte[] data)              // Write to ConPTY input pipe, transition to Working
- SendText(string text)               // Convenience: encode UTF-8 + append \n
- ReplyToPrompt(string reply)         // Send reply via named pipe (for permission prompts)
- Resize(int cols, int rows)          // ResizePseudoConsole
- Kill()                              // Graceful Ctrl+C, then terminate
- HandlePipeEvent(PipeMessage msg)    // Called by EventRouter, updates state machine
```

**SessionManager.cs**:
```
- ConcurrentDictionary<Guid, Session> _sessions
- CreateSession(string repoPath, string? claudeArgs) -> Session
  - Validates repo path exists
  - Creates PseudoConsole, ProcessHost, CircularTerminalBuffer
  - Spawns claude.exe in repo directory
  - Starts drain loop and exit monitor
  - Returns session

- GetSession(Guid id) -> Session?
- ListSessions() -> IEnumerable<SessionInfo>
- KillSession(Guid id)

- On startup: scan for orphaned claude.exe processes
  - Can't re-attach ConPTY to existing processes
  - Log warning, optionally kill orphans
```

#### 5.1.5 REST API (Minimal API)

**SessionEndpoints.cs**:
```
POST   /api/sessions                    -> Create session { repoPath, args? }
GET    /api/sessions                    -> List all sessions (includes state per session)
GET    /api/sessions/{id}               -> Session details + state + last event
GET    /api/sessions/{id}/buffer        -> Dump entire circular buffer as binary
POST   /api/sessions/{id}/input         -> Send text input { text }
POST   /api/sessions/{id}/input/raw     -> Send raw bytes (base64 encoded)
POST   /api/sessions/{id}/reply         -> Reply to prompt/permission { reply }
POST   /api/sessions/{id}/resize        -> Resize terminal { cols, rows }
DELETE /api/sessions/{id}               -> Kill session

GET    /api/sessions/{id}/events        -> SSE stream of session state changes
GET    /api/pipe-events                 -> SSE stream of ALL pipe messages (for Pipe Messages panel)
GET    /api/health                      -> Agent health check
GET    /api/repos                       -> List registered repositories
```

The `/events` SSE endpoint streams `SessionState` transitions in real-time, enabling the dashboard to show live status (Idle, Working, WaitingForInput) without polling.

#### 5.1.6 Local WebSocket

**TerminalWebSocketHandler.cs**:
```
Endpoint: ws://localhost:5100/ws/sessions/{id}/terminal

On connect:
1. Send entire buffer dump (binary frame)
2. Record current buffer position

Streaming loop:
3. Every 50ms, check buffer for new bytes via GetWrittenSince(position)
4. If new bytes exist, send as binary WebSocket frame
5. Update position

Receive loop (concurrent):
6. Read binary frames from client
7. Write raw bytes to session's ConPTY input pipe

Backpressure:
- If send buffer exceeds threshold, skip frames (log warning)
- Client can send "pause"/"resume" text messages

On disconnect:
- Clean up, stop loops
- Session continues running (multiple viewers supported)
```

#### 5.1.7 Configuration

**appsettings.json**:
```json
{
  "Agent": {
    "ClaudePath": "claude",
    "DefaultBufferSizeBytes": 2097152,
    "ApiPort": 5100,
    "WebSocketFlushIntervalMs": 50,
    "GracefulShutdownTimeoutSeconds": 5
  },
  "Repositories": [
    {
      "Name": "mindzieWeb",
      "Path": "C:\\ReposMindzie\\mindzieWeb"
    },
    {
      "Name": "mindzieEngine",
      "Path": "C:\\ReposMindzie\\mindzieEngine"
    }
  ]
}
```

#### 5.1.8 Test Client

**test-client/index.html**: Minimal page served by the agent at `http://localhost:5100`:
- Loads xterm.js from CDN
- Connects to `ws://localhost:5100/ws/sessions/{id}/terminal`
- Renders terminal output
- Captures keyboard input and sends back via WebSocket
- Dropdown to select active session
- Buttons: New Session, Kill, Resize

---

### Phase 2: Cloud Bridge (Supabase Realtime + Vercel API)

**Goal**: Connect the local agent to the cloud so the React dashboard can control it remotely.

#### 5.2.1 Supabase Schema

```sql
-- Node registry
CREATE TABLE nodes (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  machine_name TEXT NOT NULL,
  os_version TEXT,
  agent_version TEXT,
  status TEXT NOT NULL DEFAULT 'online' CHECK (status IN ('online', 'offline', 'stale')),
  last_heartbeat TIMESTAMPTZ NOT NULL DEFAULT now(),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE(machine_name)
);

CREATE INDEX idx_nodes_status ON nodes(status);

-- Repositories
CREATE TABLE repositories (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  node_id UUID NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
  name TEXT NOT NULL,
  path TEXT NOT NULL,
  current_branch TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE(node_id, path)
);

-- Sessions
CREATE TABLE sessions (
  id UUID PRIMARY KEY,  -- Agent generates the UUID
  node_id UUID NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
  repo_id UUID REFERENCES repositories(id) ON DELETE SET NULL,
  status TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'exited', 'killed', 'failed', 'archived')),
  current_state TEXT NOT NULL DEFAULT 'starting' CHECK (current_state IN ('starting', 'idle', 'working', 'waiting_for_input', 'waiting_for_permission', 'exiting', 'exited')),
  claude_args TEXT,
  started_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  ended_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_sessions_node_status ON sessions(node_id, status);

-- Command queue (dashboard -> agent)
CREATE TABLE command_queue (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id UUID NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  command_type TEXT NOT NULL CHECK (command_type IN ('input', 'input_raw', 'resize', 'kill', 'reply')),
  payload JSONB NOT NULL DEFAULT '{}',
  status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'processing', 'completed', 'failed')),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  processed_at TIMESTAMPTZ
);

CREATE INDEX idx_command_queue_pending ON command_queue(session_id, status) WHERE status = 'pending';

-- Session state events (agent pushes state transitions for dashboard awareness)
CREATE TABLE session_events (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id UUID NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  event_type TEXT NOT NULL CHECK (event_type IN ('state_changed', 'response_end', 'input_needed', 'permission_prompt')),
  old_state TEXT,
  new_state TEXT NOT NULL,
  message TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_session_events_session ON session_events(session_id, created_at DESC);

-- Enable Realtime on relevant tables
ALTER PUBLICATION supabase_realtime ADD TABLE command_queue;
ALTER PUBLICATION supabase_realtime ADD TABLE session_events;
```

**Supabase Storage**: Bucket `session-transcripts` for archived terminal output.

**Supabase Realtime Channels**:
- `terminal:{session_id}` - Agent publishes terminal byte chunks (base64 encoded)
- Agent subscribes to `command_queue` table changes (INSERT) filtered by its sessions

#### 5.2.2 Agent Cloud Service

**New files**:
```
src/CcDirector.Agent/Cloud/
  +-- SupabaseConfig.cs              (Connection settings)
  +-- SupabaseNodeRegistration.cs    (Register node, heartbeat, repo sync)
  +-- SupabaseTerminalPublisher.cs   (Publish terminal bytes to Realtime channel)
  +-- SupabaseCommandListener.cs     (Subscribe to command_queue, execute commands)
  +-- SupabaseArchiver.cs            (Flush completed sessions to Storage)
```

**SupabaseNodeRegistration.cs**:
- On startup: upsert into `nodes` table (machine_name, agent_version)
- Background timer: update `last_heartbeat` every 30s
- On startup: sync `repositories` table with configured repos
- On shutdown: set status to `offline`

**SupabaseTerminalPublisher.cs**:
- Subscribes to each session's buffer changes
- Batches terminal bytes every 50-100ms
- Publishes to Supabase Realtime channel `terminal:{session_id}` as base64
- Handles Supabase Realtime message size limits (~256KB per message)
- Chunks large outputs if needed

**SupabaseCommandListener.cs**:
- Subscribes to `command_queue` INSERT events (filtered by node's session IDs)
- On new command:
  - `input`: send text to session
  - `input_raw`: send base64-decoded bytes to session
  - `resize`: resize session terminal
  - `kill`: kill session
- Updates command status to `completed` or `failed`

**SupabaseArchiver.cs**:
- When session exits, dump entire buffer
- Upload to Supabase Storage: `session-transcripts/{session_id}.bin`
- Update session status to `archived`

#### 5.2.3 Vercel API Routes

**Dashboard project** (Next.js or standalone):
```
dashboard/
  +-- api/
  |   +-- sessions/
  |   |   +-- route.ts                  GET (list), POST (create via command_queue)
  |   |   +-- [id]/
  |   |       +-- route.ts              GET (details), DELETE (kill via command_queue)
  |   |       +-- input/route.ts        POST (send input via command_queue)
  |   |       +-- resize/route.ts       POST (resize via command_queue)
  |   |       +-- buffer/route.ts       GET (fetch archived transcript from Storage)
  |   +-- nodes/
  |   |   +-- route.ts                  GET (list nodes from Postgres)
  |   +-- repos/
  |       +-- route.ts                  GET (list repos from Postgres)
```

All mutating API routes insert into `command_queue` table. The agent picks up commands via Realtime subscription.

#### 5.2.4 Authentication

- **Agent -> Supabase**: Service role key (stored in agent's `appsettings.json` or environment variable)
- **Dashboard -> Vercel API**: JWT (Supabase Auth or custom)
- **Phase 1 local API**: No auth (localhost only)

---

### Phase 3: Web Dashboard + Multi-Node

**Goal**: Full React dashboard on Vercel with multi-node support.

#### 5.3.1 Project Setup

```
dashboard/
  +-- package.json                    (Vite + React + TypeScript)
  +-- src/
  |   +-- App.tsx
  |   +-- components/
  |   |   +-- NodeSelector.tsx        (Left sidebar: node list)
  |   |   +-- RepoList.tsx            (Repos for selected node)
  |   |   +-- SessionList.tsx         (Active sessions with colored state indicators)
  |   |   +-- SessionIndicator.tsx    (Colored dot/icon component for session state)
  |   |   +-- TerminalPanel.tsx       (xterm.js terminal view)
  |   |   +-- PipeMessagesPanel.tsx   (Right sidebar: live named pipe event log)
  |   |   +-- PipeMessageRow.tsx      (Single color-coded event row)
  |   |   +-- PromptBar.tsx           (Bottom input bar, highlights on WaitingForInput)
  |   |   +-- StatusBar.tsx           (Top bar: connection status)
  |   +-- hooks/
  |   |   +-- useSupabaseRealtime.ts  (Subscribe to terminal channel)
  |   |   +-- usePipeEvents.ts        (Subscribe to session_events for pipe messages panel)
  |   |   +-- useSessionState.ts      (Track session state + color from events)
  |   |   +-- useSession.ts           (Session state management)
  |   +-- lib/
  |   |   +-- supabase.ts             (Supabase client init)
  |   |   +-- api.ts                  (Vercel API client)
  |   +-- types/
  |       +-- index.ts                (TypeScript interfaces)
  +-- vercel.json
```

**Dependencies**:
```json
{
  "@xterm/xterm": "^5.x",
  "@xterm/addon-fit": "^0.10.x",
  "@xterm/addon-web-links": "^0.11.x",
  "@supabase/supabase-js": "^2.x",
  "react": "^19.x",
  "react-dom": "^19.x"
}
```

#### 5.3.2 Terminal Integration

**TerminalPanel.tsx** flow:
1. On session select: fetch archived/current buffer via Vercel API
2. Write buffer bytes to xterm.js instance
3. Subscribe to Supabase Realtime channel `terminal:{session_id}`
4. On each Realtime message: decode base64, write to xterm.js
5. On xterm.js `onData` event: POST to Vercel API `/api/sessions/{id}/input`
6. On xterm.js resize: POST to Vercel API `/api/sessions/{id}/resize`

**useSupabaseRealtime.ts**:
```typescript
// Subscribe to terminal output channel
const channel = supabase.channel(`terminal:${sessionId}`)
  .on('broadcast', { event: 'output' }, (payload) => {
    const bytes = base64ToUint8Array(payload.data);
    terminal.write(bytes);
  })
  .subscribe();
```

#### 5.3.3 Pipe Messages Panel Component

**PipeMessagesPanel.tsx** - Live event log of all named pipe messages:

```typescript
// Data source: Supabase Realtime subscription on session_events table
const channel = supabase.channel('pipe-events')
  .on('postgres_changes', {
    event: 'INSERT',
    schema: 'public',
    table: 'session_events'
  }, (payload) => {
    addMessage(payload.new as PipeEvent);
  })
  .subscribe();
```

**PipeEvent type**:
```typescript
interface PipeEvent {
  id: string;
  session_id: string;
  event_type: 'state_changed' | 'response_end' | 'input_needed' | 'permission_prompt';
  old_state: string | null;
  new_state: string;
  message: string | null;
  created_at: string;
}
```

**PipeMessageRow.tsx** - Color-coded per event type:
```typescript
const EVENT_COLORS = {
  response_end:     { text: '#22c55e', bg: '#052e16' },  // Green
  input_needed:     { text: '#f59e0b', bg: '#451a03' },  // Amber
  permission_prompt:{ text: '#ef4444', bg: '#450a0a' },  // Red
  state_changed:    { text: '#3b82f6', bg: '#172554' },  // Blue
};
```

**Panel features**:
- Auto-scrolls to bottom (latest events)
- Clicking a message selects that session in the terminal panel
- Max 500 messages in view (older ones pruned for performance)
- Timestamp in local time, relative format ("3s ago", "1m ago")

#### 5.3.4 Session State Indicators

**SessionIndicator.tsx** - Small colored component shown next to each session:

```typescript
const STATE_CONFIG = {
  starting:               { color: '#6b7280', icon: '[ ]',  label: 'Starting',    pulse: true },
  idle:                   { color: '#22c55e', icon: '[*]',  label: 'Idle',         pulse: false },
  working:                { color: '#3b82f6', icon: '[~]',  label: 'Working',      pulse: true },
  waiting_for_input:      { color: '#f59e0b', icon: '[!]',  label: 'Needs Input',  pulse: true },
  waiting_for_permission: { color: '#ef4444', icon: '[!!]', label: 'Needs Approval', pulse: true },
  exiting:                { color: '#6b7280', icon: '[x]',  label: 'Exiting',      pulse: false },
  exited:                 { color: '#374151', icon: '[x]',  label: 'Exited',       pulse: false },
};
```

- `pulse: true` adds a CSS animation (subtle glow/blink) to draw attention
- Renders as a colored dot/circle + short label
- Used in `SessionList.tsx` and in the terminal header bar

**useSessionState.ts** hook:
```typescript
// Subscribes to session_events for a specific session
// Returns current state + color + whether attention is needed
function useSessionState(sessionId: string) {
  // Listen to Supabase Realtime for this session's events
  // Update state on each event
  return { state, color, needsAttention, lastEvent };
}
```

#### 5.3.5 PromptBar State-Aware Behavior

When the active session enters `waiting_for_input` or `waiting_for_permission`:
1. PromptBar border changes to **amber** or **red**
2. The prompt message from the pipe event displays above the input field (e.g., "Allow file read on /src/main.rs? [yes/no/always]")
3. Quick-reply buttons appear: **[Yes]** **[No]** **[Always Allow]**
4. User can also type a custom reply in the input field
5. On reply, sends through command queue and PromptBar returns to normal state

#### 5.3.6 Multi-Node Support

- **NodeSelector** queries `nodes` table, shows online/offline status
- Supabase Realtime subscription on `nodes` table for live status updates
- Selecting a node filters repos and sessions
- Health: nodes with `last_heartbeat` > 60s ago marked as `stale`

#### 5.3.7 Git Worktree Management (Agent-side)

**New files**:
```
src/CcDirector.Agent/Git/
  +-- WorktreeManager.cs              (LibGit2Sharp: create, list, switch worktrees)
```

**NuGet**: `LibGit2Sharp`

**REST Endpoints**:
```
GET    /api/repos/{id}/worktrees          -> List worktrees
POST   /api/repos/{id}/worktrees          -> Create worktree { branch, path }
DELETE /api/repos/{id}/worktrees/{name}   -> Remove worktree
```

Sessions can target a specific worktree path instead of the main repo path.

---

## 6. Key Technical Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Project location | `C:\ReposFred\cc_director` | Standalone, separate from mindzieWeb |
| .NET version | .NET 10 | Per PRD; latest LTS |
| Architecture | Dual-channel (ConPTY + Named Pipe) | Data plane for visual rendering, control plane for structured orchestration |
| Terminal hosting | ConPTY (data plane) | Full ANSI/VT100: colors, progress bars, cursor movement |
| Event system | Named pipe + hooks (control plane) | Zero-latency structured events from claude lifecycle. Fire-and-forget. |
| Buffer design | Circular raw byte array | Fixed memory, O(1) writes. No ANSI line parsing - let xterm.js handle it |
| Network model | Agent pushes outbound | No NAT/firewall issues. Agent initiates all connections to Supabase |
| Realtime relay | Supabase Realtime channels | Agent publishes, dashboard subscribes. Bidirectional via command queue |
| Input model | Bidirectional raw bytes + pipe replies | ConPTY for keystrokes, named pipe for structured replies (permission prompts) |
| Dashboard | Vite + React + TypeScript | Fast builds, simple config, deploys to Vercel |
| Supabase role | Hot (Realtime) + Cold (Postgres/Storage) | Realtime is natural relay for agent-push; deviation from PRD "cold only" |
| Windows Service | `UseWindowsService()` | Single codebase runs as console (dev) or service (prod) |

---

## 7. Important Design Notes

### 7.1 Named Pipe + Hooks Architecture

**Why named pipes instead of parsing ConPTY output?**
- ConPTY output is raw ANSI bytes. Detecting "claude is waiting for input" requires parsing complex terminal escape sequences, which is fragile and error-prone.
- Named pipes provide **structured, reliable events** directly from claude's lifecycle hooks.
- Zero latency: pipes are kernel-level IPC, faster than any network protocol.
- Fire-and-forget: hooks silently fail if the pipe doesn't exist (Director not running). Claude works normally either way.
- No ports: unlike REST/WebSocket, named pipes don't consume TCP ports or require firewall rules.

**Pipe server design**:
- Single pipe name `\\.\pipe\CC_ClaudeDirector` with multiple instances
- Each hook invocation creates a new pipe client, writes one JSON message, disconnects
- Server runs async accept loop: `await NamedPipeServerStream.WaitForConnectionAsync()`
- After reading message, immediately start listening for next connection
- PID in each message maps to the correct session via `EventRouter`

**Hook installation considerations**:
- Hooks are configured in `~/.claude/settings.json` (global) or `.claude/settings.json` (project)
- Director should use **global** hooks to capture events from ALL claude instances
- Must preserve existing user hooks (merge, don't overwrite)
- On agent shutdown: can optionally clean up hooks (configurable)
- Hook commands must handle Windows path escaping for the pipe path

**Bidirectional communication**:
- Hooks are one-way (claude -> Director) by default
- For Director -> claude replies (e.g., auto-approving permissions), the named pipe supports bidirectional writes
- Alternative: Director writes to claude's ConPTY input pipe directly (simpler for most cases)
- Named pipe reply is useful when you want to respond to a specific structured prompt without sending raw keystrokes

### 7.2 Supabase Realtime Considerations

- Message size limit: ~256KB per broadcast message
- Agent must chunk large terminal outputs
- Batch terminal bytes every 50-100ms to avoid flooding
- Progress bars updating at 60fps will be throttled by batching (acceptable)

### 7.3 Process Lifecycle

- **Graceful shutdown**: Write `0x03` (Ctrl+C) to input pipe, wait 5s, then `TerminateProcess`
- **Process exit detection**: Background task monitors process handle via `WaitForSingleObject`. Auto-updates session status.
- **Orphan handling**: On agent startup, enumerate `claude.exe` processes. Cannot re-attach ConPTY to existing processes. Kill orphans or mark sessions as "lost."

### 7.4 claude CLI Modes

| Mode | Flags | Use Case |
|------|-------|----------|
| Interactive (default) | none | Full TUI in xterm.js. Progress bars, tool approvals. |
| Stream JSON | `--output-format stream-json --input-format stream-json` | Programmatic control (like VSCode). Structured output. |
| Non-interactive | `--print` or `--no-input` | One-shot commands, no TUI. |

The Director should default to interactive mode for the xterm.js experience. Stream JSON mode can be added later for API-driven orchestration.

### 7.5 Terminal Resize

When xterm.js resizes (browser window change, user resize):
1. Dashboard sends `{ cols, rows }` via command queue
2. Agent calls `ResizePseudoConsole(hPC, new COORD(cols, rows))`
3. Claude's TUI re-renders for the new dimensions

### 7.6 Buffer Replay Strategy

On dashboard connection to an active session:
1. Fetch current buffer snapshot (entire 2MB if full)
2. Write to xterm.js (it parses all ANSI sequences)
3. Subscribe to Realtime for live updates from that point forward
4. No line counting, no ANSI parsing on server side

---

## 8. Verification Plan

### Phase 1 (Local)

| # | Test | Expected Result |
|---|------|-----------------|
| 1 | `dotnet run` | Agent starts on port 5100, named pipe created |
| 2 | `POST /api/sessions` with repo path | claude.exe spawns (visible in tasklist) |
| 3 | Verify hooks installed | `~/.claude/settings.json` contains Director hooks |
| 4 | Open `http://localhost:5100` | xterm.js test page renders |
| 5 | Connect WebSocket | Live terminal bytes stream to xterm.js |
| 6 | Type in xterm.js terminal | Keystrokes reach claude, responses appear |
| 7 | Wait for claude response to finish | `Stop` hook fires, pipe receives `response_end`, session state -> Idle |
| 8 | Claude asks for tool permission | `Notification` hook fires, pipe receives `input_needed`, state -> WaitingForInput |
| 9 | `GET /api/sessions/{id}` | Returns session with current state (Idle/Working/WaitingForInput) |
| 10 | `GET /api/sessions/{id}/events` (SSE) | Streams state transitions in real-time |
| 11 | Resize browser | Terminal re-wraps correctly |
| 12 | `DELETE /api/sessions/{id}` | claude.exe process terminated |
| 13 | Kill claude externally | Session state auto-transitions to Exited |
| 14 | `GET /api/sessions/{id}/buffer` | Returns raw terminal history bytes |
| 15 | Install as Windows Service | Auto-starts on reboot, pipe re-created |

### Phase 2 (Cloud Bridge)

| # | Test | Expected Result |
|---|------|-----------------|
| 1 | Start agent | Node appears in Supabase `nodes` table |
| 2 | Create session | Realtime channel receives terminal bytes |
| 3 | Subscribe from test client | Live output streams via Supabase |
| 4 | Insert command in `command_queue` | Agent executes it |
| 5 | End session | Transcript archived in Supabase Storage |
| 6 | Hit Vercel API route | Reads/writes Supabase correctly |

### Phase 3 (Full Dashboard - Colors + Pipe Panel)

| # | Test | Expected Result |
|---|------|-----------------|
| 1 | Open Vercel dashboard | Node list populates, sessions show colored indicators |
| 2 | Session in Idle state | Green dot `[*]` next to session, no pulse |
| 3 | Send prompt to session | Indicator changes to Blue `[~]` (Working) with pulse |
| 4 | Claude finishes response | Stop hook fires -> pipe event -> indicator turns Green `[*]` (Idle) |
| 5 | Claude asks for permission | Notification hook fires -> indicator turns Amber `[!]` with pulse |
| 6 | PromptBar reacts | Border turns amber, shows "Allow file read?" with [Yes][No] buttons |
| 7 | Click [Yes] | Reply sent through command queue, indicator returns to Blue `[~]` |
| 8 | Pipe Messages panel | Right sidebar shows scrolling log of all pipe events |
| 9 | Each pipe message color-coded | `response_end` green, `input_needed` amber, `permission_prompt` red |
| 10 | Click pipe message | Selects corresponding session in terminal panel |
| 11 | Multiple sessions visible | Session list shows mixed indicators (green, blue, amber) at a glance |
| 12 | Type in prompt bar | Command flows: Dashboard -> Vercel -> Supabase -> Agent |
| 13 | Run agents on 2 machines | Both appear and are switchable |
| 14 | Disconnect agent | Dashboard shows node as offline, sessions go dark gray |

---

## 9. Dependencies

### .NET Agent (NuGet)
```
Microsoft.Extensions.Hosting.WindowsServices
Supabase                                      (Phase 2)
LibGit2Sharp                                  (Phase 3)
```

### React Dashboard (npm)
```
@xterm/xterm
@xterm/addon-fit
@xterm/addon-web-links
@supabase/supabase-js
react / react-dom
typescript
vite
```

### Infrastructure
```
Supabase project (free tier sufficient for dev)
Vercel account (free tier for dashboard hosting)
```
