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

---

## 3. System Architecture

### 3.1 Data Flow (Agent-Push Model)

```
+----------------------+          +-------------------------------+
|  LOCAL MACHINE       |          |  CLOUD (Vercel + Supabase)    |
|                      |          |                               |
|  .NET 10 Agent       |          |  Vercel (React SPA + API)     |
|  +----------+        |  push    |  +-----------------+          |
|  | ConPTY   |--bytes--+-------->|  | /api/sessions   |          |
|  | claude   |        |          |  | /api/buffer     |          |
|  +----------+        |          |  +---------+-------+          |
|       |              |          |            |                   |
|  +----v------+       |          |  +---------v--------+         |
|  | Circular  |       |  push    |  | Supabase         |         |
|  | Buffer    |--meta--+-------->|  | - Realtime ch    |<--sub--+|
|  | (2MB RAM) |       |          |  | - Postgres       |        ||
|  +-----------+       |          |  | - Storage        |        ||
|       |              |          |  +------------------+        ||
|  +----v------+       |  poll    |                              ||
|  | Session   |<--cmd--+--------+  React Dashboard -------------+|
|  | Manager   |       |          |  (xterm.js + Supabase sub)    |
|  +-----------+       |          +-------------------------------+
+----------------------+
```

### 3.2 Communication Flows

| Flow | Path | Transport |
|------|------|-----------|
| Terminal output (live) | Agent -> Supabase Realtime channel -> Dashboard xterm.js | Supabase Realtime (batched 50-100ms) |
| Commands to agent | Dashboard -> Vercel API -> Supabase command channel -> Agent | Supabase Realtime subscription |
| Buffer replay | Dashboard -> Vercel API -> Supabase | REST |
| Metadata (nodes/repos) | Agent -> Supabase Postgres | REST |
| Session archival | Agent -> Supabase Storage | REST (on session end) |
| Local terminal I/O | Browser <-> Agent local WebSocket | WebSocket (binary, bidirectional) |

### 3.3 Component Summary

**The Node (.NET 10 Agent)**
- ConPTY hosting of `claude.exe` with full ANSI/VT100 support
- Circular byte buffer (~2MB/session) in RAM for raw terminal bytes
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
+-----------------------------------------------------------------------+
|  CLAUDE CODE DIRECTOR  [ Node: Desktop-Main ] [ Status: Connected ]   |
+----------+------------------------------------------------------------+
| NODES    | TERMINAL: repo-1 (Worktree: main)          [ Kill ] [ Res ] |
|----------+------------------------------------------------------------+
| > Desk   |                                                            |
|   Lapt   |    $ claude                                                |
|          |    (xterm.js renders from live memory buffer)              |
| REPOS    |    > Building solution...                                  |
|----------|    [################....] 80%                              |
| repo-1 > |    Done. Tests passed.                                     |
| repo-2   |    > _                                                     |
|          |                                                            |
+----------+------------------------------------------------------------+
| [ Prompt ] > Type instruction for Claude...                  [ SEND ] |
+-----------------------------------------------------------------------+
```

- **xterm.js** captures raw keystrokes and forwards via WebSocket/Supabase (for interactive prompts, tool approval, Ctrl+C)
- **PromptBar** is a convenience shortcut for sending text commands, not the only input method

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
|   |   +-- Program.cs                        (Host builder, REST + WebSocket setup)
|   |   +-- ConPty/
|   |   |   +-- NativeMethods.cs              (P/Invoke declarations)
|   |   |   +-- PseudoConsole.cs              (ConPTY handle lifecycle)
|   |   |   +-- ProcessHost.cs                (Spawn claude.exe, async I/O loops)
|   |   +-- Memory/
|   |   |   +-- CircularTerminalBuffer.cs     (Thread-safe circular byte buffer)
|   |   +-- Sessions/
|   |   |   +-- Session.cs                    (Single session: ConPTY + buffer + process)
|   |   |   +-- SessionManager.cs             (Manages all sessions, lifecycle)
|   |   +-- Api/
|   |   |   +-- SessionEndpoints.cs           (Minimal API endpoint definitions)
|   |   |   +-- TerminalWebSocketHandler.cs   (Bidirectional binary WebSocket)
|   |   +-- appsettings.json
|   +-- CcDirector.Agent.Tests/
|       +-- CcDirector.Agent.Tests.csproj     (xUnit)
|       +-- CircularTerminalBufferTests.cs
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

#### 5.1.3 Circular Buffer

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
- RepoPath: string
- WorkingDirectory: string
- Status: enum (Starting, Running, Exiting, Exited, Failed)
- CreatedAt: DateTimeOffset
- PseudoConsole: PseudoConsole
- ProcessHost: ProcessHost
- Buffer: CircularTerminalBuffer

Methods:
- SendInput(byte[] data)              // Write to ConPTY input pipe
- SendText(string text)               // Convenience: encode UTF-8 + append \n
- Resize(int cols, int rows)          // ResizePseudoConsole
- Kill()                              // Graceful Ctrl+C, then terminate
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
GET    /api/sessions                    -> List all sessions
GET    /api/sessions/{id}               -> Session details + status
GET    /api/sessions/{id}/buffer        -> Dump entire circular buffer as binary
POST   /api/sessions/{id}/input         -> Send text input { text }
POST   /api/sessions/{id}/input/raw     -> Send raw bytes (base64 encoded)
POST   /api/sessions/{id}/resize        -> Resize terminal { cols, rows }
DELETE /api/sessions/{id}               -> Kill session

GET    /api/health                      -> Agent health check
GET    /api/repos                       -> List registered repositories
```

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
  command_type TEXT NOT NULL CHECK (command_type IN ('input', 'input_raw', 'resize', 'kill')),
  payload JSONB NOT NULL DEFAULT '{}',
  status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'processing', 'completed', 'failed')),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  processed_at TIMESTAMPTZ
);

CREATE INDEX idx_command_queue_pending ON command_queue(session_id, status) WHERE status = 'pending';

-- Enable Realtime on relevant tables
ALTER PUBLICATION supabase_realtime ADD TABLE command_queue;
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
  |   |   +-- TerminalPanel.tsx       (xterm.js terminal view)
  |   |   +-- PromptBar.tsx           (Bottom input bar)
  |   |   +-- StatusBar.tsx           (Top bar: connection status)
  |   |   +-- SessionList.tsx         (Active sessions for selected node)
  |   +-- hooks/
  |   |   +-- useSupabaseRealtime.ts  (Subscribe to terminal channel)
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

#### 5.3.3 Multi-Node Support

- **NodeSelector** queries `nodes` table, shows online/offline status
- Supabase Realtime subscription on `nodes` table for live status updates
- Selecting a node filters repos and sessions
- Health: nodes with `last_heartbeat` > 60s ago marked as `stale`

#### 5.3.4 Git Worktree Management (Agent-side)

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
| Terminal hosting | ConPTY | Full ANSI/VT100: colors, progress bars, cursor movement |
| Buffer design | Circular raw byte array | Fixed memory, O(1) writes. No ANSI line parsing - let xterm.js handle it |
| Network model | Agent pushes outbound | No NAT/firewall issues. Agent initiates all connections to Supabase |
| Realtime relay | Supabase Realtime channels | Agent publishes, dashboard subscribes. Bidirectional via command queue |
| Input model | Bidirectional raw bytes | xterm.js keystrokes + PromptBar convenience. Supports tool approval, Ctrl+C |
| Dashboard | Vite + React + TypeScript | Fast builds, simple config, deploys to Vercel |
| Supabase role | Hot (Realtime) + Cold (Postgres/Storage) | Realtime is natural relay for agent-push; deviation from PRD "cold only" |
| Windows Service | `UseWindowsService()` | Single codebase runs as console (dev) or service (prod) |

---

## 7. Important Design Notes

### 7.1 Supabase Realtime Considerations

- Message size limit: ~256KB per broadcast message
- Agent must chunk large terminal outputs
- Batch terminal bytes every 50-100ms to avoid flooding
- Progress bars updating at 60fps will be throttled by batching (acceptable)

### 7.2 Process Lifecycle

- **Graceful shutdown**: Write `0x03` (Ctrl+C) to input pipe, wait 5s, then `TerminateProcess`
- **Process exit detection**: Background task monitors process handle via `WaitForSingleObject`. Auto-updates session status.
- **Orphan handling**: On agent startup, enumerate `claude.exe` processes. Cannot re-attach ConPTY to existing processes. Kill orphans or mark sessions as "lost."

### 7.3 claude CLI Modes

| Mode | Flags | Use Case |
|------|-------|----------|
| Interactive (default) | none | Full TUI in xterm.js. Progress bars, tool approvals. |
| Stream JSON | `--output-format stream-json --input-format stream-json` | Programmatic control (like VSCode). Structured output. |
| Non-interactive | `--print` or `--no-input` | One-shot commands, no TUI. |

The Director should default to interactive mode for the xterm.js experience. Stream JSON mode can be added later for API-driven orchestration.

### 7.4 Terminal Resize

When xterm.js resizes (browser window change, user resize):
1. Dashboard sends `{ cols, rows }` via command queue
2. Agent calls `ResizePseudoConsole(hPC, new COORD(cols, rows))`
3. Claude's TUI re-renders for the new dimensions

### 7.5 Buffer Replay Strategy

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
| 1 | `dotnet run` | Agent starts on port 5100 |
| 2 | `POST /api/sessions` with repo path | claude.exe spawns (visible in tasklist) |
| 3 | Open `http://localhost:5100` | xterm.js test page renders |
| 4 | Connect WebSocket | Live terminal bytes stream to xterm.js |
| 5 | Type in xterm.js terminal | Keystrokes reach claude, responses appear |
| 6 | Resize browser | Terminal re-wraps correctly |
| 7 | `DELETE /api/sessions/{id}` | claude.exe process terminated |
| 8 | Kill claude externally | Session status auto-updates to "exited" |
| 9 | `GET /api/sessions/{id}/buffer` | Returns raw terminal history bytes |
| 10 | Install as Windows Service | Auto-starts on reboot |

### Phase 2 (Cloud Bridge)

| # | Test | Expected Result |
|---|------|-----------------|
| 1 | Start agent | Node appears in Supabase `nodes` table |
| 2 | Create session | Realtime channel receives terminal bytes |
| 3 | Subscribe from test client | Live output streams via Supabase |
| 4 | Insert command in `command_queue` | Agent executes it |
| 5 | End session | Transcript archived in Supabase Storage |
| 6 | Hit Vercel API route | Reads/writes Supabase correctly |

### Phase 3 (Full Dashboard)

| # | Test | Expected Result |
|---|------|-----------------|
| 1 | Open Vercel dashboard | Node list populates from Supabase |
| 2 | Select session | xterm.js renders from Realtime subscription |
| 3 | Type in prompt bar | Command flows: Dashboard -> Vercel -> Supabase -> Agent |
| 4 | Run agents on 2 machines | Both appear and are switchable |
| 5 | Disconnect agent | Dashboard shows node as offline |

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
