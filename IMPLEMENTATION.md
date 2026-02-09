# Claude Code Director - Implementation Document

## 1. Overview

Claude Code Director is a WPF desktop application that manages multiple `claude.exe` sessions on a local Windows machine. It uses ConPTY for terminal hosting, a named pipe for structured hook events, and a custom ANSI terminal renderer — all in a single .NET 10 WPF app.

**Two phases:**
- **Phase 1** — Local WPF application (current focus)
- **Phase 2** — Remote access via web dashboard (future, deferred)

---

## 2. Project Structure

```
D:\ReposFred\cc_director\
+-- cc_director.sln
+-- src/
|   +-- CcDirector.Core/                    Core logic, no UI dependency
|   |   +-- Configuration/
|   |   |   +-- AgentOptions.cs             Runtime settings (ClaudePath, buffer size, etc.)
|   |   |   +-- RepositoryConfig.cs         { Name, Path } for configured repos
|   |   +-- ConPty/
|   |   |   +-- NativeMethods.cs            P/Invoke for Win32 ConPTY APIs
|   |   |   +-- PseudoConsole.cs            ConPTY handle lifecycle (create, resize, dispose)
|   |   |   +-- ProcessHost.cs             Spawn claude.exe, async I/O loops, exit monitor
|   |   +-- Hooks/
|   |   |   +-- HookInstaller.cs            Install/uninstall hooks in ~/.claude/settings.json
|   |   |   +-- hook-relay.ps1              PowerShell: reads stdin JSON, writes to named pipe
|   |   +-- Memory/
|   |   |   +-- CircularTerminalBuffer.cs   Thread-safe circular byte buffer (2 MB default)
|   |   +-- Pipes/
|   |   |   +-- DirectorPipeServer.cs       Named pipe server on CC_ClaudeDirector
|   |   |   +-- PipeMessage.cs              Flat JSON model for all 14 hook event types
|   |   |   +-- EventRouter.cs             Maps Claude session_id -> Director Session, updates state
|   |   +-- Sessions/
|   |       +-- ActivityState.cs            Enum: Starting, Idle, Working, WaitingForInput, WaitingForPerm, Exited
|   |       +-- Session.cs                  Single session: ConPTY + buffer + process + state machine
|   |       +-- SessionManager.cs           Creates, tracks, kills sessions (ConcurrentDictionary)
|   +-- CcDirector.Wpf/                     WPF desktop application
|   |   +-- App.xaml / App.xaml.cs           Startup: loads config, creates services, installs hooks
|   |   +-- MainWindow.xaml / .xaml.cs       3-panel layout + SessionViewModel + PipeMessageViewModel
|   |   +-- NewSessionDialog.xaml / .xaml.cs Repo picker + folder browse dialog
|   |   +-- Controls/
|   |   |   +-- TerminalControl.cs          Custom WPF terminal renderer (DrawingVisual, 50ms poll)
|   |   +-- Helpers/
|   |   |   +-- AnsiParser.cs               VT100/ANSI parser -> TerminalCell grid + scrollback
|   |   |   +-- TerminalCell.cs             Single cell: char, fg, bg, bold, italic, underline
|   |   +-- appsettings.json                Agent options + Repositories list
|   +-- CcDirector.Core.Tests/              xUnit tests
|       +-- ActivityStateTests.cs           State machine transitions for all 14 hook events
|       +-- CircularTerminalBufferTests.cs  Buffer read/write/wrap/thread-safety
|       +-- DirectorPipeServerTests.cs      Pipe server message receipt
|       +-- EventRouterTests.cs             Session routing and auto-registration
|       +-- HookInstallerTests.cs           Install/uninstall/idempotency/backup
|       +-- SessionManagerTests.cs          Session lifecycle
```

---

## 3. claude.exe Reference

| Property | Value |
|----------|-------|
| Path | `claude` (in PATH) or configured via `Agent.ClaudePath` |
| Type | Native Windows PE32+ binary |
| Concurrency | Multiple instances run simultaneously |
| Mode | Interactive (default) — full TUI in terminal |

### 3.1 Hooks System

Claude Code supports 14 lifecycle hooks configured in `~/.claude/settings.json`:

| # | Hook Event | Fires When |
|---|------------|------------|
| 1 | `SessionStart` | Session begins or resumes |
| 2 | `UserPromptSubmit` | User submits a prompt |
| 3 | `PreToolUse` | Before a tool call |
| 4 | `PostToolUse` | After a tool call succeeds |
| 5 | `PostToolUseFailure` | After a tool call fails |
| 6 | `PermissionRequest` | Permission dialog appears |
| 7 | `Notification` | Notifications (idle, elicitation, etc.) |
| 8 | `SubagentStart` | Subagent spawned |
| 9 | `SubagentStop` | Subagent finishes |
| 10 | `Stop` | Claude finishes responding |
| 11 | `TeammateIdle` | Teammate about to go idle |
| 12 | `TaskCompleted` | Task marked completed |
| 13 | `PreCompact` | Before context compaction |
| 14 | `SessionEnd` | Session terminates |

Hooks receive JSON on stdin with fields like `session_id`, `hook_event_name`, `tool_name`, `cwd`, etc.

### 3.2 Named Pipe IPC

Windows named pipe `\\.\pipe\CC_ClaudeDirector` for structured communication:
- PowerShell relay script bridges Claude hooks (stdin JSON) to the named pipe
- Director reads events and routes them to the correct session
- Fire-and-forget: silent failure if Director is not running
- Zero-latency, no ports, pure Windows IPC

---

## 4. Architecture

### 4.1 Dual-Channel Design

Each Claude session has two complementary channels:

```
+------------------------------------------------------------------+
|                      CLAUDE SESSION                               |
|                                                                   |
|  DATA PLANE (ConPTY)              CONTROL PLANE (Named Pipe)     |
|  +---------------------------+    +---------------------------+   |
|  | Raw terminal bytes        |    | Structured JSON events    |   |
|  | ANSI colors, progress bars|    | All 14 hook event types   |   |
|  | Full visual fidelity      |    |                           |   |
|  |                           |    | Events:                   |   |
|  | Direction: bidirectional  |    |  - SessionStart/End       |   |
|  |   OUT: terminal output    |    |  - Stop, Notification     |   |
|  |   IN:  raw keystrokes     |    |  - PreToolUse, PostToolUse|   |
|  +---------------------------+    |  - PermissionRequest      |   |
|                                   |  - SubagentStart/Stop     |   |
|  Purpose:                         |  - and more...            |   |
|  - WPF terminal rendering         +---------------------------+   |
|  - Visual terminal experience                                     |
|                                   Purpose:                        |
|                                   - Session activity state        |
|                                   - Indicator colors              |
|                                   - Detect idle/working/waiting   |
+------------------------------------------------------------------+
```

### 4.2 Component Flow

```
claude.exe hooks --> hook-relay.ps1 --> Named Pipe --> DirectorPipeServer
                                                            |
                                                       EventRouter
                                                            |
                                                    Session.HandlePipeEvent()
                                                            |
                                                    ActivityState changed
                                                            |
                                                    WPF UI updates (color, status text)

claude.exe <--> ConPTY <--> ProcessHost <--> CircularTerminalBuffer
                                                            |
                                                    TerminalControl (50ms poll)
                                                            |
                                                    AnsiParser -> TerminalCell grid
                                                            |
                                                    DrawingVisual render
```

---

## 5. UI Layout

```
+-----------------------------------------------------------------------------------+
|  CLAUDE CODE DIRECTOR                                                              |
+----------+--------+-------------------------------+--------+----------------------+
| SESSIONS |        |  TERMINAL                     |        | PIPE MESSAGES         |
|----------|  <->   |  (custom WPF terminal control) |  <->   |----------------------|
| [*] repo1|        |                               |        | 14:32:01 Stop         |
| [~] repo2|        |  $ claude                     |        |   session: ab1234     |
| [!] repo3|        |  > Building solution...       |        |                       |
|          |        |  [################....] 80%    |        | 14:31:45 PreToolUse   |
|          |        |  Done. Tests passed.           |        |   session: ab1234     |
|          |        |  > _                           |        |   tool: Read          |
|          |        |                               |        |                       |
|          |        |                               |        | 14:31:30 UserPrompt   |
| [+New]   |        |                               |        |   session: ab1234     |
| [Kill]   |        |                               |        |                       |
|          |        |                               |        | [Clear]               |
+----------+--------+-------------------------------+--------+----------------------+
```

### 5.1 Session Activity Indicators

Session indicators are driven by hook events through the `ActivityState` state machine:

| State | Color | Indicator | Triggered By |
|-------|-------|-----------|-------------|
| Starting | Gray (#6B7280) | `[ ]` | Session created, claude.exe spawning |
| Idle | Green (#22C55E) | `[*]` | `SessionStart` hook |
| Working | Blue (#3B82F6) | `[~]` | `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `SubagentStart`, input sent |
| WaitingForInput | Green (#22C55E) | `[*]` | `Stop` hook (Claude finished, user's turn) |
| WaitingForPerm | Red (#EF4444) | `[!!]` | `PermissionRequest` hook or `Notification` with `permission_prompt` |
| Exited | Dark Gray (#374151) | `[x]` | `SessionEnd` hook or process exit |

**State machine transitions (Session.HandlePipeEvent):**

| Hook Event | -> ActivityState | Rationale |
|------------|-----------------|-----------|
| `SessionStart` | Idle | Session ready |
| `UserPromptSubmit` | Working | User sent a prompt |
| `PreToolUse` | Working | Claude is using a tool |
| `PostToolUse` | Working | Tool completed, still working |
| `PostToolUseFailure` | Working | Tool failed, still working |
| `PermissionRequest` | WaitingForPerm | Permission dialog shown (red) |
| `Notification` (permission_prompt) | WaitingForPerm | Permission via notification |
| `Notification` (other) | WaitingForInput | Notification shown |
| `SubagentStart` | Working | Subagent active |
| `SubagentStop` | Working | Subagent done, main agent continues |
| `TaskCompleted` | Working | Task done, still processing |
| `Stop` | WaitingForInput | Claude finished responding (user's turn) |
| `TeammateIdle` | *(no change)* | Not relevant to session state |
| `PreCompact` | *(no change)* | Not relevant to session state |
| `SessionEnd` | Exited | Session terminated |

### 5.2 Pipe Messages Panel

Right sidebar showing a live scrolling log of every hook event received through the named pipe:

- **Timestamp** (HH:mm:ss)
- **Event name** (color-coded: Stop=Green, Notification=Amber, UserPromptSubmit=Blue, PermissionRequest=Red, etc.)
- **Session ID** (short form)
- **Detail** (tool name, notification type, or message)
- **Max 500 messages** with FIFO removal
- **Clear button** to reset the log

---

## 6. Phase 1 — Local WPF Application

**Goal:** A fully functional local WPF application that starts/stops Claude Code sessions, shows their activity states, manages repositories, and provides visibility into git activity — all on a single Windows machine with no cloud dependencies.

### 6.1 Done

- [x] **ConPTY hosting** — Spawn claude.exe via Windows Pseudo Console with full ANSI/VT100 support
- [x] **Process lifecycle** — Start, monitor, gracefully shutdown (Ctrl+C then terminate), detect exit
- [x] **Circular terminal buffer** — 2 MB thread-safe ring buffer for raw terminal bytes
- [x] **WPF terminal renderer** — Custom `TerminalControl` using DrawingVisual, 50ms polling, keyboard input, scrollback (1000 lines)
- [x] **ANSI parser** — SGR colors, cursor movement, erase ops, scroll regions, alternate screen buffer, cursor visibility, UTF-8
- [x] **Named pipe server** — `CC_ClaudeDirector` pipe receives JSON hook events from PowerShell relay
- [x] **Hook installer** — Installs all 14 hooks in `~/.claude/settings.json` (idempotent, preserves user hooks, creates backups)
- [x] **Hook relay script** — PowerShell script bridges Claude stdin JSON to named pipe
- [x] **Event router** — Maps Claude `session_id` to Director `Session`, auto-registers on first event by matching cwd
- [x] **Activity state machine** — Full state transitions for all 14 hook events
- [x] **Session indicators** — Colored left border on session list items (green/blue/red/gray)
- [x] **3-panel WPF layout** — Sessions sidebar, terminal center, pipe messages right
- [x] **New session dialog** — Pick from configured repos or browse for folder
- [x] **Pipe messages panel** — Live scrolling log of all hook events with color-coding
- [x] **Configuration** — `appsettings.json` with Agent options and Repositories list
- [x] **Orphan detection** — `ScanForOrphans()` on startup detects leftover claude.exe processes
- [x] **Test suite** — xUnit tests for buffer, pipe server, event router, hook installer, activity state (53 tests)

### 6.2 To Do

#### 6.2.1 Repository Panel

Currently repos are just paths in `appsettings.json` used for the new-session dialog. Need a proper repo panel in the UI:

- **Repo list in sidebar** — Show all configured repositories with:
  - Repo name
  - Current branch
  - Active session count per repo (if any)
- **Branch display** — Run `git rev-parse --abbrev-ref HEAD` to show current branch per repo
- **Recent commits** — Show last N commits per repo (author, message, short hash, relative time)
- **Git status summary** — Modified/staged/untracked file counts

#### 6.2.2 Session-to-Repo Association

- Show which repo each session belongs to
- Group sessions by repo in the sidebar
- Show branch info in session details

#### 6.2.3 VS Code-Style Activity States

Refine the activity indicators to match VS Code's Claude Code status bar behavior:

- Pulsing/animated indicator for active states (Working, WaitingForPerm)
- Session status text showing what Claude is doing (e.g., "Using Read tool", "Waiting for permission")
- Tool name display when `PreToolUse` fires (show which tool Claude is using)
- Token/cost tracking if available from hook data

#### 6.2.4 Input/Prompt Bar

- **Bottom prompt bar** for sending text to the active session
- Highlight prompt bar red/amber when session is in `WaitingForPerm` / `WaitingForInput`
- Show the permission request details (what tool, what file)
- Quick-reply buttons: **[Yes]** **[No]** **[Always Allow]** for permission prompts

#### 6.2.5 Session Management Polish

- Ability to rename sessions
- Session history (list of recently closed sessions)
- Multi-session operations (kill all, restart)
- Auto-restart option for crashed sessions
- Session logs/transcript export

#### 6.2.6 Configuration UI

- Settings dialog to edit Agent options (ClaudePath, buffer size, etc.)
- Add/remove/edit repositories without editing appsettings.json manually
- Configure default Claude args per repo

---

## 7. Phase 2 — Remote Access (Deferred)

**Goal:** Connect the local Director to the cloud so it can be monitored and controlled from a web browser on any device.

Phase 2 is **not in scope** for current development. It will involve:

- Web dashboard (React + xterm.js) hosted on Vercel
- Supabase for real-time relay (terminal bytes, hook events) and storage (session transcripts)
- Agent-push model: local Director pushes all data outbound, no inbound ports needed
- Multi-node support: manage Directors on multiple machines from one dashboard
- Authentication and authorization

Phase 2 design will be detailed when Phase 1 is complete and stable.

---

## 8. Technical Details

### 8.1 ConPTY Wrapper

**NativeMethods.cs** — P/Invoke for:
- `CreatePseudoConsole` / `ResizePseudoConsole` / `ClosePseudoConsole`
- `CreatePipe`, `InitializeProcThreadAttributeList`, `UpdateProcThreadAttribute`
- `CreateProcess` with `EXTENDED_STARTUPINFO_PRESENT`

**PseudoConsole.cs** — Handle lifecycle:
- Constructor: creates input/output pipes, then CreatePseudoConsole
- `Resize(short cols, short rows)` — calls ResizePseudoConsole
- IDisposable: closes handles

**ProcessHost.cs** — Process management:
- Spawns claude.exe with ConPTY attribute
- Async drain loop: reads output pipe into CircularTerminalBuffer (8KB chunks)
- Write method: sends bytes to input pipe
- Exit monitor: background task watches process handle
- Graceful shutdown: Ctrl+C (0x03), wait, then TerminateProcess

### 8.2 Session State Machine

```
                    SessionStart
                        |
                        v
  +--------+       +--------+       +------------------+
  |Starting| ----> |  Idle  | ----> |    Working       |
  +--------+       +--------+       |                  |
                        ^           | UserPromptSubmit  |
                        |           | PreToolUse        |
                   Stop |           | PostToolUse       |
                        |           | PostToolUseFailure|
                        |           | SubagentStart     |
                   +----+----+      | SubagentStop      |
                   |WaitFor  |      | TaskCompleted     |
                   |Input    | <----+------------------+
                   +---------+           |
                                         | PermissionRequest
                                         | Notification(perm)
                                         v
                                   +-----------+
                                   |WaitForPerm|
                                   +-----------+

                   SessionEnd (from any state)
                        |
                        v
                   +---------+
                   | Exited  |
                   +---------+
```

### 8.3 Named Pipe Protocol

Hook relay script (`hook-relay.ps1`) reads JSON from stdin and writes to pipe:
```powershell
$json = [Console]::In.ReadToEnd()
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", "CC_ClaudeDirector", "Out")
$pipe.Connect(2000)
$writer = New-Object System.IO.StreamWriter($pipe)
$writer.WriteLine($json.Trim())
$writer.Flush()
```

Hook configuration in `~/.claude/settings.json`:
```json
{
  "hooks": {
    "Stop": [{ "hooks": [{ "type": "command", "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"path\\hook-relay.ps1\"", "async": true, "timeout": 5 }] }],
    "SessionStart": [{ "hooks": [{ ... }] }],
    "PreToolUse": [{ "hooks": [{ ... }] }],
    ...all 14 events...
  }
}
```

### 8.4 Circular Buffer

- Fixed-size byte array (default 2 MB)
- Write head wraps around, keeps latest bytes
- `GetWrittenSince(position)` for incremental reads (used by terminal poll loop)
- `TotalBytesWritten` monotonic counter for stream position tracking
- Thread-safe via `ReaderWriterLockSlim`

### 8.5 Configuration

**appsettings.json:**
```json
{
  "Agent": {
    "ClaudePath": "claude",
    "DefaultBufferSizeBytes": 2097152,
    "GracefulShutdownTimeoutSeconds": 5
  },
  "Repositories": [
    { "Name": "my-repo", "Path": "D:\\Repos\\my-repo" }
  ]
}
```

---

## 9. Key Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| UI framework | WPF | Native Windows, no browser dependency, direct ConPTY integration |
| .NET version | .NET 10 | Latest, modern C# features |
| Terminal rendering | Custom DrawingVisual | Full control over ANSI rendering, no xterm.js dependency for local use |
| Event system | Named pipe + hooks | Zero-latency structured events from Claude lifecycle |
| Buffer design | Circular raw byte array | Fixed memory, O(1) writes, no ANSI parsing on write path |
| Session ID mapping | EventRouter auto-registration | Claude's `session_id` matched to Director sessions by `cwd` on first event |
| Hook installer | JsonNode tree manipulation | Preserves arbitrary user content in settings.json |
| Thread safety | ConcurrentDictionary + ReaderWriterLockSlim + Dispatcher | Multiple sessions, pipe events, and UI all on different threads |

---

## 10. Dependencies

### NuGet Packages
```
Microsoft.Extensions.Configuration.Json
Microsoft.Extensions.Configuration.Binder
```

### Runtime Requirements
- Windows 10+ (ConPTY support)
- .NET 10 SDK + runtime
- `claude.exe` installed and in PATH (or configured path)

---

## 11. Verification

### Phase 1 Tests

| # | Test | Expected Result |
|---|------|-----------------|
| 1 | Launch Director | WPF window opens, named pipe created, hooks installed |
| 2 | Click [+New Session] | Dialog shows configured repos, can browse for folder |
| 3 | Create session | claude.exe spawns, terminal output renders in center panel |
| 4 | Type in terminal | Keystrokes reach Claude, responses appear with ANSI colors |
| 5 | Claude responds | `Stop` hook fires, pipe message appears, indicator changes |
| 6 | Claude uses a tool | `PreToolUse`/`PostToolUse` hooks fire, indicator stays blue (Working) |
| 7 | Permission prompt | `PermissionRequest` hook fires, indicator turns red |
| 8 | Multiple sessions | Each session has independent state indicator |
| 9 | Kill session | claude.exe terminates, indicator goes dark gray |
| 10 | Close and reopen | Hooks still installed, pipe server restarts cleanly |
| 11 | `dotnet test` | All tests pass (except known flaky cmd.exe timing test) |

---

## 12. Research Notes

### Console Hosting Approaches Tried

| Approach | Result | Issue |
|----------|--------|-------|
| **ConPTY + custom renderer** | IO works, ANSI parsing works | Full TUI rendering had gaps — Claude Code's complex TUI (progress bars, multi-pane layouts) didn't render perfectly in the custom DrawingVisual renderer |
| **Pipe mode** | IO works perfectly | No TUI at all — Claude Code outputs plain text without its interactive interface |
| **Embedded console (SetParent)** | TUI renders correctly | Keyboard input broken — `SetParent` + `WS_CHILD` changes window message routing so keystrokes never reach the console |
| **Overlay console (current)** | TUI renders, keyboard works | Console is a separate top-level window positioned over the WPF terminal area |

### Win32 Findings

- **Getting the console HWND:** `AttachConsole(pid)` + `GetConsoleWindow()` returns the conhost window handle. Must `FreeConsole()` after to detach.
- **`WS_CHILD` breaks input:** Setting `WS_CHILD` style and reparenting with `SetParent` prevents keyboard messages from being dispatched to the console's message loop.
- **Overlay approach:** Keep the console as a top-level window, strip `WS_CAPTION` / `WS_THICKFRAME` for borderless look, set `WS_EX_TOOLWINDOW` to hide from taskbar/alt-tab, then use `MoveWindow` to position it over the WPF `TerminalArea` border.
- **`WriteConsoleInput`:** Injects `KEY_EVENT_RECORD` structs directly into the console input buffer — enables sending text from the WPF prompt box to the console without focus/keyboard issues.

### Overlay Approach — Verified Working (2026-02-09)

The overlay console approach is confirmed working:

- **TUI rendering:** Claude Code's full TUI renders correctly (progress bars, tool output, colored text, permission prompts)
- **Text input via prompt box:** `WriteConsoleInput` successfully injects text + Enter into Claude's prompt. Tested with multi-word commands.
- **Window positioning:** Console tracks the WPF `TerminalArea` border on move/resize
- **Session state:** Hook events flow through named pipe — activity indicators (Working/WaitingForInput/etc.) update correctly
- **Pipe messages panel:** All hook events visible in real-time (PreToolUse, PostToolUse, Stop, SubagentStart/Stop, UserPromptSubmit)
- **Direct keyboard input:** Clicking on the console overlay and typing works — keystrokes reach Claude natively

### Known Issues

- **Scrolling:** The console's native scrollbar appears but scrolling behavior is not intuitive — mouse wheel scrolling works but cannot easily scroll to end/bottom of output. Needs investigation (may relate to conhost scroll buffer behavior when `WS_CAPTION` is stripped, or may need scroll-to-bottom keybinding/button).
