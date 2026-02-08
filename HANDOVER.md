# Handover: Session Activity Indicators via Claude Code Hooks + Named Pipes

## Current State (2026-02-08)

### What's Done
- **5 commits** on `main`, all clean:
  - Initial commit + project structure
  - Pure WPF restructure (removed web layer)
  - UTF-8 decoding fix, CSI private param parsing fix
  - Scroll regions, cursor visibility, alt screen buffer, BCE support
- **3 projects**: `CcDirector.Core`, `CcDirector.Wpf`, `CcDirector.Core.Tests`
- **21 tests** passing (13 buffer + 8 session manager)
- **Working terminal rendering**: ConPTY -> CircularTerminalBuffer -> AnsiParser -> WPF TerminalControl
- **Session management**: Create/kill sessions, sidebar ListBox, terminal attach/detach
- **IMPLEMENTATION.md** has been updated with the hooks/pipe design but contains some **incorrect assumptions** (see Critical Corrections below)

### What's NOT Done (The Task)
Colored session indicators in the left sidebar showing Claude's real-time state (Idle=green, Working=blue, WaitingForInput=amber, etc.) driven by Claude Code hooks writing structured events to a Windows Named Pipe.

---

## Critical Corrections to IMPLEMENTATION.md

The plan in IMPLEMENTATION.md was written **before** researching the actual Claude Code hooks API. The following assumptions are **wrong** and must be corrected:

### 1. Hooks receive JSON on stdin, NOT environment variables

IMPLEMENTATION.md says:
```batch
echo {"type":"response_end","pid":%PID%} > \\.\pipe\CC_ClaudeDirector
```

**WRONG.** There is no `%PID%` env var. Hooks receive a JSON object on **stdin** containing:
```json
{
  "session_id": "abc123",
  "transcript_path": "/path/to/transcript.jsonl",
  "cwd": "/current/working/directory",
  "hook_event_name": "Stop",
  ...event-specific fields...
}
```

### 2. Route by session_id, not PID

Since hooks provide `session_id` (Claude Code's own session ID), the EventRouter should map `session_id -> Session`, not `PID -> Session`. PID is still useful for the process exit monitor but is NOT the hook routing key.

### 3. You can't just `echo > \\.\pipe\Name` on Windows

Windows named pipes don't work like Unix FIFOs. You can't redirect to them with `echo >`. The hook scripts need to use **PowerShell** or a small compiled helper to write to the pipe. PowerShell example:

```powershell
$input_json = [Console]::In.ReadToEnd()
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", "CC_ClaudeDirector", [System.IO.Pipes.PipeDirection]::Out)
$pipe.Connect(2000)
$writer = New-Object System.IO.StreamWriter($pipe)
$writer.WriteLine($input_json)
$writer.Flush()
$pipe.Close()
```

Or better: compile a tiny .NET console app (`cc_hook_relay.exe`) that reads stdin and writes to the pipe, deployed alongside the WPF app.

### 4. Many more hook events are available than assumed

IMPLEMENTATION.md only mentions `Stop` and `Notification`. The actual hooks API has:

| Event | Useful For | Has Matcher? |
|-------|-----------|-------------|
| **Stop** | Detect "response finished" -> Idle | No |
| **Notification** | Detect permission/input prompts | Yes (`permission_prompt`, `idle_prompt`) |
| **PreToolUse** | Detect "about to use tool" -> Working | Yes (tool name regex) |
| **PostToolUse** | Detect "tool completed" | Yes (tool name regex) |
| **SessionStart** | Detect session startup | Yes (`startup`, `resume`) |
| **SessionEnd** | Detect session exit | Yes (`clear`, `logout`, etc.) |
| **UserPromptSubmit** | Detect user sent prompt -> Working | No |
| **SubagentStart/Stop** | Track subagent activity | Yes (agent type) |

### 5. Hooks use a matcher + hooks array structure

Not just a flat command. Correct schema:
```json
{
  "hooks": {
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "powershell -File C:/path/to/hook-relay.ps1",
            "async": true,
            "timeout": 5
          }
        ]
      }
    ],
    "Notification": [
      {
        "matcher": "permission_prompt",
        "hooks": [
          {
            "type": "command",
            "command": "powershell -File C:/path/to/hook-relay.ps1",
            "async": true,
            "timeout": 5
          }
        ]
      }
    ]
  }
}
```

### 6. Async hooks are ideal for this use case

Setting `"async": true` means the hook fires in the background without blocking Claude. This is exactly what we want - monitoring, not control.

---

## What Needs to Be Built

### Component 1: Hook Relay (the bridge)

**Option A (Recommended): PowerShell script**
- File: `src/CcDirector.Wpf/Hooks/hook-relay.ps1`
- Reads stdin JSON, writes it to `\\.\pipe\CC_ClaudeDirector`
- Simple, no compilation needed
- Slightly slower startup (~200ms for PowerShell)

**Option B: Compiled .NET console app**
- Small `cc_hook_relay.exe` that reads stdin, writes to named pipe
- Faster startup (~10ms), but needs to be built and deployed
- Could be a 4th project in the solution or just a single-file publish

### Component 2: Named Pipe Server (`src/CcDirector.Core/Pipes/`)

**`DirectorPipeServer.cs`**
- `NamedPipeServerStream` on `CC_ClaudeDirector`
- Async accept loop with multiple instances (many hooks may fire concurrently)
- Each client writes one JSON line and disconnects
- Reads the JSON, deserializes to `PipeMessage`, passes to `EventRouter`

**`PipeMessage.cs`**
- Deserializes the hook stdin JSON
- Key fields: `hook_event_name`, `session_id`, `message`, `notification_type`, `tool_name`
- Don't invent a new schema - just use what Claude Code hooks already send

**`EventRouter.cs`**
- Maps `session_id` (string from Claude Code) to `Session` (our Guid-based sessions)
- The mapping is established when a session is created (we know the Claude Code session_id from the first hook event, or from parsing the transcript path)
- Routes events to `Session.HandlePipeEvent()` which updates the state machine

### Component 3: Session State Machine (`src/CcDirector.Core/Sessions/`)

**`SessionState.cs`** - New enum (separate from existing `SessionStatus`):
```
Starting        -> Gray      (session just created)
Idle            -> Green     (Stop hook fired)
Working         -> Blue      (UserPromptSubmit or input sent)
WaitingForInput -> Amber     (Notification with notification_type=idle_prompt)
WaitingForPerm  -> Red       (Notification with notification_type=permission_prompt)
Exited          -> Dark Gray (process exit or SessionEnd hook)
```

**Update `Session.cs`**:
- Add `SessionState ActivityState` property (default: Starting)
- Add `string? ClaudeSessionId` property (set on first hook event)
- Add `event Action<SessionState, SessionState>? OnActivityStateChanged`
- Add `HandlePipeEvent(PipeMessage)` method with state transitions
- Transition to `Working` when `SendInput()`/`SendText()` is called

**Update `SessionManager.cs`**:
- Add `ConcurrentDictionary<string, Guid> _claudeSessionMap` for session_id -> our Guid
- Method to register/lookup by Claude session_id

### Component 4: Hook Installer (`src/CcDirector.Core/Hooks/`)

**`HookInstaller.cs`**:
- Reads `~/.claude/settings.json`
- Merges Director hooks into existing config (preserve user hooks!)
- Backs up original settings before modifying
- Hooks to install: `Stop`, `Notification`, `UserPromptSubmit`, `SessionStart`, `SessionEnd`
- All hooks use `"async": true` (monitoring only, never block Claude)
- All hooks call the relay script/exe with the pipe name

**Key hooks config to install:**
```json
{
  "Stop": [{"hooks": [{"type": "command", "command": "..relay..", "async": true, "timeout": 5}]}],
  "Notification": [{"hooks": [{"type": "command", "command": "..relay..", "async": true, "timeout": 5}]}],
  "UserPromptSubmit": [{"hooks": [{"type": "command", "command": "..relay..", "async": true, "timeout": 5}]}],
  "SessionStart": [{"hooks": [{"type": "command", "command": "..relay..", "async": true, "timeout": 5}]}],
  "SessionEnd": [{"hooks": [{"type": "command", "command": "..relay..", "async": true, "timeout": 5}]}]
}
```

### Component 5: WPF UI Updates (`src/CcDirector.Wpf/`)

**`SessionViewModel` in `MainWindow.xaml.cs`**:
- Implement `INotifyPropertyChanged`
- Add `ActivityBrush` property (returns SolidColorBrush based on `Session.ActivityState`)
- Subscribe to `Session.OnActivityStateChanged`, dispatch to UI thread, raise PropertyChanged

**Color mapping:**
```csharp
Starting        -> new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))  // Gray
Idle            -> new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E))  // Green
Working         -> new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6))  // Blue
WaitingForInput -> new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B))  // Amber
WaitingForPerm  -> new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))  // Red
Exited          -> new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51))  // Dark Gray
```

**`MainWindow.xaml` ListBox ItemTemplate** (currently lines 50-57):
- Wrap existing StackPanel in a Border with `BorderBrush="{Binding ActivityBrush}"` and `BorderThickness="4,0,0,0"`

**`App.xaml.cs`**:
- Create and start `DirectorPipeServer` in `OnStartup`
- Create `EventRouter` and wire to `SessionManager`
- Run `HookInstaller` to configure Claude Code hooks
- Stop pipe server and optionally restore hooks in `OnExit`

---

## Suggested Build Order

1. **SessionState enum + Session changes** - Add `ActivityState`, event, `HandlePipeEvent()`. Quick, no new files needed for the enum (put it in Session.cs or a new file). Write tests.

2. **PipeMessage + DirectorPipeServer** - Get the pipe server listening and deserializing JSON. Write integration test that connects a `NamedPipeClientStream` and sends test JSON.

3. **EventRouter** - Wire pipe messages to sessions. Test with mock sessions.

4. **Hook relay script** - Write the PowerShell script. Test manually: `echo '{"hook_event_name":"Stop","session_id":"test"}' | powershell -File hook-relay.ps1`

5. **HookInstaller** - Read/merge/write `settings.json`. Be very careful to preserve existing user config.

6. **WPF UI** - Add INotifyPropertyChanged to SessionViewModel, colored border to XAML, wire up events. This is the visible payoff.

7. **Wire everything in App.xaml.cs** - Start pipe server, create router, install hooks on startup.

---

## Key Files to Read Before Starting

| File | Path | Why |
|------|------|-----|
| Session.cs | `src/CcDirector.Core/Sessions/Session.cs` | Add state machine here |
| SessionManager.cs | `src/CcDirector.Core/Sessions/SessionManager.cs` | Add session_id mapping |
| MainWindow.xaml | `src/CcDirector.Wpf/MainWindow.xaml` | Add colored border to ListBox template |
| MainWindow.xaml.cs | `src/CcDirector.Wpf/MainWindow.xaml.cs` | SessionViewModel needs INotifyPropertyChanged |
| App.xaml.cs | `src/CcDirector.Wpf/App.xaml.cs` | Wire up pipe server + hook installer on startup |
| App.xaml | `src/CcDirector.Wpf/App.xaml` | Resource brushes defined here |
| IMPLEMENTATION.md | Root | Full design doc (but see corrections above!) |

## Existing Code Patterns to Follow

- **No web dependencies** - Pure WPF + .NET 9 (`net9.0-windows`). No ASP.NET, no WebSocket, no IHostedService.
- **`AgentOptions`** takes `ClaudePath`, `DefaultBufferSizeBytes`, `GracefulShutdownTimeoutSeconds`
- **Logging**: `Action<string>?` callback pattern (see SessionManager constructor)
- **ConPTY input**: Uses `\r` (CR) not `\n` (LF)
- **Tests**: xUnit in `CcDirector.Core.Tests`, async tests with `await Task.Delay`

## Gotchas

1. **IMPLEMENTATION.md says .NET 10** but actual code is **.NET 9**. Use .NET 9.
2. **IMPLEMENTATION.md says `CcDirector.Agent`** but actual project is **`CcDirector.Core`** + **`CcDirector.Wpf`**.
3. **The WPF app may be running** - builds will fail to copy the exe. Close it first or check.
4. **Named pipe on Windows**: Use `NamedPipeServerStream("CC_ClaudeDirector", PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances, ...)` - the `\\.\pipe\` prefix is added automatically by .NET.
5. **Claude Code hooks snapshot at session start** - if you modify `settings.json` while Claude is running, existing sessions won't pick up the change. New sessions will.
6. **Preserve existing user hooks** - The HookInstaller MUST merge, not overwrite. Read existing `settings.json`, add Director hooks alongside any user-defined hooks.
7. **CircularTerminalBuffer** gotcha: When `_totalWritten == _capacity` and `_writeHead == 0`, use `<` not `<=` in the wrap check.
