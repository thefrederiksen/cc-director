# CC Director

A desktop application for managing multiple [Claude Code](https://docs.anthropic.com/en/docs/claude-code) sessions simultaneously. Run, monitor, and switch between independent Claude Code instances — each working on its own repository — from a single unified interface.

> **Mac/Linux Support (Experimental):** Cross-platform backend support has been added but needs testing. See [Help Wanted: Mac Testers](#help-wanted-mac-testers) below.

![CC Director](images/cc-director-main.png)

## Download (Windows)

**[Download cc-director.exe](releases/cc-director.exe)** - Pre-built Windows executable (no build required)

Requires [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) installed on your machine.

*Mac/Linux users: Build from source (see [Building](#building)). GUI not yet available — backend only.*

## Features

### Multi-Session Management
- Run multiple Claude Code sessions side-by-side, each in its own embedded console
- Switch between sessions instantly from the sidebar
- Drag-and-drop to reorder sessions
- Name and color-code sessions for easy identification
- Right-click context menu: Rename, Open in Explorer, Open in VS Code, Close

### Embedded Console
- Claude Code runs in a native Windows console window overlaid directly onto the WPF application
- Full interactive terminal — no emulation, no limitations
- Send prompts from a dedicated input bar at the bottom (Ctrl+Enter to submit)

### Real-Time Activity Tracking
- Monitors each session's state in real-time: **Idle**, **Working**, **Waiting for Input**, **Waiting for Permission**, **Exited**
- Color-coded status indicators on each session in the sidebar
- Powered by Claude Code's hook system — every tool call, prompt, and notification is captured

### Session Persistence
- Sessions survive app restarts — CC Director reconnects to running Claude processes on launch
- "Reconnect" button scans for orphaned `claude.exe` processes and reclaims them
- Recent sessions are remembered with their custom names and colors

### Git Integration
- **Source Control tab** shows staged and unstaged changes for the active session's repository
- File tree with status indicators (Modified, Added, Deleted, Renamed, etc.)
- Current branch display with ahead/behind sync status
- Click a file to open it in VS Code

### Repository Management
- **Repositories tab** for registering, cloning, and initializing Git repositories
- Clone from URL or browse your GitHub repos
- Quick-launch a new session from any registered repository

### Hook Integration
- Automatically installs hooks into Claude Code's `~/.claude/settings.json`
- Captures 14 hook event types: session start/end, tool use, notifications, subagent activity, task completion, and more
- Named pipe IPC (`CC_ClaudeDirector`) for fast, async event delivery
- Optional pipe message log panel (toggle from sidebar) for debugging and observability

### Logging & Diagnostics
- File logging to `%LOCALAPPDATA%\CcDirector\logs\`
- "Open Logs" button in the sidebar for quick access

## Architecture

```
CcDirector.sln
├── CcDirector.Core        # Session management, hooks, pipes, git, config (no UI dependencies)
├── CcDirector.Wpf         # WPF desktop application
└── CcDirector.Core.Tests  # xUnit test suite
```

**How it works:**

1. CC Director spawns Claude Code with a pseudo-terminal (ConPTY on Windows, PTY on Mac/Linux)
2. A relay script is installed as a Claude Code hook — it forwards hook events (JSON) over IPC
3. An IPC server inside CC Director receives events, routes them to the correct session, and updates the activity state
4. The UI reflects state changes in real-time via data binding

```
                          Windows                              Mac/Linux
                          -------                              ---------
Claude Code ──hook──▶ PowerShell relay               Python relay script
                            │                                   │
                      Named pipe                         Unix domain socket
                      (CC_ClaudeDirector)              (~/.cc-director/director.sock)
                            │                                   │
                            └──────────────┬────────────────────┘
                                           ▼
                                     CC Director
                                           │
                               ┌───────────┴───────────┐
                           EventRouter          Session UI
                         (maps session_id)    (activity colors,
                                                status badges)
```

## Requirements

### Windows
- Windows 10/11
- .NET 10 SDK (or Desktop Runtime for pre-built exe)
- [Claude Code CLI](https://docs.anthropic.com/en/docs/claude-code) installed and available on PATH
- **Windows Console Host** as default terminal (not Windows Terminal — a warning dialog will guide you if needed)

### Mac/Linux (Experimental)
- macOS 12+ or Linux with glibc
- .NET 10 SDK
- [Claude Code CLI](https://docs.anthropic.com/en/docs/claude-code) installed and available on PATH
- Python 3 (for hook relay script)

## Building

```bash
dotnet build src/CcDirector.Wpf/CcDirector.Wpf.csproj
```

## Running

```bash
dotnet run --project src/CcDirector.Wpf/CcDirector.Wpf.csproj
```

Or open `CcDirector.sln` in Visual Studio and run the `CcDirector.Wpf` project.

## Running Tests

```bash
dotnet test src/CcDirector.Core.Tests/CcDirector.Core.Tests.csproj
```

## Configuration

Edit `src/CcDirector.Wpf/appsettings.json` to configure:

- **ClaudePath** — path to the `claude` executable (default: `"claude"`)
- **DefaultClaudeArgs** — CLI arguments passed to each session (default: `"--dangerously-skip-permissions"`)
- **Repositories** — seed list of repository paths to register on first launch

Session state and repository registry are persisted in `~/Documents/CcDirector/`.

## Help Wanted: Mac Testers

We've added experimental cross-platform support for macOS and Linux, but **we need help testing it** since we don't have regular access to Mac hardware.

### What's Been Implemented

| Component | Windows | Mac/Linux |
|-----------|---------|-----------|
| Terminal backend | ConPTY | Unix PTY (openpty) |
| IPC for hooks | Named pipes | Unix domain sockets |
| Hook relay | PowerShell | Python |
| UI | WPF | Avalonia (planned) |

The core backend (`CcDirector.Core`) is now cross-platform. The UI layer (`CcDirector.Wpf`) is Windows-only, but we plan to add an Avalonia UI for Mac/Linux.

### How to Help Test

1. **Clone and build on Mac:**
   ```bash
   git clone https://github.com/anthropics/cc-director.git
   cd cc-director
   dotnet build src/CcDirector.Core/
   ```

2. **Run the unit tests:**
   ```bash
   dotnet test src/CcDirector.Core.Tests/
   ```

3. **Test the Unix PTY manually** (if you're comfortable with C#):
   - The `UnixPtyBackend` should spawn processes with proper terminal emulation
   - The `UnixSocketServer` should accept connections at `~/.cc-director/director.sock`
   - The Python hook relay should send JSON to the socket

4. **Report issues:**
   - Open an issue with your macOS/Linux version, .NET version, and any error messages
   - Bonus points for stack traces and reproduction steps

### Known Limitations (Mac/Linux)

- **No GUI yet** — only the backend is cross-platform; UI requires Avalonia port
- **Embedded console mode** (`SessionBackendType.Embedded`) is Windows-only
- **Untested on Apple Silicon** — should work but needs verification

See [docs/plan-mac-support.md](docs/plan-mac-support.md) for the full implementation plan.

## License

MIT
